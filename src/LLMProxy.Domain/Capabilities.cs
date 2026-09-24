using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMProxy.Domain;

[Flags]
public enum ModelCapability
{
    None = 0, Chat = 1, Tools = 2, JsonObject = 4, JsonSchema = 8, StrictTools = 16,
    RequiredTool = 32, NamedTool = 64, DisableParallelTools = 128, Seed = 256,
    MessageNames = 512, ImageInput = 1024, AudioInput = 2048, ReasoningEffort = 4096,
    ThinkingBudget = 8192, Embeddings = 16384, Responses = 32768,
    TextChat = Chat | Tools | JsonObject | JsonSchema | StrictTools | RequiredTool |
        NamedTool | DisableParallelTools | Seed | MessageNames
}

public sealed record ModelCapabilities
{
    public ModelCapability Features { get; init; } = ModelCapability.TextChat;
    public double MaxTemperature { get; init; } = 2;
    public double MinTopP { get; init; }
    public double MaxTopP { get; init; } = 1;
    public bool JsonWithTools { get; init; } = true;
    public bool MixedToolStrictness { get; init; } = true;
    public bool ThinkingWithTools { get; init; } = true;
    public bool ToolMessageNamesOnly { get; init; }
    public bool AcceptsResponseFormat { get; init; } = true;
    public int? ContextWindowTokens { get; init; }

    public bool Supports(LlmRequest request)
    {
        if ((Features & Required(request)) != Required(request)
            || request.Temperature > MaxTemperature || request.TopP < MinTopP || request.TopP > MaxTopP)
            return false;
        if (!AcceptsResponseFormat && request.ResponseFormat is { ValueKind: not JsonValueKind.Null }) return false;
        if (!ThinkingWithTools && request.ThinkingBudgetTokens is not null && request.Tools is { Count: > 0 }) return false;
        if (ToolMessageNamesOnly && request.Messages.Any(m => m.Name is not null && m.Role != "tool")) return false;
        if (!JsonWithTools && request.Tools is { Count: > 0 } && Format(request) is "json_object" or "json_schema")
            return false;
        if (!MixedToolStrictness && request.Tools?.Select(t => t.Function.Strict == true).Distinct().Count() > 1)
            return false;
        return true;
    }

    public static ModelCapability Required(LlmRequest request)
    {
        var result = request.Operation switch
        {
            GatewayOperation.Embeddings => ModelCapability.Embeddings,
            GatewayOperation.Responses => ModelCapability.Responses,
            _ => ModelCapability.Chat
        };
        if (request.Tools is { Count: > 0 }) result |= ModelCapability.Tools;
        if (request.Tools?.Any(t => t.Function.Strict == true) == true) result |= ModelCapability.StrictTools;
        if (Format(request) == "json_object") result |= ModelCapability.JsonObject;
        if (Format(request) == "json_schema") result |= ModelCapability.JsonSchema;
        if (request.ToolChoice is { ValueKind: JsonValueKind.String } choice && choice.GetString() == "required")
            result |= ModelCapability.RequiredTool;
        if (request.ToolChoice is { ValueKind: JsonValueKind.Object }) result |= ModelCapability.NamedTool;
        if (request.ParallelToolCalls == false) result |= ModelCapability.DisableParallelTools;
        if (request.Seed is not null) result |= ModelCapability.Seed;
        if (request.Messages.Any(m => m.Name is not null)) result |= ModelCapability.MessageNames;
        if (request.ReasoningEffort is not null) result |= ModelCapability.ReasoningEffort;
        if (request.ThinkingBudgetTokens is not null) result |= ModelCapability.ThinkingBudget;
        foreach (var message in request.Messages)
            if (message.Content is { ValueKind: JsonValueKind.Array } content)
                foreach (var part in content.EnumerateArray())
                    if (part.TryGetProperty("type", out var type))
                        result |= type.GetString() switch
                        {
                            "image_url" => ModelCapability.ImageInput,
                            "input_audio" => ModelCapability.AudioInput,
                            _ => ModelCapability.None
                        };
        return result | request.RequiredCapabilities;
    }

    private static string? Format(LlmRequest request) => request.ResponseFormat is { ValueKind: JsonValueKind.Object } format
        && format.TryGetProperty("type", out var type) ? type.GetString() : null;
}

public enum GatewayOperation { Chat, Embeddings, Responses }
public sealed record ProtocolEvent(string? Event, JsonObject Data);

public interface IProtocolProvider
{
    Task<JsonObject> CompleteProtocolAsync(GatewayOperation operation, JsonObject payload, CancellationToken cancellationToken);
    IAsyncEnumerable<ProtocolEvent> StreamProtocolAsync(GatewayOperation operation, JsonObject payload, CancellationToken cancellationToken);
}

public interface IProviderCatalog
{
    IReadOnlyList<ILlmProvider> Providers { get; }
}

public sealed record ConcurrencyScope(string Key, int Limit);
public interface IConcurrencyLimiter
{
    Task<IAsyncDisposable> AcquireAsync(IReadOnlyList<ConcurrencyScope> scopes, TimeSpan lifetime, CancellationToken cancellationToken);
}

public sealed record ModelPrice(decimal Input, decimal Output, decimal CachedInput, decimal CacheCreation)
{
    public decimal Calculate(TokenUsage usage)
    {
        var cached = Math.Clamp(usage.PromptTokensDetails?.CachedTokens ?? 0, 0, usage.InputTokens);
        var created = Math.Clamp(usage.PromptTokensDetails?.CacheCreationTokens ?? 0, 0, usage.InputTokens - cached);
        return decimal.Round((usage.InputTokens - cached - created) * Input + cached * CachedInput
            + created * CacheCreation + usage.OutputTokens * Output, 9, MidpointRounding.ToPositiveInfinity);
    }
}
