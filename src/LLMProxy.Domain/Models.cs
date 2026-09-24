namespace LLMProxy.Domain;

public sealed record ModelTarget(string Provider, string Model);
public sealed record ModelDefinition(string Name, IReadOnlyList<ModelTarget> Targets, string Routing,
    bool EnableFallback, int RequestsPerMinute, int MaxOutputTokens);
public sealed record ProviderRoute(ILlmProvider Provider, ModelTarget Target);

public interface ILlmProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<LlmResponse> ChatCompletionAsync(LlmRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<LlmStreamChunk> StreamChatCompletionAsync(LlmRequest request, CancellationToken cancellationToken);
}

public interface IModelRegistry
{
    IReadOnlyList<ModelDefinition> Models { get; }
    ModelDefinition Get(string model);
}

public interface IModelPricing
{
    decimal GetInputTokenPrice(string model);
    decimal GetOutputTokenPrice(string model);
}

public interface IModelRouter
{
    Task<ILlmProvider> SelectProviderAsync(string model, CancellationToken cancellationToken);
}

public interface IRoutePlanner
{
    Task<IReadOnlyList<ProviderRoute>> PlanAsync(string model, CancellationToken cancellationToken);
}

public interface IRoutingStrategy
{
    string Name { get; }
    Task<IReadOnlyList<ModelTarget>> OrderAsync(string model, IReadOnlyList<ModelTarget> targets,
        CancellationToken cancellationToken);
}

public interface IRoutingState
{
    Task<long> NextAsync(string model, CancellationToken cancellationToken);
}
