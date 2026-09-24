using Azure.Core;
using Azure.Identity;
using LLMProxy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace LLMProxy.Providers;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlmProxyProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var options = ProviderAccounts.Read(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton<IProviderPoolState>(sp => new MemoryProviderPoolState(sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<ProviderHttpTransport>();
        services.ConfigureHttpClientDefaults(client => ConfigureProviderClient(client, options.Resilience));
        foreach (var name in BuiltInAndAccountNames(options))
            services.AddHttpClient("llmproxy-check." + name).WithoutProviderRetries();
        foreach (var name in ProviderAccounts.BuiltIns)
        {
            services.AddHttpClient("llmproxy." + name);
            services.AddSingleton<ILlmProvider>(sp => ProviderAccounts.Create(name, options,
                sp.GetRequiredService<ProviderHttpTransport>(), sp.GetService<TokenCredential>()));
        }
        foreach (var (name, account) in options.Accounts)
        {
            services.AddHttpClient("llmproxy." + name);
            services.AddSingleton<ILlmProvider>(sp => ProviderAccounts.CreateAccount(name, account,
                sp.GetRequiredService<IHttpClientFactory>(), sp.GetService<TokenCredential>(), sp.GetService<TimeProvider>(),
                sp.GetRequiredService<IProviderPoolState>()));
        }
        return services;
    }

    private static IEnumerable<string> BuiltInAndAccountNames(ProviderOptions options) => ProviderAccounts.BuiltIns.Concat(options.Accounts.Keys);

    public static IHttpClientBuilder WithoutProviderRetries(this IHttpClientBuilder client) => client.ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
    {
        foreach (var handler in handlers.OfType<ResilienceHandler>().ToArray()) handlers.Remove(handler);
    });

    public static IHttpClientBuilder AddProviderHttpClient(this IServiceCollection services, string name, HttpResilienceOptions options)
        => ConfigureProviderClient(services.AddHttpClient("llmproxy." + name), options);

    private static IHttpClientBuilder ConfigureProviderClient(IHttpClientBuilder client, HttpResilienceOptions options)
    {
        if (options.RetryCount is < 0 or > 5 || options.AttemptTimeoutSeconds < 1
            || options.TotalTimeoutSeconds < options.AttemptTimeoutSeconds || options.RetryDelaySeconds < 0)
            throw new InvalidOperationException("Invalid provider resilience configuration.");
        client.ConfigureHttpClient(http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10)
            })
            .RemoveAllLoggers();
        client.AddStandardResilienceHandler(resilience =>
        {
            resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(options.TotalTimeoutSeconds);
            resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.AttemptTimeoutSeconds);
            resilience.Retry.MaxRetryAttempts = Math.Max(1, options.RetryCount);
            resilience.Retry.Delay = TimeSpan.FromSeconds(options.RetryDelaySeconds);
            resilience.Retry.BackoffType = DelayBackoffType.Exponential;
            resilience.Retry.UseJitter = true;
            var shouldRetry = resilience.Retry.ShouldHandle;
            resilience.Retry.ShouldHandle = args => options.RetryCount == 0
                || args.Outcome.Result?.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    && args.Context.GetRequestMessage()?.Options.TryGetValue(ProviderKeyPool.MultipleKeysOption, out var multipleKeys) == true
                    && multipleKeys
                ? ValueTask.FromResult(false) : shouldRetry(args);
            resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(60, options.AttemptTimeoutSeconds * 2));
        }).SelectPipelineBy(_ => request => (request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "") + "/"
            + (request.Options.TryGetValue(ProviderKeyPool.KeyIdOption, out var keyId) ? keyId : "default"));
        client.AddHttpMessageHandler(() => new AttemptRecordingHandler());
        return client;
    }
}
