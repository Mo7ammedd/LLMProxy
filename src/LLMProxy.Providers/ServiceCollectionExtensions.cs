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
        var options = configuration.GetSection("LLMProxy:Providers").Get<ProviderOptions>() ?? new();
        var connections = new (string Name, string Prefix, ProviderConnectionOptions Connection)[]
        {
            ("openai", "OPENAI", options.OpenAI), ("anthropic", "ANTHROPIC", options.Anthropic),
            ("gemini", "GEMINI", options.Gemini), ("azure", "AZURE_OPENAI", options.AzureOpenAI),
            ("foundry", "FOUNDRY", options.Foundry), ("mistral", "MISTRAL", options.Mistral),
            ("cohere", "COHERE", options.Cohere), ("deepseek", "DEEPSEEK", options.DeepSeek),
            ("groq", "GROQ", options.Groq), ("ollama", "OLLAMA", options.Ollama)
        };
        foreach (var (name, prefix, connection) in connections)
        {
            connection.ApiKey = configuration[prefix + "_API_KEY"] ?? connection.ApiKey;
            connection.BaseUrl = Nonempty(configuration[prefix + "_ENDPOINT"]) ?? connection.BaseUrl;
            services.AddProviderHttpClient(name, options.Resilience);
        }
        options.AzureOpenAI.ApiVersion = Nonempty(configuration["AZURE_OPENAI_API_VERSION"]) ?? options.AzureOpenAI.ApiVersion;
        options.Foundry.TokenScope = Nonempty(configuration["FOUNDRY_TOKEN_SCOPE"]) ?? options.Foundry.TokenScope;
        if (Nonempty(configuration["FOUNDRY_AUTHENTICATION"]) is { } authentication)
        {
            if (!Enum.TryParse<FoundryAuthentication>(authentication, true, out var mode) || !Enum.IsDefined(mode))
                throw new InvalidOperationException("FOUNDRY_AUTHENTICATION must be ApiKey or EntraId.");
            options.Foundry.Authentication = mode;
        }
        if (Nonempty(configuration["OLLAMA_ALLOW_INSECURE_HTTP"]) is { } insecure)
        {
            if (!bool.TryParse(insecure, out var allow)) throw new InvalidOperationException("OLLAMA_ALLOW_INSECURE_HTTP must be true or false.");
            options.Ollama.AllowInsecureHttp = allow;
        }
        if (options.Foundry.Authentication == FoundryAuthentication.EntraId)
            services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddSingleton(options);
        services.AddSingleton<ProviderHttpTransport>();
        services.AddSingleton<ILlmProvider, OpenAiProvider>();
        services.AddSingleton<ILlmProvider, AnthropicProvider>();
        services.AddSingleton<ILlmProvider, GeminiProvider>();
        services.AddSingleton<ILlmProvider, AzureOpenAiProvider>();
        services.AddSingleton<ILlmProvider>(provider => new FoundryProvider(provider.GetRequiredService<ProviderHttpTransport>(), options,
            options.Foundry.Authentication == FoundryAuthentication.EntraId ? provider.GetRequiredService<TokenCredential>() : null));
        services.AddSingleton<ILlmProvider, MistralProvider>();
        services.AddSingleton<ILlmProvider, CohereProvider>();
        services.AddSingleton<ILlmProvider, DeepSeekProvider>();
        services.AddSingleton<ILlmProvider, GroqProvider>();
        services.AddSingleton<ILlmProvider, OllamaProvider>();
        return services;
    }

    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
