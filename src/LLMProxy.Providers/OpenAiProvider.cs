using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public abstract class OpenAiCompatibleProvider(ProviderHttpTransport transport, ProviderConnectionOptions connection) : ILlmProvider
{
    public abstract string Name { get; }
    public bool IsConfigured => connection.IsConfigured;
    protected virtual Uri Endpoint(LlmRequest request) => connection.Endpoint("chat/completions");
    protected virtual Dictionary<string, string> Headers() => new() { ["Authorization"] = "Bearer " + connection.ApiKey };

    public async Task<LlmResponse> ChatCompletionAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request with { Stream = false }, cancellationToken);
        using var json = await ProviderJson.ReadAsync(response, cancellationToken);
        var root = json.RootElement;
        List<ChatChoice>? choices;
        try { choices = root.Object("choices").Deserialize<List<ChatChoice>>(LlmJson.Options); }
        catch (JsonException) { throw new ProviderException("invalid_provider_response", false); }
        if (choices is not { Count: 1 } || choices[0].Message is null || string.IsNullOrEmpty(choices[0].FinishReason))
            throw new ProviderException("invalid_provider_response", false);
        return new LlmResponse(root.Text("id") ?? "", request.Model, root.Number("created"), choices, ProviderJson.OpenAiUsage(root));
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamChatCompletionAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request with { Stream = true }, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var finished = false;
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
                try { delta = choice.Object("delta").Deserialize<ChatDelta>(LlmJson.Options); }
                catch (JsonException) { throw new ProviderException("invalid_provider_response", false); }
                var reason = choice.Text("finish_reason");
                finished |= reason is not null;
                yield return new LlmStreamChunk(delta, reason);
            }
            if (root.Object("usage").ValueKind == JsonValueKind.Object)
                yield return new LlmStreamChunk(Usage: ProviderJson.OpenAiUsage(root));
        }
        throw new ProviderException("incomplete_provider_stream", false);
    }

    private Task<HttpResponseMessage> SendAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToNode(request, LlmJson.Options)!.AsObject();
        payload.Remove("max_tokens");
        payload["max_completion_tokens"] = request.OutputTokenLimit;
        foreach (var message in payload["messages"]!.AsArray())
            if (message?["tool_calls"] is JsonArray calls)
                foreach (var call in calls) call?.AsObject().Remove("extra_content");
        if (request.Stream) payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        else payload.Remove("stream_options");
        return transport.SendAsync(Name, Endpoint(request), payload, Headers(), request.Stream, cancellationToken);
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
