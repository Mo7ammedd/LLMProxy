using System.Collections.Concurrent;
using LLMProxy.Domain;

namespace LLMProxy.Application;

public sealed record CatalogSnapshot(IModelRegistry Registry, IModelPricing Pricing, IReadOnlyList<ILlmProvider> Providers);
public sealed record ConfigurationReloadResult(long Generation, int Models, int Providers);
public interface IRuntimeConfiguration
{
    Task<ConfigurationReloadResult> ReloadAsync(CancellationToken cancellationToken);
    Task RefreshProviderKeysAsync(CancellationToken cancellationToken);
}

public sealed class RuntimeCatalog(CatalogSnapshot initial) : IModelRegistry, IModelPricing, IProviderCatalog
{
    private CatalogSnapshot _current = initial;
    public CatalogSnapshot Current => Volatile.Read(ref _current);
    public IReadOnlyList<ModelDefinition> Models => Current.Registry.Models;
    public IReadOnlyList<ILlmProvider> Providers => Current.Providers;
    public ModelDefinition Get(string model) => Current.Registry.Get(model);
    public decimal GetInputTokenPrice(string model) => Current.Pricing.GetInputTokenPrice(model);
    public decimal GetOutputTokenPrice(string model) => Current.Pricing.GetOutputTokenPrice(model);
    public ModelPrice GetPrice(string model, long inputTokens) => Current.Pricing.GetPrice(model, inputTokens);
    public ModelPrice GetReservationPrice(string model, long inputTokens) => Current.Pricing.GetReservationPrice(model, inputTokens);
    public void Replace(CatalogSnapshot snapshot) => Interlocked.Exchange(ref _current, snapshot);
}

public sealed class ProviderLatency(TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<string, (double Seconds, DateTimeOffset At)> _seconds = new(StringComparer.Ordinal);
    public double Get(ModelTarget target)
    {
        var sample = _seconds.GetValueOrDefault($"{target.Provider}/{target.Model}");
        return sample.At < (time ?? TimeProvider.System).GetUtcNow().AddMinutes(-5) ? 0 : sample.Seconds;
    }
    public void Observe(ModelTarget target, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return;
        var now = (time ?? TimeProvider.System).GetUtcNow();
        _seconds.AddOrUpdate($"{target.Provider}/{target.Model}", (seconds, now), (_, old) => (old.Seconds * .8 + seconds * .2, now));
    }
}

public sealed class CostRouting(CostCalculator costs) : IRoutingStrategy
{
    public string Name => "cost";
    public Task<IReadOnlyList<ModelTarget>> OrderAsync(string model, IReadOnlyList<ModelTarget> targets,
        CancellationToken cancellationToken) => OrderAsync(new LlmRequest { Model = model }, targets, cancellationToken);
    public Task<IReadOnlyList<ModelTarget>> OrderAsync(LlmRequest request, IReadOnlyList<ModelTarget> targets,
        CancellationToken cancellationToken) => OrderAsync(request, targets, null, cancellationToken);
    public Task<IReadOnlyList<ModelTarget>> OrderAsync(LlmRequest request, IReadOnlyList<ModelTarget> targets,
        IModelPricing? pricing, CancellationToken cancellationToken)
    {
        var usage = TokenUsage.From(RequestValidator.EstimateInputTokens(request), request.OutputTokenLimit);
        return Task.FromResult<IReadOnlyList<ModelTarget>>(targets.OrderBy(t => pricing is null ? costs.Calculate(t, usage)
            : pricing.GetPrice($"{t.Provider}/{t.Model}", usage.InputTokens).Calculate(usage)).ToArray());
    }
}

public sealed class LatencyRouting(ProviderLatency latency) : IRoutingStrategy
{
    public string Name => "latency";
    public Task<IReadOnlyList<ModelTarget>> OrderAsync(string model, IReadOnlyList<ModelTarget> targets,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModelTarget>>(targets.OrderBy(latency.Get).ToArray());
}
