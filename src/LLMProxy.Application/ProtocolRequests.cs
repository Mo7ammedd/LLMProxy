using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Application;

public sealed record PreparedProtocol(LlmRequest Admission, JsonObject Payload);

public sealed class ProtocolRequests(GatewayOptions options, IModelRegistry registry)
{
    public PreparedProtocol Validate(GatewayOperation operation, JsonObject source, ApiKey key)
    {
        var payload = (JsonObject)source.DeepClone();
        var model = String(payload, "model") ?? throw Invalid("model");
        var definition = registry.Get(model);
        if (!key.Allows(model)) throw new GatewayException("This API key cannot access the requested model.", "model_not_allowed", 403);
        if (operation == GatewayOperation.Embeddings)
        {
            RejectUnknown(payload, ["model", "input", "dimensions", "encoding_format", "user"]);
            ValidateEmbeddingInput(payload["input"]);
            if (Integer(payload, "dimensions") is < 1 or > 65536) throw Invalid("dimensions");
            if (String(payload, "encoding_format") is not (null or "float" or "base64")) throw Invalid("encoding_format");
            return new(new LlmRequest
            {
                Model = model,
                Operation = operation,
                MaxTokens = 0,
                InputTokenEstimate = Encoding.UTF8.GetByteCount(payload["input"]!.ToJsonString()) + 256L
            }, payload);
        }
        if (operation != GatewayOperation.Responses) throw Invalid("operation");
        RejectUnknown(payload, ["model", "input", "instructions", "stream", "max_output_tokens", "temperature", "top_p", "tools",
            "tool_choice", "parallel_tool_calls", "text", "reasoning", "store", "include", "metadata", "truncation", "user",
            "safety_identifier", "prompt_cache_key", "background"]);
        if (Boolean(payload, "store") == true) throw Unsupported("store");
        if (Boolean(payload, "background") == true) throw Unsupported("background");
        payload["store"] = false;
        var requirements = ValidateResponseInput(payload["input"]);
        if (payload["instructions"] is not null) _ = String(payload, "instructions");
        var max = Integer(payload, "max_output_tokens") ?? Math.Min(options.Requests.DefaultMaxOutputTokens, definition.MaxOutputTokens);
        if (max < 1 || max > definition.MaxOutputTokens) throw Invalid("max_output_tokens");
        payload["max_output_tokens"] = max;
        var tools = new List<ToolDefinition>();
        if (payload["tools"] is not null)
        {
            if (payload["tools"] is not JsonArray array || array.Count > 128) throw Invalid("tools");
            foreach (var tool in array)
            {
                if (tool is not JsonObject item || String(item, "type") != "function") throw Unsupported("tools");
                var name = String(item, "name");
                if (name is not { Length: > 0 and <= 64 } || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) throw Invalid("tools.name");
                if (item["parameters"] is not (null or JsonObject)) throw Invalid("tools.parameters");
                tools.Add(new("function", new(name, String(item, "description"),
                    item["parameters"] is { } schema ? JsonSerializer.SerializeToElement(schema) : null, Boolean(item, "strict"))));
            }
        }
        JsonElement? format = null;
        if (payload["text"] is JsonObject text && text["format"] is JsonObject responseFormat)
        {
            var type = String(responseFormat, "type");
            if (type is not ("text" or "json_object" or "json_schema")) throw Invalid("text.format");
            if (type == "json_schema" && (responseFormat["schema"] is not JsonObject || String(responseFormat, "name") is not { Length: > 0 }))
                throw Invalid("text.format");
            format = JsonSerializer.SerializeToElement(responseFormat);
        }
        else if (payload["text"] is not (null or JsonObject)) throw Invalid("text");
        string? effort = null;
        if (payload["reasoning"] is JsonObject reasoning) effort = String(reasoning, "effort");
        else if (payload["reasoning"] is not null) throw Invalid("reasoning");
        if (effort is not (null or "none" or "minimal" or "low" or "medium" or "high" or "xhigh")) throw Invalid("reasoning.effort");
        var temperature = Number(payload, "temperature");
        var topP = Number(payload, "top_p");
        if (temperature is < 0 or > 2 || topP is < 0 or > 1) throw Invalid("temperature");
        JsonElement? toolChoice = null;
        if (payload["tool_choice"] is JsonObject named)
        {
            if (String(named, "type") != "function" || !tools.Any(t => t.Function.Name == String(named, "name"))) throw Invalid("tool_choice");
            toolChoice = JsonSerializer.SerializeToElement(new { type = "function", function = new { name = String(named, "name") } });
        }
        else if (payload["tool_choice"] is not null)
        {
            var choice = String(payload, "tool_choice");
            if (choice is not ("auto" or "none" or "required") || choice == "required" && tools.Count == 0) throw Invalid("tool_choice");
            toolChoice = JsonSerializer.SerializeToElement(choice);
        }
        return new(new LlmRequest
        {
            Model = model,
            Operation = operation,
            Stream = Boolean(payload, "stream") ?? false,
            MaxTokens = max,
            Temperature = temperature,
            TopP = topP,
            Tools = tools.Count > 0 ? tools : null,
            ToolChoice = toolChoice,
            ParallelToolCalls = Boolean(payload, "parallel_tool_calls"),
            ResponseFormat = format,
            ReasoningEffort = effort,
            RequiredCapabilities = requirements,
            InputTokenEstimate = Encoding.UTF8.GetByteCount(payload.ToJsonString()) + 256L
        }, payload);
    }

    private static void ValidateEmbeddingInput(JsonNode? input)
    {
        if (input is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0) return;
        if (input is not JsonArray array || array.Count == 0) throw Invalid("input");
        static bool Token(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var token) && token >= 0;
        if (array.Count <= 8192 && array.All(Token)) return;
        if (array.Count > 2048) throw Invalid("input");
        if (array.All(node => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)) return;
        if (array.All(node => node is JsonArray tokens && tokens.Count is > 0 and <= 8192 && tokens.All(Token))) return;
        throw Invalid("input");
    }

    private static ModelCapability ValidateResponseInput(JsonNode? input)
    {
        if (input is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0) return ModelCapability.None;
        if (input is not JsonArray array || array.Count is < 1 or > 256) throw Invalid("input");
        var features = ModelCapability.None;
        foreach (var node in array)
        {
            if (node is not JsonObject item) throw Invalid("input");
            var type = String(item, "type");
            if (type == "reasoning") continue;
            if (type == "function_call")
            {
                if (String(item, "call_id") is not { Length: > 0 } || String(item, "name") is not { Length: > 0 }
                    || String(item, "arguments") is null) throw Invalid("input.function_call");
                continue;
            }
            if (type == "function_call_output")
            {
                if (String(item, "call_id") is not { Length: > 0 }) throw Invalid("input.call_id");
                features |= ValidateResponseContent(item["output"]);
                continue;
            }
            if (type is not (null or "message")) throw Unsupported("input.type");
            if (String(item, "role") is not ("user" or "assistant" or "system" or "developer")) throw Invalid("input.role");
            features |= ValidateResponseContent(item["content"]);
        }
        return features;
    }

    private static ModelCapability ValidateResponseContent(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out _)) return ModelCapability.None;
        if (content is not JsonArray parts || parts.Count == 0) throw Invalid("input.content");
        var features = ModelCapability.None;
        foreach (var part in parts)
        {
            if (part is not JsonObject component) throw Invalid("input.content");
            switch (String(component, "type"))
            {
                case "input_text":
                case "output_text": _ = String(component, "text") ?? throw Invalid("input.content.text"); break;
                case "input_image":
                    if (component["file_id"] is not null) throw Unsupported("input.content.file_id");
                    var url = String(component, "image_url");
                    if (url is null || !RequestValidator.ValidImageUrl(url)) throw Invalid("input.content.image_url");
                    features |= ModelCapability.ImageInput;
                    break;
                default: throw Unsupported("input.content.type");
            }
        }
        return features;
    }

    internal static string? String(JsonObject obj, string key) => obj[key] is null ? null
        : obj[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : throw Invalid(key);
    internal static int? Integer(JsonObject obj, string key) => obj[key] is null ? null
        : obj[key] is JsonValue value && value.TryGetValue<int>(out var result) ? result : throw Invalid(key);
    internal static bool? Boolean(JsonObject obj, string key) => obj[key] is null ? null
        : obj[key] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : throw Invalid(key);
    private static double? Number(JsonObject obj, string key) => obj[key] is null ? null
        : obj[key] is JsonValue value && value.TryGetValue<double>(out var result) && double.IsFinite(result) ? result : throw Invalid(key);
    private static void RejectUnknown(JsonObject payload, string[] allowed)
    {
        foreach (var field in payload)
            if (!allowed.Contains(field.Key, StringComparer.Ordinal)) throw Unsupported(field.Key);
    }
    private static GatewayException Invalid(string parameter) => new("Invalid request parameter.", "invalid_request", param: parameter);
    private static GatewayException Unsupported(string parameter) => new("This parameter is not supported for this operation.", "unsupported_parameter", param: parameter);
}
