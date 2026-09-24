using Azure.Core;
using LLMProxy.Domain;
using LLMProxy.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LLMProxy.ProviderTests;

public sealed class ProviderRegistrationTests
{
    [Fact]
    public void All_ten_adapters_are_registered_and_nothing_is_implicitly_enabled()
    {
        using var container = Configure([]);
        var providers = container.GetServices<ILlmProvider>().ToArray();
        Assert.Equal(new[] { "openai", "anthropic", "gemini", "azure", "foundry", "mistral", "cohere", "deepseek", "groq", "ollama" }, providers.Select(provider => provider.Name));
        Assert.All(providers, provider => Assert.False(provider.IsConfigured));
        Assert.Null(container.GetService<TokenCredential>());
    }

    [Theory]
    [InlineData("FOUNDRY", "foundry", "https://example.test/openai/v1")]
    [InlineData("MISTRAL", "mistral", "https://example.test/v1")]
    [InlineData("COHERE", "cohere", "https://example.test/v2")]
    [InlineData("DEEPSEEK", "deepseek", "https://example.test/v1")]
    [InlineData("GROQ", "groq", "https://example.test/openai/v1")]
    [InlineData("OLLAMA", "ollama", "https://example.test/v1")]
    public void Environment_aliases_enable_only_the_selected_provider(string prefix, string name, string endpoint)
    {
        using var container = Configure(new Dictionary<string, string?> { [prefix + "_API_KEY"] = "fake", [prefix + "_ENDPOINT"] = endpoint });
        Assert.Equal(name, Assert.Single(container.GetServices<ILlmProvider>(), provider => provider.IsConfigured).Name);
    }

    [Fact]
    public void Blank_endpoint_aliases_preserve_nested_configuration_but_blank_keys_disable_it()
    {
        using var container = Configure(new Dictionary<string, string?>
        {
            ["LLMProxy:Providers:Mistral:BaseUrl"] = "https://custom.test/v1",
            ["LLMProxy:Providers:Mistral:ApiKey"] = "nested-key",
            ["MISTRAL_ENDPOINT"] = "",
            ["MISTRAL_API_KEY"] = ""
        });
        var options = container.GetRequiredService<ProviderOptions>();
        Assert.Equal("https://custom.test/v1", options.Mistral.BaseUrl);
        Assert.Equal("", options.Mistral.ApiKey);
        Assert.False(options.Mistral.IsConfigured);
    }

    [Theory]
    [InlineData("", false, false)]
    [InlineData("http://localhost:11434/v1", false, false)]
    [InlineData("http://localhost:11434/v1", true, true)]
    [InlineData("https://ollama.test/v1", false, true)]
    [InlineData("https://user:secret@ollama.test/v1", false, false)]
    public void Ollama_requires_an_explicit_endpoint_and_http_opt_in(string endpoint, bool insecure, bool configured)
    {
        using var container = Configure(new Dictionary<string, string?>
        {
            ["OLLAMA_ENDPOINT"] = endpoint,
            ["OLLAMA_ALLOW_INSECURE_HTTP"] = insecure.ToString()
        });
        Assert.Equal(configured, container.GetServices<ILlmProvider>().Single(provider => provider.Name == "ollama").IsConfigured);
    }

    [Theory]
    [InlineData("FOUNDRY_AUTHENTICATION", "invalid")]
    [InlineData("FOUNDRY_AUTHENTICATION", "42")]
    [InlineData("OLLAMA_ALLOW_INSECURE_HTTP", "not-a-boolean")]
    public void Invalid_authentication_or_security_switches_fail_at_configuration_time(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Configure(new Dictionary<string, string?> { [key] = value }));
    }

    private static ServiceProvider Configure(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLlmProxyProviders(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }
}
