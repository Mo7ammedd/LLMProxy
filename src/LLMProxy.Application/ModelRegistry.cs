using LLMProxy.Domain;

namespace LLMProxy.Application;

public sealed class ModelRegistry : IModelRegistry
{
    private readonly Dictionary<string, ModelDefinition> _models;
    public IReadOnlyList<ModelDefinition> Models { get; }

    public ModelRegistry(GatewayOptions options)
    {
        if (options.Models.Count == 0) throw new InvalidOperationException("Configure at least one public model.");
        _models = options.Models.ToDictionary(pair => pair.Key, pair =>
        {
            var value = pair.Value;
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || value.Providers.Length == 0
                || value.Providers.Distinct(StringComparer.Ordinal).Count() != value.Providers.Length
                || value.MaxOutputTokens is < 1 or > 1_000_000 || value.RequestsPerMinute < 1 || value.MaxConcurrentRequests < 1)
                throw new InvalidOperationException("Invalid model configuration.");
            var targets = value.Providers.Select(provider =>
            {
                if (!value.ProviderModels.TryGetValue(provider, out var model) || string.IsNullOrWhiteSpace(model))
                    throw new InvalidOperationException("Every model provider needs an explicit upstream model mapping.");
                var capabilities = value.ProviderCapabilities.GetValueOrDefault(provider);
                if (capabilities is { ContextWindowTokens: < 1 } || capabilities is { MaxTemperature: < 0 or > 2 }
                    || capabilities is { MinTopP: < 0 } || capabilities is { MaxTopP: > 1 }
                    || capabilities is not null && capabilities.MinTopP > capabilities.MaxTopP)
                    throw new InvalidOperationException("Invalid model capabilities.");
                return new ModelTarget(provider, model, capabilities);
            }).ToArray();
            return new ModelDefinition(pair.Key, targets, value.Routing, value.EnableFallback,
                value.RequestsPerMinute, value.MaxOutputTokens, value.MaxConcurrentRequests);
        }, StringComparer.Ordinal);
        Models = _models.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    public ModelDefinition Get(string model) => _models.TryGetValue(model, out var value)
        ? value : throw new GatewayException("The requested model does not exist.", "model_not_found", 404, "model");
}

public sealed class ConfiguredPricing(GatewayOptions options) : IModelPricing
{
    public decimal GetInputTokenPrice(string model) => Get(model).InputPerMillion / 1_000_000m;
    public decimal GetOutputTokenPrice(string model) => Get(model).OutputPerMillion / 1_000_000m;

    public ModelPrice GetPrice(string model, long inputTokens)
    {
        var price = Get(model);
        var tier = price.Tiers.Where(t => t.FromInputTokens <= inputTokens).OrderByDescending(t => t.FromInputTokens).FirstOrDefault();
        var input = tier?.InputPerMillion ?? price.InputPerMillion;
        return new ModelPrice(input / 1_000_000m, (tier?.OutputPerMillion ?? price.OutputPerMillion) / 1_000_000m,
            (tier?.CachedInputPerMillion ?? price.CachedInputPerMillion ?? input) / 1_000_000m,
            (tier?.CacheCreationPerMillion ?? price.CacheCreationPerMillion ?? input) / 1_000_000m);
    }

    public ModelPrice GetReservationPrice(string model, long inputTokens)
    {
        var price = Get(model);
        var samples = new[] { GetPrice(model, 0), GetPrice(model, inputTokens) }
            .Concat(price.Tiers.Where(t => t.FromInputTokens <= inputTokens).Select(t => GetPrice(model, t.FromInputTokens))).ToArray();
        return new ModelPrice(samples.Max(p => p.Input), samples.Max(p => p.Output),
            samples.Max(p => p.CachedInput), samples.Max(p => p.CacheCreation));
    }

    private PriceOptions Get(string model)
    {
        // ':' separates .NET configuration paths even inside JSON dictionary keys (e.g. Ollama tags).
        // Escape '%' first so literal percent-encoded model names remain distinct.
        var key = model.Replace("%", "%25", StringComparison.Ordinal).Replace(":", "%3A", StringComparison.Ordinal);
        if (!options.Pricing.TryGetValue(key, out var price) || price.InputPerMillion < 0 || price.OutputPerMillion < 0
            || price.CachedInputPerMillion < 0 || price.CacheCreationPerMillion < 0
            || price.Tiers.Any(t => t.FromInputTokens < 0 || t.InputPerMillion < 0 || t.OutputPerMillion < 0
                || t.CachedInputPerMillion < 0 || t.CacheCreationPerMillion < 0)
            || price.Tiers.Select(t => t.FromInputTokens).Distinct().Count() != price.Tiers.Count)
            throw new InvalidOperationException("Missing or invalid provider/model pricing configuration.");
        return price;
    }
}

public sealed class CostCalculator(IModelPricing pricing)
{
    public decimal Calculate(ProviderRoute route, TokenUsage usage, bool reservation = false)
    {
        var table = route.Pricing ?? pricing;
        var key = $"{route.Target.Provider}/{route.Target.Model}";
        return (reservation ? table.GetReservationPrice(key, usage.InputTokens) : table.GetPrice(key, usage.InputTokens)).Calculate(usage);
    }
    public ModelPrice GetPrice(ModelTarget target, long inputTokens) => pricing.GetPrice($"{target.Provider}/{target.Model}", inputTokens);
    public decimal Calculate(ModelTarget target, TokenUsage usage)
    {
        var key = $"{target.Provider}/{target.Model}";
        return pricing.GetPrice(key, usage.InputTokens).Calculate(usage);
    }
}
