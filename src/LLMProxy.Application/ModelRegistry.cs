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
                || value.MaxOutputTokens is < 1 or > 1_000_000 || value.RequestsPerMinute < 1)
                throw new InvalidOperationException("Invalid model configuration.");
            var targets = value.Providers.Select(provider =>
            {
                if (!value.ProviderModels.TryGetValue(provider, out var model) || string.IsNullOrWhiteSpace(model))
                    throw new InvalidOperationException("Every model provider needs an explicit upstream model mapping.");
                return new ModelTarget(provider, model);
            }).ToArray();
            return new ModelDefinition(pair.Key, targets, value.Routing, value.EnableFallback,
                value.RequestsPerMinute, value.MaxOutputTokens);
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

    private PriceOptions Get(string model)
    {
        if (!options.Pricing.TryGetValue(model, out var price) || price.InputPerMillion < 0 || price.OutputPerMillion < 0)
            throw new InvalidOperationException("Missing or invalid provider/model pricing configuration.");
        return price;
    }
}

public sealed class CostCalculator(IModelPricing pricing)
{
    public decimal Calculate(ModelTarget target, TokenUsage usage)
    {
        var key = $"{target.Provider}/{target.Model}";
        return decimal.Round(usage.InputTokens * pricing.GetInputTokenPrice(key)
            + usage.OutputTokens * pricing.GetOutputTokenPrice(key), 9, MidpointRounding.ToPositiveInfinity);
    }
}
