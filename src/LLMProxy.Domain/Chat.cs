using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMProxy.Domain;

public sealed record LlmRequest
{
    public string Model { get; init; } = "";
    public List<ChatMessage> Messages { get; init; } = [];
    public bool Stream { get; init; }
    public StreamOptions? StreamOptions { get; init; }
    public int? MaxTokens { get; init; }
    public int? MaxCompletionTokens { get; init; }
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public JsonElement? Stop { get; init; }
    public List<ToolDefinition>? Tools { get; init; }
    public JsonElement? ToolChoice { get; init; }
    public bool? ParallelToolCalls { get; init; }
    public JsonElement? ResponseFormat { get; init; }
    public int? Seed { get; init; }
    public string? User { get; init; }
    public int? N { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore] public int OutputTokenLimit => MaxCompletionTokens ?? MaxTokens ?? 1024;
}

public sealed record StreamOptions(bool IncludeUsage = false);

public sealed record ChatMessage
{
    public string Role { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public JsonElement? Content { get; init; }
    public string? Name { get; init; }
    public string? ToolCallId { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ReasoningContent { get; init; }

    public string Text() => Content?.ValueKind switch
    {
        JsonValueKind.String => Content.Value.GetString() ?? "",
        JsonValueKind.Array => string.Concat(Content.Value.EnumerateArray()
            .Select(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String ? text.GetString() : "")),
        _ => ""
    };

    public static ChatMessage FromText(string role, string? text) => new()
    {
        Role = role,
        Content = text is null ? null : JsonSerializer.SerializeToElement(text)
    };
}

public sealed record ToolDefinition(string Type, FunctionDefinition Function);
public sealed record FunctionDefinition(string Name, string? Description, JsonElement? Parameters, bool? Strict = null);
public sealed record ToolCall(string Id, string Type, FunctionCall Function, JsonElement? ExtraContent = null);
public sealed record FunctionCall(string Name, string Arguments);
public sealed record ToolCallDelta(int Index, string? Id = null, string? Type = null, FunctionDelta? Function = null, JsonElement? ExtraContent = null);
public sealed record FunctionDelta(string? Name = null, string? Arguments = null);
public sealed record ChatDelta(string? Role = null, string? Content = null, List<ToolCallDelta>? ToolCalls = null,
    string? ReasoningContent = null);

public sealed record TokenUsage(
    [property: JsonPropertyName("prompt_tokens")] long InputTokens,
    [property: JsonPropertyName("completion_tokens")] long OutputTokens,
    [property: JsonPropertyName("total_tokens")] long TotalTokens)
{
    public static TokenUsage Zero { get; } = new(0, 0, 0);
    public static TokenUsage From(long input, long output) => new(input, output, checked(input + output));
}

public sealed record ChatChoice(int Index, ChatMessage Message, string FinishReason);

public sealed record LlmResponse(string Id, string Model, long Created, List<ChatChoice> Choices, TokenUsage Usage)
{
    public string Object => "chat.completion";
}

// A usage-only event has no delta or finish reason. HTTP/SSE framing belongs to the API layer.
public sealed record LlmStreamChunk(ChatDelta? Delta = null, string? FinishReason = null, TokenUsage? Usage = null);

public static class LlmJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 32
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
