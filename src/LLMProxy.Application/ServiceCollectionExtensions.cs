using LLMProxy.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LLMProxy.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlmProxyApplication(this IServiceCollection services, GatewayOptions options)
    {
        if (options.Requests.TimeoutSeconds is < 1 or > 3600
            || options.Requests.ReservationTtlSeconds <= options.Requests.TimeoutSeconds + 60
            || options.Requests.DefaultMaxOutputTokens < 1 || options.Requests.MaxMessages < 1
            || options.Requests.MaxBodyBytes is < 1024 or > 16_777_216
            || options.RateLimiting.PerUserRequestsPerMinute < 1 || options.RateLimiting.Users.Values.Any(x => x < 1)
            || options.Concurrency.GlobalLimit < 1 || options.Concurrency.PerKeyLimit < 1 || options.Concurrency.PerProviderLimit < 1)
            throw new InvalidOperationException("Invalid request or rate limit configuration.");
        if (options.Batches.MaxRequests is < 1 or > 50000 || options.Batches.MaxFileBytes is < 1024 or > 209715200
            || options.Batches.Workers is < 0 or > 64)
            throw new InvalidOperationException("Invalid batch settings.");
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ModelRegistry>();
        services.AddSingleton<ConfiguredPricing>();
        services.AddSingleton(sp => new RuntimeCatalog(new CatalogSnapshot(sp.GetRequiredService<ModelRegistry>(),
            sp.GetRequiredService<ConfiguredPricing>(), sp.GetServices<ILlmProvider>().ToArray())));
        services.AddSingleton<IModelRegistry>(sp => sp.GetRequiredService<RuntimeCatalog>());
        services.AddSingleton<IModelPricing>(sp => sp.GetRequiredService<RuntimeCatalog>());
        services.AddSingleton<IProviderCatalog>(sp => sp.GetRequiredService<RuntimeCatalog>());
        services.AddSingleton<CostCalculator>();
        services.AddSingleton<IRoutingStrategy, PriorityRouting>();
        services.AddSingleton<IRoutingStrategy, FallbackRouting>();
        services.AddSingleton<IRoutingStrategy, RoundRobinRouting>();
        services.AddSingleton<IRoutingStrategy, RandomRouting>();
        services.AddSingleton<IRoutingStrategy, CostRouting>();
        services.AddSingleton<IRoutingStrategy, LatencyRouting>();
        services.AddSingleton<ProviderLatency>();
        services.AddSingleton<ModelRouter>();
        services.AddSingleton<IModelRouter>(sp => sp.GetRequiredService<ModelRouter>());
        services.AddSingleton<IRoutePlanner>(sp => sp.GetRequiredService<ModelRouter>());
        services.AddSingleton<ApiKeyService>();
        services.AddSingleton<OperatorService>();
        services.AddSingleton<RequestValidator>();
        services.AddSingleton<ProtocolRequests>();
        services.AddSingleton<BatchService>();
        services.AddSingleton<GatewayTelemetry>();
        services.AddSingleton<GatewayService>();
        return services;
    }
}
