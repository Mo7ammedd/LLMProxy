using System.Security.Cryptography;
using LLMProxy.Domain;

namespace LLMProxy.Application;

public sealed class PriorityRouting : IRoutingStrategy
{
    public string Name => "priority";
    public Task<IReadOnlyList<ModelTarget>> OrderAsync(string model, IReadOnlyList<ModelTarget> targets,
        CancellationToken cancellationToken) => Task.FromResult(targets);
}

public sealed class FallbackRouting : IRoutingStrategy
{
    public string Name => "fallback";
    public Task<IReadOnlyList<ModelTarget>> OrderAsync(string model, IReadOnlyList<ModelTarget> targets,
        CancellationToken cancellationToken) => Task.FromResult(targets);
}

public sealed class RoundRobinRouting(IRoutingState state) : IRoutingStrategy
{
    public string Name => "round-robin";
    public async Task<IReadOnlyList<ModelTarget>> OrderAsync(string model, IReadOnlyList<ModelTarget> targets,
        CancellationToken cancellationToken)
    {
        var cursor = (ulong)await state.NextAsync(model, cancellationToken);
        var offset = (int)(cursor % (ulong)targets.Count);
        return Enumerable.Range(0, targets.Count).Select(i => targets[(offset + i) % targets.Count]).ToArray();
    }
}

public sealed class RandomRouting : IRoutingStrategy
{
    public string Name => "random";
    public Task<IReadOnlyList<ModelTarget>> OrderAsync(string model, IReadOnlyList<ModelTarget> targets,
        CancellationToken cancellationToken)
    {
        var shuffled = targets.ToArray();
        RandomNumberGenerator.Shuffle(shuffled.AsSpan());
        return Task.FromResult<IReadOnlyList<ModelTarget>>(shuffled);
    }
}

public sealed class ModelRouter : IModelRouter, IRoutePlanner
{
    private readonly IModelRegistry _registry;
    private readonly Dictionary<string, ILlmProvider> _providers;
    private readonly Dictionary<string, IRoutingStrategy> _strategies;
    private readonly IProviderCatalog? _catalog;

    public ModelRouter(IModelRegistry registry, IEnumerable<ILlmProvider> providers, IEnumerable<IRoutingStrategy> strategies,
        IProviderCatalog? catalog = null)
    {
        _registry = registry;
        _providers = providers.ToDictionary(x => x.Name, StringComparer.Ordinal);
        _strategies = strategies.ToDictionary(x => x.Name, StringComparer.Ordinal);
        _catalog = catalog;
        foreach (var model in registry.Models)
        {
            if (!_strategies.ContainsKey(model.Routing))
                throw new InvalidOperationException("An unregistered routing strategy is configured.");
            if (model.Targets.Any(x => _providers.GetValueOrDefault(x.Provider) is null))
                throw new InvalidOperationException("An unregistered provider is configured.");
        }
    }

    public async Task<ILlmProvider> SelectProviderAsync(string model, CancellationToken cancellationToken)
        => (await PlanAsync(model, cancellationToken))[0].Provider;

    public async Task<IReadOnlyList<ProviderRoute>> PlanAsync(string model, CancellationToken cancellationToken)
        => await PlanAsync(new LlmRequest { Model = model }, cancellationToken);

    public async Task<IReadOnlyList<ProviderRoute>> PlanAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Capture one catalog generation so account and model changes cannot split route planning.
        var snapshot = (_catalog as RuntimeCatalog)?.Current;
        var definition = (snapshot?.Registry ?? _registry).Get(request.Model);
        var providers = snapshot?.Providers.ToDictionary(x => x.Name, StringComparer.Ordinal)
            ?? _catalog?.Providers.ToDictionary(x => x.Name, StringComparer.Ordinal) ?? _providers;
        var available = definition.Targets.Where(x => providers.TryGetValue(x.Provider, out var provider) && provider.IsConfigured).ToArray();
        if (available.Length == 0)
            throw new GatewayException("No configured provider is available for this model.", "provider_unavailable", 503);
        var input = RequestValidator.EstimateInputTokens(request);
        var targets = available.Where(target => providers[target.Provider].Capabilities.Supports(request)
            && (target.Capabilities?.Supports(request) ?? true)
            && (target.Capabilities?.ContextWindowTokens is not { } window || input + request.OutputTokenLimit <= window)).ToArray();
        if (targets.Length == 0)
            throw new GatewayException("No configured target supports the requested features and context size.", "unsupported_model_capability", 400, "model");
        var ordered = await _strategies[definition.Routing].OrderAsync(request, targets, snapshot?.Pricing, cancellationToken);
        if (!definition.EnableFallback && definition.Routing != "fallback") ordered = ordered.Take(1).ToArray();
        return ordered.Select(target => new ProviderRoute(providers[target.Provider], target, snapshot?.Pricing)).ToArray();
    }
}
