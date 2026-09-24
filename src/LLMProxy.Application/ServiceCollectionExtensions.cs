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
            || options.RateLimiting.PerUserRequestsPerMinute < 1 || options.RateLimiting.Users.Values.Any(x => x < 1))
            throw new InvalidOperationException("Invalid request or rate limit configuration.");
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IModelRegistry, ModelRegistry>();
        services.AddSingleton<IModelPricing, ConfiguredPricing>();
        services.AddSingleton<CostCalculator>();
        services.AddSingleton<IRoutingStrategy, PriorityRouting>();
        services.AddSingleton<IRoutingStrategy, FallbackRouting>();
        services.AddSingleton<IRoutingStrategy, RoundRobinRouting>();
        services.AddSingleton<IRoutingStrategy, RandomRouting>();
        services.AddSingleton<ModelRouter>();
        services.AddSingleton<IModelRouter>(sp => sp.GetRequiredService<ModelRouter>());
        services.AddSingleton<IRoutePlanner>(sp => sp.GetRequiredService<ModelRouter>());
        services.AddSingleton<ApiKeyService>();
        services.AddSingleton<RequestValidator>();
        services.AddSingleton<GatewayTelemetry>();
        services.AddSingleton<GatewayService>();
        return services;
    }
}
