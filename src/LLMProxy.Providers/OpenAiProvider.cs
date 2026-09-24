using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public abstract class OpenAiCompatibleProvider(ProviderHttpTransport transport, ProviderConnectionOptions connection) : ILlmProvider
{
    public abstract string Name { get; }
    public bool IsConfigured => connection.IsConfigured;
    protected virtual string TokenLimitParameter => "max_completion_tokens";
    protected virtual bool RequestStreamUsage => true;
    protected virtual bool SupportsDeveloperRole => true;
    protected virtual bool SupportsReasoningContent => false;
    protected virtual Uri Endpoint(LlmRequest request) => connection.Endpoint("chat/completions");
    protected virtual Dictionary<string, string> Headers() => new() { ["Authorization"] = "Bearer " + connection.ApiKey };
    protected virtual ValueTask<Dictionary<string, string>> HeadersAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(Headers());
    protected virtual string FinishReason(string reason) => reason;
    protected virtual TokenUsage? ReadUsage(JsonElement root) => root.Object("usage").ValueKind == JsonValueKind.Object
        ? ProviderJson.OpenAiUsage(root) : null;
    protected virtual List<ChatChoice>? ReadChoices(JsonElement root) => root.Object("choices").Deserialize<List<ChatChoice>>(LlmJson.Options);
    protected virtual ChatDelta? ReadDelta(JsonElement delta) => delta.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        ? null : delta.Deserialize<ChatDelta>(LlmJson.Options);

    public async Task<LlmResponse> ChatCompletionAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request with { Stream = false }, cancellationToken);
        using var json = await ProviderJson.ReadAsync(response, cancellationToken);
        var root = json.RootElement;
        if (root.Object("choices").ValueKind != JsonValueKind.Array) throw new ProviderException("invalid_provider_response", false);
        List<ChatChoice>? choices;
        try { choices = ReadChoices(root); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { throw new ProviderException("invalid_provider_response", false); }
        if (choices is not { Count: 1 } || choices[0].Message is null || string.IsNullOrEmpty(choices[0].FinishReason))
            throw new ProviderException("invalid_provider_response", false);
        choices[0] = choices[0] with
        {
            FinishReason = FinishReason(choices[0].Message.ToolCalls is { Count: > 0 } && choices[0].FinishReason == "stop"
                ? "tool_calls" : choices[0].FinishReason),
            Message = SupportsReasoningContent ? choices[0].Message : choices[0].Message with { ReasoningContent = null }
        };
        return new LlmResponse(root.Text("id") ?? "", request.Model, root.Number("created"), choices, ReadUsage(root) ?? TokenUsage.Zero);
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamChatCompletionAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request with { Stream = true }, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var finished = false;
        var sawTools = false;
        await foreach (var item in SseReader.ReadAsync(body, cancellationToken))
        {
            if (item.Data == "[DONE]")
            {
                if (!finished) throw new ProviderException("incomplete_provider_stream", false);
                yield break;
            }
            using var json = ProviderJson.Parse(item.Data);
            var root = json.RootElement;
            if (item.Event == "error" || root.Object("error").ValueKind == JsonValueKind.Object)
                throw new ProviderException("provider_stream_error", true);
            foreach (var choice in root.Array("choices"))
            {
                ChatDelta? delta;
                try { delta = ReadDelta(choice.Object("delta")); }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException)
                { throw new ProviderException("invalid_provider_response", false); }
                if (delta is not null && !SupportsReasoningContent) delta = delta with { ReasoningContent = null };
                sawTools |= delta?.ToolCalls is { Count: > 0 };
                var reason = choice.Text("finish_reason");
                finished |= reason is not null;
                yield return new LlmStreamChunk(delta, reason is null ? null : FinishReason(reason == "stop" && sawTools ? "tool_calls" : reason));
            }
            if (ReadUsage(root) is { } usage) yield return new LlmStreamChunk(Usage: usage);
        }
        throw new ProviderException("incomplete_provider_stream", false);
    }

    protected virtual JsonObject BuildPayload(LlmRequest request)
    {
        var payload = JsonSerializer.SerializeToNode(request, LlmJson.Options)!.AsObject();
        payload.Remove("max_tokens");
        payload.Remove("max_completion_tokens");
        payload[TokenLimitParameter] = request.OutputTokenLimit;
        foreach (var message in payload["messages"]!.AsArray())
        {
            if (message is not JsonObject item) continue;
            if (!SupportsReasoningContent) item.Remove("reasoning_content");
            if (!SupportsDeveloperRole && item["role"]?.GetValue<string>() == "developer") item["role"] = "system";
            if (message?["tool_calls"] is JsonArray calls)
                foreach (var call in calls) call?.AsObject().Remove("extra_content");
        }
        if (request.Stream && RequestStreamUsage) payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        else payload.Remove("stream_options");
        return payload;
    }

    private async Task<HttpResponseMessage> SendAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request);
        var headers = await HeadersAsync(cancellationToken);
        return await transport.SendAsync(Name, Endpoint(request), payload, headers, request.Stream, cancellationToken);
    }
}

public sealed class OpenAiProvider(ProviderHttpTransport transport, ProviderOptions options)
    : OpenAiCompatibleProvider(transport, options.OpenAI)
{
    public override string Name => "openai";
}

public sealed class AzureOpenAiProvider(ProviderHttpTransport transport, ProviderOptions options)
    : OpenAiCompatibleProvider(transport, options.AzureOpenAI)
{
    public override string Name => "azure";
    protected override Uri Endpoint(LlmRequest request) => options.AzureOpenAI.Endpoint(
        $"openai/deployments/{Uri.EscapeDataString(request.Model)}/chat/completions?api-version={Uri.EscapeDataString(options.AzureOpenAI.ApiVersion)}");
    protected override Dictionary<string, string> Headers() => new() { ["api-key"] = options.AzureOpenAI.ApiKey };
}
