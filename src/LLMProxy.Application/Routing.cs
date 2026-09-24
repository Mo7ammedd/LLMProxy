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

    public ModelRouter(IModelRegistry registry, IEnumerable<ILlmProvider> providers, IEnumerable<IRoutingStrategy> strategies)
    {
        _registry = registry;
        _providers = providers.ToDictionary(x => x.Name, StringComparer.Ordinal);
        _strategies = strategies.ToDictionary(x => x.Name, StringComparer.Ordinal);
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
    {
        cancellationToken.ThrowIfCancellationRequested();
        var definition = _registry.Get(model);
        var targets = definition.Targets.Where(x => _providers[x.Provider].IsConfigured).ToArray();
        if (targets.Length == 0)
            throw new GatewayException("No configured provider is available for this model.", "provider_unavailable", 503);
        var ordered = await _strategies[definition.Routing].OrderAsync(model, targets, cancellationToken);
        if (!definition.EnableFallback && definition.Routing != "fallback") ordered = ordered.Take(1).ToArray();
        return ordered.Select(target => new ProviderRoute(_providers[target.Provider], target)).ToArray();
    }
}
