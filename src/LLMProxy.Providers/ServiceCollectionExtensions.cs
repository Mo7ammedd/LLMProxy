using LLMProxy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace LLMProxy.Providers;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlmProxyProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("LLMProxy:Providers").Get<ProviderOptions>() ?? new();
        options.OpenAI.ApiKey = configuration["OPENAI_API_KEY"] ?? options.OpenAI.ApiKey;
        options.Anthropic.ApiKey = configuration["ANTHROPIC_API_KEY"] ?? options.Anthropic.ApiKey;
        options.Gemini.ApiKey = configuration["GEMINI_API_KEY"] ?? options.Gemini.ApiKey;
        options.AzureOpenAI.ApiKey = configuration["AZURE_OPENAI_API_KEY"] ?? options.AzureOpenAI.ApiKey;
        options.AzureOpenAI.BaseUrl = configuration["AZURE_OPENAI_ENDPOINT"] ?? options.AzureOpenAI.BaseUrl;
        options.AzureOpenAI.ApiVersion = configuration["AZURE_OPENAI_API_VERSION"] ?? options.AzureOpenAI.ApiVersion;
        services.AddSingleton(options);
        services.AddSingleton<ProviderHttpTransport>();
        foreach (var name in new[] { "openai", "anthropic", "gemini", "azure" }) services.AddProviderHttpClient(name, options.Resilience);
        services.AddSingleton<ILlmProvider, OpenAiProvider>();
        services.AddSingleton<ILlmProvider, AnthropicProvider>();
        services.AddSingleton<ILlmProvider, GeminiProvider>();
        services.AddSingleton<ILlmProvider, AzureOpenAiProvider>();
        return services;
    }

    public static IHttpClientBuilder AddProviderHttpClient(this IServiceCollection services, string name, HttpResilienceOptions options)
    {
        if (options.RetryCount is < 0 or > 5 || options.AttemptTimeoutSeconds < 1
            || options.TotalTimeoutSeconds < options.AttemptTimeoutSeconds || options.RetryDelaySeconds < 0)
            throw new InvalidOperationException("Invalid provider resilience configuration.");
        var client = services.AddHttpClient("llmproxy." + name, http => http.Timeout = Timeout.InfiniteTimeSpan)
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
            if (options.RetryCount == 0) resilience.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
            resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(60, options.AttemptTimeoutSeconds * 2));
        });
        return client;
    }
}
