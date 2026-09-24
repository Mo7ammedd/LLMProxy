using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public sealed class AnthropicProvider(ProviderHttpTransport transport, ProviderOptions options) : ILlmProvider
{
    public string Name => "anthropic";
    public bool IsConfigured => options.Anthropic.IsConfigured;

    public async Task<LlmResponse> ChatCompletionAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request with { Stream = false }, cancellationToken);
        using var json = await ProviderJson.ReadAsync(response, cancellationToken);
        var root = json.RootElement;
        if (root.Text("type") != "message") throw new ProviderException("invalid_provider_response", false);
        var blocks = root.Array("content").ToArray();
        var text = string.Concat(blocks.Where(x => x.Text("type") == "text").Select(x => x.Text("text")));
        var calls = blocks.Where(x => x.Text("type") == "tool_use").Select(x => new ToolCall(
            x.Text("id") ?? "", "function", new FunctionCall(x.Text("name") ?? "", x.Object("input").GetRawText()))).ToList();
        var message = ChatMessage.FromText("assistant", calls.Count > 0 && text.Length == 0 ? null : text)
            with
        { ToolCalls = calls.Count == 0 ? null : calls };
        return new LlmResponse(root.Text("id") ?? "", request.Model, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            [new ChatChoice(0, message, FinishReason(root.Text("stop_reason")))], Usage(root.Object("usage")));
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamChatCompletionAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request with { Stream = true }, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var usage = TokenUsage.Zero;
        var toolIndices = new Dictionary<long, int>();
        var finished = false;
        await foreach (var item in SseReader.ReadAsync(body, cancellationToken))
        {
            using var json = ProviderJson.Parse(item.Data);
            var root = json.RootElement;
            switch (root.Text("type") ?? item.Event)
            {
                case "error":
                    var errorType = root.Object("error").Text("type");
                    throw new ProviderException("provider_stream_error", errorType is "overloaded_error" or "rate_limit_error" or "api_error");
                case "message_start":
                    usage = Usage(root.Object("message").Object("usage"));
                    yield return new LlmStreamChunk(new ChatDelta(Role: "assistant"), Usage: usage);
                    break;
                case "content_block_start":
                    var block = root.Object("content_block");
                    if (block.Text("type") == "tool_use")
                    {
                        var toolIndex = toolIndices.Count;
                        toolIndices[root.Number("index")] = toolIndex;
                        yield return new LlmStreamChunk(new ChatDelta(ToolCalls:
                            [new ToolCallDelta(toolIndex, block.Text("id"), "function", new FunctionDelta(block.Text("name"), ""))]));
                    }
                    else if (block.Text("type") == "text" && block.Text("text") is { Length: > 0 } text)
                        yield return new LlmStreamChunk(new ChatDelta(Content: text));
                    break;
                case "content_block_delta":
                    var delta = root.Object("delta");
                    if (delta.Text("type") == "text_delta") yield return new LlmStreamChunk(new ChatDelta(Content: delta.Text("text")));
                    else if (delta.Text("type") == "input_json_delta")
                    {
                        if (!toolIndices.TryGetValue(root.Number("index"), out var index)) throw new ProviderException("invalid_provider_response", false);
                        yield return new LlmStreamChunk(new ChatDelta(ToolCalls:
                            [new ToolCallDelta(index, Function: new FunctionDelta(Arguments: delta.Text("partial_json")))]));
                    }
                    break;
                case "message_delta":
                    var update = root.Object("usage");
                    usage = TokenUsage.From(Math.Max(usage.InputTokens, Usage(update).InputTokens), update.Number("output_tokens"));
                    var reason = root.Object("delta").Text("stop_reason");
                    if (reason is not null)
                    {
                        finished = true;
                        yield return new LlmStreamChunk(FinishReason: FinishReason(reason), Usage: usage);
                    }
                    break;
                case "message_stop":
                    if (!finished) throw new ProviderException("incomplete_provider_stream", false);
                    yield return new LlmStreamChunk(Usage: usage);
                    yield break;
            }
        }
        throw new ProviderException("incomplete_provider_stream", false);
    }

    private Task<HttpResponseMessage> SendAsync(LlmRequest request, CancellationToken cancellationToken) => transport.SendAsync(Name,
        options.Anthropic.Endpoint("messages"), BuildPayload(request), new Dictionary<string, string>
        { ["x-api-key"] = options.Anthropic.ApiKey, ["anthropic-version"] = "2023-06-01" }, request.Stream, cancellationToken);

    internal static JsonObject BuildPayload(LlmRequest request)
    {
        ProviderJson.RejectUnsupported(request, supportsJson: false);
        if (request.Temperature > 1) ProviderJson.Unsupported("temperature");
        if (request.Messages.Any(x => x.Name is not null)) ProviderJson.Unsupported("messages.name");
        var messages = new JsonArray();
        foreach (var message in request.Messages.Where(x => x.Role is not ("system" or "developer")))
        {
            var blocks = new JsonArray();
            if (message.Role == "tool")
                blocks.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = message.ToolCallId, ["content"] = message.Text() });
            else
            {
                if (message.Text() is { Length: > 0 } text) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                foreach (var call in message.ToolCalls ?? []) blocks.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = call.Id,
                    ["name"] = call.Function.Name,
                    ["input"] = JsonNode.Parse(call.Function.Arguments)
                });
                if (blocks.Count == 0) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = "" });
            }
            var role = message.Role == "assistant" ? "assistant" : "user";
            // Anthropic accepts merged consecutive roles; merging also keeps parallel tool results together.
            if (messages.LastOrDefault() is JsonObject previous && previous["role"]?.GetValue<string>() == role)
                foreach (var block in blocks) previous["content"]!.AsArray().Add(block?.DeepClone());
            else messages.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
        }
        var payload = new JsonObject { ["model"] = request.Model, ["messages"] = messages, ["max_tokens"] = request.OutputTokenLimit, ["stream"] = request.Stream };
        var system = string.Join("\n\n", request.Messages.Where(x => x.Role is "system" or "developer").Select(x => x.Text()));
        if (system.Length > 0) payload["system"] = system;
        if (request.Temperature is { } temperature) payload["temperature"] = temperature;
        if (request.TopP is { } topP) payload["top_p"] = topP;
        if (request.Stop is { ValueKind: JsonValueKind.String } stop) payload["stop_sequences"] = new JsonArray(stop.GetString());
        else if (request.Stop is { ValueKind: JsonValueKind.Array }) payload["stop_sequences"] = ProviderJson.Node(request.Stop);
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = new JsonArray(request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["name"] = tool.Function.Name,
                ["description"] = tool.Function.Description,
                ["input_schema"] = ProviderJson.Node(tool.Function.Parameters) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            }).ToArray());
            var choice = new JsonObject { ["type"] = "auto" };
            if (request.ToolChoice is { ValueKind: JsonValueKind.String } selected)
                choice["type"] = selected.GetString() == "required" ? "any" : selected.GetString();
            else if (request.ToolChoice is { ValueKind: JsonValueKind.Object } named)
            {
                choice["type"] = "tool";
                choice["name"] = named.Object("function").Text("name");
            }
            if (request.ParallelToolCalls is { } parallel) choice["disable_parallel_tool_use"] = !parallel;
            payload["tool_choice"] = choice;
        }
        return payload;
    }

    private static string FinishReason(string? reason) => reason switch
    {
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        "refusal" => "content_filter",
        _ => "stop"
    };
    private static TokenUsage Usage(JsonElement usage) => TokenUsage.From(
        usage.Number("input_tokens") + usage.Number("cache_read_input_tokens") + usage.Number("cache_creation_input_tokens"), usage.Number("output_tokens"));
}
