using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public sealed class GeminiProvider(ProviderHttpTransport transport, ProviderOptions options) : ILlmProvider
{
    public string Name => "gemini";
    public bool IsConfigured => options.Gemini.IsConfigured;

    public async Task<LlmResponse> ChatCompletionAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request with { Stream = false }, cancellationToken);
        using var json = await ProviderJson.ReadAsync(response, cancellationToken);
        var root = json.RootElement;
        var candidate = root.Array("candidates").FirstOrDefault();
        if (candidate.ValueKind == JsonValueKind.Undefined && root.Object("promptFeedback").Text("blockReason") is null)
            throw new ProviderException("invalid_provider_response", false);
        var parts = candidate.Object("content").Array("parts").Where(x => !IsThought(x)).ToArray();
        var text = string.Concat(parts.Select(x => x.Text("text")));
        var calls = parts.Where(x => x.Object("functionCall").ValueKind == JsonValueKind.Object).Select(part =>
        {
            var call = part.Object("functionCall");
            return new ToolCall("call_" + Guid.NewGuid().ToString("N"), "function",
                new FunctionCall(call.Text("name") ?? "", Arguments(call)), ExtraContent(part));
        }).ToList();
        var message = ChatMessage.FromText("assistant", text.Length == 0 && calls.Count > 0 ? null : text)
            with
        { ToolCalls = calls.Count > 0 ? calls : null };
        return new LlmResponse(root.Text("responseId") ?? "", request.Model, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            [new ChatChoice(0, message, candidate.ValueKind == JsonValueKind.Undefined ? "content_filter"
                : FinishReason(candidate.Text("finishReason"), calls.Count > 0))], Usage(root));
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamChatCompletionAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request with { Stream = true }, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var toolIndex = 0;
        var started = false;
        var finished = false;
        var usage = TokenUsage.Zero;
        await foreach (var item in SseReader.ReadAsync(body, cancellationToken))
        {
            using var json = ProviderJson.Parse(item.Data);
            var root = json.RootElement;
            if (root.Object("error").ValueKind == JsonValueKind.Object) throw new ProviderException("provider_stream_error", true);
            if (!started)
            {
                started = true;
                yield return new LlmStreamChunk(new ChatDelta(Role: "assistant"));
            }
            if (root.Object("usageMetadata").ValueKind == JsonValueKind.Object) usage = Usage(root);
            if (root.Object("promptFeedback").Text("blockReason") is not null)
            {
                finished = true;
                yield return new LlmStreamChunk(FinishReason: "content_filter");
            }
            foreach (var candidate in root.Array("candidates"))
            {
                foreach (var part in candidate.Object("content").Array("parts").Where(x => !IsThought(x)))
                {
                    if (part.Text("text") is { } text) yield return new LlmStreamChunk(new ChatDelta(Content: text));
                    if (part.Object("functionCall") is { ValueKind: JsonValueKind.Object } call)
                    {
                        yield return new LlmStreamChunk(new ChatDelta(ToolCalls:
                            [new ToolCallDelta(toolIndex++, "call_" + Guid.NewGuid().ToString("N"), "function",
                                new FunctionDelta(call.Text("name"), Arguments(call)), ExtraContent(part))]));
                    }
                }
                if (candidate.Text("finishReason") is { } reason)
                {
                    finished = true;
                    yield return new LlmStreamChunk(FinishReason: FinishReason(reason, toolIndex > 0));
                }
            }
        }
        // Gemini closes the SSE response after the final candidate; it does not send [DONE].
        if (!finished) throw new ProviderException("incomplete_provider_stream", false);
        yield return new LlmStreamChunk(Usage: usage);
    }

    private Task<HttpResponseMessage> SendAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var method = request.Stream ? "streamGenerateContent?alt=sse" : "generateContent";
        return transport.SendAsync(Name, options.Gemini.Endpoint($"models/{Uri.EscapeDataString(request.Model)}:{method}"),
            BuildPayload(request), new Dictionary<string, string> { ["x-goog-api-key"] = options.Gemini.ApiKey }, request.Stream, cancellationToken);
    }

    internal static JsonObject BuildPayload(LlmRequest request)
    {
        ProviderJson.RejectUnsupported(request, supportsJson: true);
        if (request.ParallelToolCalls == false) ProviderJson.Unsupported("parallel_tool_calls");
        if (request.Messages.Any(x => x.Name is not null)) ProviderJson.Unsupported("messages.name");
        var contents = new JsonArray();
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in request.Messages.Where(x => x.Role is not ("system" or "developer")))
        {
            var parts = new JsonArray();
            if (message.Role == "tool")
            {
                if (message.ToolCallId is null || !callNames.TryGetValue(message.ToolCallId, out var name))
                    throw new GatewayException("Tool response has no matching function call.", "invalid_tool_call");
                JsonNode? result;
                try { result = JsonNode.Parse(message.Text()); }
                catch (JsonException) { result = JsonValue.Create(message.Text()); }
                parts.Add(new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = name,
                        ["response"] = result as JsonObject ?? new JsonObject { ["result"] = result }
                    }
                });
            }
            else
            {
                if (message.Text() is { Length: > 0 } text) parts.Add(new JsonObject { ["text"] = text });
                foreach (var call in message.ToolCalls ?? [])
                {
                    callNames[call.Id] = call.Function.Name;
                    var part = new JsonObject { ["functionCall"] = new JsonObject { ["name"] = call.Function.Name, ["args"] = JsonNode.Parse(call.Function.Arguments) } };
                    if (call.ExtraContent?.Object("google").Text("thought_signature") is { } signature) part["thoughtSignature"] = signature;
                    parts.Add(part);
                }
                if (parts.Count == 0) parts.Add(new JsonObject { ["text"] = "" });
            }
            var role = message.Role == "assistant" ? "model" : "user";
            if (contents.LastOrDefault() is JsonObject previous && previous["role"]?.GetValue<string>() == role)
                foreach (var part in parts) previous["parts"]!.AsArray().Add(part?.DeepClone());
            else contents.Add(new JsonObject { ["role"] = role, ["parts"] = parts });
        }
        var generation = new JsonObject { ["maxOutputTokens"] = request.OutputTokenLimit, ["candidateCount"] = 1 };
        if (request.Temperature is { } temperature) generation["temperature"] = temperature;
        if (request.TopP is { } topP) generation["topP"] = topP;
        if (request.Stop is { ValueKind: JsonValueKind.String } stop) generation["stopSequences"] = new JsonArray(stop.GetString());
        else if (request.Stop is { ValueKind: JsonValueKind.Array }) generation["stopSequences"] = ProviderJson.Node(request.Stop);
        if (request.ResponseFormat is { ValueKind: JsonValueKind.Object } format)
        {
            if (format.Text("type") is "json_object" or "json_schema") generation["responseMimeType"] = "application/json";
            else if (format.Text("type") != "text") ProviderJson.Unsupported("response_format");
            if (format.Text("type") == "json_schema") generation["responseJsonSchema"] = ProviderJson.Node(format.Object("json_schema").Object("schema"));
        }
        var payload = new JsonObject { ["contents"] = contents, ["generationConfig"] = generation };
        var system = string.Join("\n\n", request.Messages.Where(x => x.Role is "system" or "developer").Select(x => x.Text()));
        if (system.Length > 0) payload["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) };
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray(request.Tools.Select(tool => (JsonNode)new JsonObject
                {
                    ["name"] = tool.Function.Name,
                    ["description"] = tool.Function.Description,
                    ["parametersJsonSchema"] = ProviderJson.Node(tool.Function.Parameters) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                }).ToArray())
            });
            var choice = new JsonObject { ["mode"] = "AUTO" };
            if (request.ToolChoice is { ValueKind: JsonValueKind.String } selected)
                choice["mode"] = selected.GetString() switch { "none" => "NONE", "required" => "ANY", _ => "AUTO" };
            else if (request.ToolChoice is { ValueKind: JsonValueKind.Object } named)
            {
                choice["mode"] = "ANY";
                choice["allowedFunctionNames"] = new JsonArray(named.Object("function").Text("name"));
            }
            payload["toolConfig"] = new JsonObject { ["functionCallingConfig"] = choice };
        }
        return payload;
    }

    private static bool IsThought(JsonElement part) => part.Object("thought").ValueKind == JsonValueKind.True;
    private static string Arguments(JsonElement call) => call.Object("args").ValueKind == JsonValueKind.Undefined ? "{}" : call.Object("args").GetRawText();
    private static JsonElement? ExtraContent(JsonElement part) => part.Text("thoughtSignature") is { } signature
        ? JsonSerializer.SerializeToElement(new { google = new { thought_signature = signature } }) : null;
    private static string FinishReason(string? reason, bool hasTools) => reason switch
    {
        "MAX_TOKENS" => "length",
        "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "RECITATION" => "content_filter",
        _ => hasTools ? "tool_calls" : "stop"
    };
    private static TokenUsage Usage(JsonElement root)
    {
        var usage = root.Object("usageMetadata");
        return TokenUsage.From(usage.Number("promptTokenCount"), usage.Number("candidatesTokenCount") + usage.Number("thoughtsTokenCount"));
    }
}
