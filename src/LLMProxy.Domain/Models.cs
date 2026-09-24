namespace LLMProxy.Domain;

public sealed record ModelTarget(string Provider, string Model, ModelCapabilities? Capabilities = null);
public sealed record ModelDefinition(string Name, IReadOnlyList<ModelTarget> Targets, string Routing,
    bool EnableFallback, int RequestsPerMinute, int MaxOutputTokens, int MaxConcurrentRequests = 100);
public sealed record ProviderRoute(ILlmProvider Provider, ModelTarget Target, IModelPricing? Pricing = null);

public interface ILlmProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    ModelCapabilities Capabilities => new();
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
    ModelPrice GetPrice(string model, long inputTokens) => new(GetInputTokenPrice(model), GetOutputTokenPrice(model),
        GetInputTokenPrice(model), GetInputTokenPrice(model));
    ModelPrice GetReservationPrice(string model, long inputTokens) => GetPrice(model, inputTokens);
}

public interface IModelRouter
{
    Task<ILlmProvider> SelectProviderAsync(string model, CancellationToken cancellationToken);
}

public interface IRoutePlanner
{
    Task<IReadOnlyList<ProviderRoute>> PlanAsync(string model, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderRoute>> PlanAsync(LlmRequest request, CancellationToken cancellationToken)
        => PlanAsync(request.Model, cancellationToken);
}

public interface IRoutingStrategy
{
    string Name { get; }
    Task<IReadOnlyList<ModelTarget>> OrderAsync(string model, IReadOnlyList<ModelTarget> targets,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ModelTarget>> OrderAsync(LlmRequest request, IReadOnlyList<ModelTarget> targets,
        CancellationToken cancellationToken) => OrderAsync(request.Model, targets, cancellationToken);
    Task<IReadOnlyList<ModelTarget>> OrderAsync(LlmRequest request, IReadOnlyList<ModelTarget> targets,
        IModelPricing? pricing, CancellationToken cancellationToken) => OrderAsync(request, targets, cancellationToken);
}

public interface IRoutingState
{
    Task<long> NextAsync(string model, CancellationToken cancellationToken);
}
