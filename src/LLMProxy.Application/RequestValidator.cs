using System.Text;
using System.Text.Json;
using LLMProxy.Domain;

namespace LLMProxy.Application;

public sealed class RequestValidator(GatewayOptions options, IModelRegistry registry)
{
    public LlmRequest Validate(LlmRequest request, ApiKey key)
    {
        if (string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 128) Invalid("model", "A model is required.");
        var definition = registry.Get(request.Model);
        if (!key.Allows(request.Model)) throw new GatewayException("This API key cannot access the requested model.", "model_not_allowed", 403);
        if (request.Messages is not { Count: > 0 } || request.Messages.Count > options.Requests.MaxMessages)
            Invalid("messages", "Provide a nonempty messages array within the configured limit.");
        if (request.Extra is { Count: > 0 }) Invalid(request.Extra.Keys.First(), "This parameter is not supported by this gateway version.");
        if (request.N is not (null or 1)) Invalid("n", "Only n=1 is supported.");
        if (request.Temperature is < 0 or > 2 || request.TopP is < 0 or > 1
            || request.Temperature is { } temperature && !double.IsFinite(temperature)
            || request.TopP is { } topP && !double.IsFinite(topP)) Invalid("temperature", "Sampling parameters are outside their valid range.");
        if (request.MaxTokens is not null && request.MaxCompletionTokens is not null)
            Invalid("max_completion_tokens", "Specify only one output token limit.");
        var max = request.MaxCompletionTokens ?? request.MaxTokens ?? Math.Min(options.Requests.DefaultMaxOutputTokens, definition.MaxOutputTokens);
        if (max < 1 || max > definition.MaxOutputTokens) Invalid("max_tokens", "The output token limit exceeds this model's configured bounds.");
        if (request.StreamOptions is not null && !request.Stream) Invalid("stream_options", "stream_options requires stream=true.");
        if (request.Stop is { ValueKind: not (JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Array) }) Invalid("stop", "stop must be a string or array of strings.");
        if (request.Stop is { ValueKind: JsonValueKind.Array } stop && (stop.GetArrayLength() > 4 || stop.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)))
            Invalid("stop", "At most four stop strings are supported.");
        if (request.Tools is { Count: > 128 }) Invalid("tools", "At most 128 tools are supported.");
        foreach (var tool in request.Tools ?? [])
        {
            if (tool is null || tool.Type != "function" || tool.Function is null || !ValidName(tool.Function.Name)
                || tool.Function.Parameters is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) })
                Invalid("tools", "Tools must contain named functions with JSON object schemas.");
        }
        ValidateToolChoice(request);
        ValidateResponseFormat(request.ResponseFormat);
        var pendingTools = new HashSet<string>(StringComparer.Ordinal);
        var conversationStarted = false;
        foreach (var message in request.Messages!)
        {
            if (message is null || message.Role is not ("system" or "developer" or "user" or "assistant" or "tool"))
                Invalid("messages", "Invalid message role.");
            if (message!.Name is not null && !ValidName(message.Name)) Invalid("messages", "Invalid message name.");
            if (message.Role is "system" or "developer")
            {
                if (conversationStarted) Invalid("messages", "System and developer instructions must precede conversation messages.");
            }
            else conversationStarted = true;
            if (message.Content is { ValueKind: not (JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Null) })
                Invalid("messages", "Message content must be text.");
            if (message.Content is { ValueKind: JsonValueKind.Array } parts && parts.EnumerateArray().Any(p =>
                p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "text"
                || !p.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String))
                Invalid("messages", "Only text content parts are supported.");
            if (message.Role != "assistant" && message.ToolCalls is { Count: > 0 }) Invalid("messages", "Only assistant messages may contain tool_calls.");
            if (message.Role != "assistant" && message.ReasoningContent is not null)
                Invalid("messages", "Only assistant messages may contain reasoning_content.");
            if (message.Role == "tool")
            {
                if (message.ToolCallId is null || !pendingTools.Remove(message.ToolCallId)) Invalid("messages", "Tool results must reference an unresolved assistant tool call.");
            }
            else if (pendingTools.Count != 0) Invalid("messages", "Each tool call requires a result before the next conversation message.");
            foreach (var call in message.ToolCalls ?? [])
            {
                if (call is null || string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 128 || call.Type != "function"
                    || call.Function is null || !ValidName(call.Function.Name) || string.IsNullOrWhiteSpace(call.Function.Arguments)
                    || !pendingTools.Add(call.Id)) Invalid("messages", "Invalid or duplicate tool call.");
                try
                {
                    using var args = JsonDocument.Parse(call!.Function.Arguments);
                    if (args.RootElement.ValueKind != JsonValueKind.Object) Invalid("messages", "Tool arguments must be a JSON object.");
                }
                catch (JsonException) { Invalid("messages", "Tool arguments must be valid JSON."); }
            }
            if (message.Content is null or { ValueKind: JsonValueKind.Null } && message.ToolCalls is not { Count: > 0 })
                Invalid("messages", "Message content is required unless an assistant supplies tool calls.");
        }
        if (pendingTools.Count != 0) Invalid("messages", "All assistant tool calls require results.");
        if (!request.Messages!.Any(x => x.Role is "user" or "tool")) Invalid("messages", "At least one user or tool message is required.");
        return request.MaxCompletionTokens is not null ? request : request with { MaxTokens = max };
    }

    // UTF-8 byte count plus framing is deliberately conservative, and not a model tokenizer.
    public static long EstimateInputTokens(LlmRequest request)
    {
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request.Messages, LlmJson.Options));
        if (request.Tools is not null) bytes += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request.Tools, LlmJson.Options));
        return checked(bytes + request.Messages.Count * 16L + 256L);
    }

    private static bool ValidName(string? name) => name is { Length: > 0 and <= 64 }
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void ValidateToolChoice(LlmRequest request)
    {
        if (request.ToolChoice is not { ValueKind: not JsonValueKind.Null } choice) return;
        if (request.Tools is not { Count: > 0 }) Invalid("tool_choice", "tool_choice requires tools.");
        if (choice.ValueKind == JsonValueKind.String && choice.GetString() is "auto" or "none" or "required") return;
        if (choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String && type.GetString() == "function"
            && choice.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object
            && function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            && request.Tools!.Any(x => x.Function.Name == name.GetString())) return;
        Invalid("tool_choice", "Invalid tool_choice or unknown function.");
    }

    private static void ValidateResponseFormat(JsonElement? format)
    {
        if (format is null or { ValueKind: JsonValueKind.Null }) return;
        JsonElement type = default;
        if (format.Value.ValueKind != JsonValueKind.Object || !format.Value.TryGetProperty("type", out type)
            || type.ValueKind != JsonValueKind.String || type.GetString() is not ("text" or "json_object" or "json_schema"))
            Invalid("response_format", "response_format must specify text, json_object or json_schema.");
        if (type.GetString() == "json_schema"
            && (!format.Value.TryGetProperty("json_schema", out var schema) || schema.ValueKind != JsonValueKind.Object
                || !schema.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || !ValidName(name.GetString())
                || !schema.TryGetProperty("schema", out var definition) || definition.ValueKind != JsonValueKind.Object))
            Invalid("response_format", "json_schema requires a name and JSON object schema.");
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid(string param, string message) => throw new GatewayException(message, "invalid_request", param: param);
}
