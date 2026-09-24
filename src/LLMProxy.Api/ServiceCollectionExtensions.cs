using System.Net;
using LLMProxy.Application;
using LLMProxy.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace LLMProxy.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlmProxyApi(this IServiceCollection services, ApiOptions options)
    {
        if (options.AdminKey.Length is > 0 and < 32 or > 256)
            throw new InvalidOperationException("Administrative keys must contain 32–256 random characters.");
        if (options.AllowedOrigins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/"))
            throw new InvalidOperationException("CORS requires explicit HTTP(S) origins.");
        services.AddSingleton(options);
        // The gateway has no cookie/session state. Bearer keys use their own persisted hashes.
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(GatewayKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, GatewayKeyAuthenticationHandler>(GatewayKeyAuthenticationHandler.SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, AdminKeyAuthenticationHandler>(AdminKeyAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy("Admin", policy => policy.AddAuthenticationSchemes(AdminKeyAuthenticationHandler.SchemeName)
                .RequireRole(OperatorRoles.Administrator, OperatorRoles.Operator, OperatorRoles.Auditor))
            .AddPolicy("AdminWrite", policy => policy.AddAuthenticationSchemes(AdminKeyAuthenticationHandler.SchemeName)
                .RequireRole(OperatorRoles.Administrator, OperatorRoles.Operator))
            .AddPolicy("AdminOperators", policy => policy.AddAuthenticationSchemes(AdminKeyAuthenticationHandler.SchemeName)
                .RequireRole(OperatorRoles.Administrator));
        services.AddCors(cors => cors.AddDefaultPolicy(policy => policy.WithOrigins(options.AllowedOrigins)
            .AllowAnyHeader().WithMethods("GET", "POST", "PUT", "DELETE").WithExposedHeaders("X-Request-Id", "Retry-After")));
        services.AddHttpsRedirection(https => https.HttpsPort = options.HttpsPort);
        if (options.TrustedProxies.Length > 0)
        {
            var proxies = options.TrustedProxies.Select(IPAddress.Parse).ToArray();
            services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();
                foreach (var address in proxies) forwarded.KnownProxies.Add(address);
            });
        }
        services.AddHealthChecks().AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<ProviderConfigurationHealthCheck>("provider_configuration", tags: ["ready"])
            .AddCheck<ShutdownHealthCheck>("accepting_requests", tags: ["ready"]);
        return services;
    }

    public static WebApplication UseLlmProxyApi(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ApiOptions>();
        app.UseMiddleware<ApiExceptionMiddleware>();
        if (options.TrustedProxies.Length > 0) app.UseForwardedHeaders();
        if (options.RequireHttps)
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }
        app.UseStatusCodePages(async status =>
        {
            if (status.HttpContext.Request.Path.StartsWithSegments("/v1"))
                await ApiErrors.WriteAsync(status.HttpContext, new GatewayException("The HTTP request could not be handled.",
                    "http_error", status.HttpContext.Response.StatusCode));
        });
        app.UseCors();
        app.UseMiddleware<RequestDeadlineMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<AuditMiddleware>();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true && context.Request.Path.StartsWithSegments("/v1"))
                await context.RequestServices.GetRequiredService<IRuntimeConfiguration>().RefreshProviderKeysAsync(context.RequestAborted);
            await next(context);
        });
        app.MapLlmProxy();
        return app;
    }
}

public sealed class ProviderConfigurationHealthCheck(IModelRegistry registry, IProviderCatalog catalog, IModelPricing pricing) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var available = catalog.Providers.Where(x => x.IsConfigured).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        try
        {
            var availableModels = 0;
            foreach (var model in registry.Models)
            {
                if (model.Targets.Any(x => available.Contains(x.Provider))) availableModels++;
                foreach (var target in model.Targets)
                {
                    pricing.GetInputTokenPrice($"{target.Provider}/{target.Model}");
                    pricing.GetOutputTokenPrice($"{target.Provider}/{target.Model}");
                }
            }
            return Task.FromResult(availableModels == 0
                ? HealthCheckResult.Unhealthy("No public model has a configured provider.")
                : availableModels < registry.Models.Count
                    ? HealthCheckResult.Degraded("Some model aliases have no configured provider and are not advertised.")
                    : HealthCheckResult.Healthy());
        }
        catch (InvalidOperationException) { return Task.FromResult(HealthCheckResult.Unhealthy("Provider/model pricing is missing or invalid.")); }
    }
}

public sealed class ShutdownHealthCheck(IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(lifetime.ApplicationStopping.IsCancellationRequested
            ? HealthCheckResult.Unhealthy("The server is shutting down.") : HealthCheckResult.Healthy());
}
