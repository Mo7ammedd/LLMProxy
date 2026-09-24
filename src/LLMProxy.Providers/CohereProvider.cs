using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public sealed class CohereProvider(ProviderHttpTransport transport, ProviderOptions options) : ILlmProvider
{
    public string Name => "cohere";
    public bool IsConfigured => options.Cohere.IsConfigured;
    public ModelCapabilities Capabilities => ProviderCapabilities.For(Name);

    public async Task<LlmResponse> ChatCompletionAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request with { Stream = false }, cancellationToken);
        using var json = await ProviderJson.ReadAsync(response, cancellationToken);
        var root = json.RootElement;
        var reason = FinishReason(root.Text("finish_reason"));
        var native = root.Object("message");
        if (native.Text("role") != "assistant") throw new ProviderException("invalid_provider_response", false);
        var text = string.Concat(native.Array("content").Where(part => part.Text("type") == "text").Select(part => part.Text("text")));
        var calls = native.Array("tool_calls").Select(ReadToolCall).ToList();
        var message = ChatMessage.FromText("assistant", calls.Count > 0 && text.Length == 0 ? null : text)
            with
        { ToolCalls = calls.Count == 0 ? null : calls };
        return new LlmResponse(root.Text("id") ?? "", request.Model, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            [new ChatChoice(0, message, reason == "stop" && calls.Count > 0 ? "tool_calls" : reason)], Usage(root.Object("usage")));
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamChatCompletionAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request with { Stream = true }, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var toolIndices = new HashSet<int>();
        await foreach (var item in SseReader.ReadAsync(body, cancellationToken))
        {
            using var json = ProviderJson.Parse(item.Data);
            var root = json.RootElement;
            var delta = root.Object("delta");
            var message = delta.Object("message");
            switch (root.Text("type") ?? item.Event)
            {
                case "error":
                    throw new ProviderException("provider_stream_error", true);
                case "message-start":
                    yield return new LlmStreamChunk(new ChatDelta(Role: "assistant"));
                    break;
                case "content-delta":
                    if (message.Object("content").Text("text") is { } text)
                        yield return new LlmStreamChunk(new ChatDelta(Content: text));
                    break;
                case "tool-call-start":
                case "tool-call-delta":
                    var indexElement = root.Object("index");
                    if (indexElement.ValueKind != JsonValueKind.Number || !indexElement.TryGetInt32(out var index) || index < 0)
                        throw new ProviderException("invalid_provider_response", false);
                    var call = message.Object("tool_calls");
                    var function = call.Object("function");
                    var start = (root.Text("type") ?? item.Event) == "tool-call-start";
                    if (start ? !toolIndices.Add(index) || string.IsNullOrEmpty(call.Text("id")) || string.IsNullOrEmpty(function.Text("name"))
                        : !toolIndices.Contains(index)) throw new ProviderException("invalid_provider_response", false);
                    yield return new LlmStreamChunk(new ChatDelta(ToolCalls:
                        [new ToolCallDelta(index, start ? call.Text("id") : null, start ? "function" : null,
                            new FunctionDelta(start ? function.Text("name") : null, function.Text("arguments") ?? ""))]));
                    break;
                case "message-end":
                    var reason = FinishReason(delta.Text("finish_reason"));
                    yield return new LlmStreamChunk(FinishReason: reason == "stop" && toolIndices.Count > 0 ? "tool_calls" : reason,
                        Usage: Usage(delta.Object("usage")));
                    yield break;
            }
        }
        throw new ProviderException("incomplete_provider_stream", false);
    }

    private Task<HttpResponseMessage> SendAsync(LlmRequest request, CancellationToken cancellationToken) => transport.SendAsync(Name,
        options.Cohere.Endpoint("chat"), BuildPayload(request), new Dictionary<string, string>
        { ["Authorization"] = "Bearer " + options.Cohere.ApiKey }, request.Stream, cancellationToken, options.Cohere);

    private static JsonObject BuildPayload(LlmRequest request)
    {
        if (request.TopP is < 0.01 or > 0.99) ProviderJson.Unsupported("top_p");
        if (request.ParallelToolCalls == false) ProviderJson.Unsupported("parallel_tool_calls");
        if (request.Messages.Any(message => message.Name is not null)) ProviderJson.Unsupported("messages.name");
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var item = new JsonObject { ["role"] = message.Role == "developer" ? "system" : message.Role };
            if (message.Role == "tool")
            {
                item["tool_call_id"] = message.ToolCallId;
                item["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message.Text() });
            }
            else
            {
                if (message.Content is { ValueKind: not JsonValueKind.Null }) item["content"] = message.Text();
                if (message.ToolCalls is { Count: > 0 })
                    item["tool_calls"] = new JsonArray(message.ToolCalls.Select(call => (JsonNode)new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Function.Name, ["arguments"] = call.Function.Arguments }
                    }).ToArray());
            }
            messages.Add(item);
        }
        var payload = new JsonObject { ["model"] = request.Model, ["messages"] = messages, ["stream"] = request.Stream, ["max_tokens"] = request.OutputTokenLimit };
        if (request.Temperature is { } temperature) payload["temperature"] = temperature;
        if (request.TopP is { } topP) payload["p"] = topP;
        if (request.Seed is { } seed) payload["seed"] = seed;
        if (request.Stop is { ValueKind: JsonValueKind.String } stop) payload["stop_sequences"] = new JsonArray(stop.GetString());
        else if (request.Stop is { ValueKind: JsonValueKind.Array }) payload["stop_sequences"] = ProviderJson.Node(request.Stop);

        if (request.Tools is { Count: > 0 })
        {
            var tools = request.Tools;
            if (request.ToolChoice is { ValueKind: JsonValueKind.Object } named)
            {
                tools = tools.Where(tool => tool.Function.Name == named.Object("function").Text("name")).ToList();
                payload["tool_choice"] = "REQUIRED";
            }
            else if (request.ToolChoice is { ValueKind: JsonValueKind.String } selected && selected.GetString() != "auto")
                payload["tool_choice"] = selected.GetString() == "none" ? "NONE" : "REQUIRED";
            if (tools.Any(tool => tool.Function.Strict == true))
            {
                if (tools.Any(tool => tool.Function.Strict != true)) ProviderJson.Unsupported("tools.function.strict");
                payload["strict_tools"] = true;
            }
            payload["tools"] = new JsonArray(tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Function.Name,
                    ["description"] = tool.Function.Description,
                    ["parameters"] = ProviderJson.Node(tool.Function.Parameters) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                }
            }).ToArray());
        }

        if (request.ResponseFormat is { } format && format.Text("type") is "json_object" or "json_schema")
        {
            if (request.Tools is { Count: > 0 }) ProviderJson.Unsupported("response_format");
            var responseFormat = new JsonObject { ["type"] = "json_object" };
            if (format.Text("type") == "json_schema") responseFormat["json_schema"] = ProviderJson.Node(format.Object("json_schema").Object("schema"));
            payload["response_format"] = responseFormat;
        }
        return payload;
    }

    private static ToolCall ReadToolCall(JsonElement call)
    {
        var function = call.Object("function");
        if (string.IsNullOrEmpty(call.Text("id")) || string.IsNullOrEmpty(function.Text("name")) || function.Text("arguments") is null)
            throw new ProviderException("invalid_provider_response", false);
        return new ToolCall(call.Text("id")!, "function", new FunctionCall(function.Text("name")!, function.Text("arguments")!));
    }

    private static string FinishReason(string? reason) => reason switch
    {
        "COMPLETE" or "STOP_SEQUENCE" => "stop",
        "MAX_TOKENS" => "length",
        "TOOL_CALL" => "tool_calls",
        "ERROR" => throw new ProviderException("provider_stream_error", true),
        "TIMEOUT" => throw new ProviderException("provider_timeout", true, 504),
        _ => throw new ProviderException("invalid_provider_response", false)
    };

    private static TokenUsage Usage(JsonElement usage)
    {
        var tokens = usage.Object("tokens");
        if (tokens.ValueKind != JsonValueKind.Object) tokens = usage.Object("billed_units");
        return TokenUsage.From(tokens.Number("input_tokens"), tokens.Number("output_tokens"));
    }
}
