using Azure.Core;
using Azure.Identity;
using LLMProxy.Domain;
using LLMProxy.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LLMProxy.ProviderTests;

public sealed class FoundryTests
{
    [Theory]
    [InlineData("https://resource.services.ai.azure.com")]
    [InlineData("https://resource.services.ai.azure.com/openai/v1/")]
    [InlineData("https://resource.openai.azure.com/openai/v1")]
    public async Task Foundry_uses_v1_deployments_and_bearer_keys_without_a_legacy_api_version(string endpoint)
    {
        using var harness = new ProviderHarness(Responses.OpenAi);
        harness.Options.Foundry.BaseUrl = endpoint;
        Assert.True(harness.Provider("foundry").IsConfigured);
        await harness.Provider("foundry").ChatCompletionAsync(ProviderHarness.Request() with { Model = "my-deployment" }, default);
        Assert.Equal("/openai/v1/chat/completions", harness.Uri!.AbsolutePath);
        Assert.Empty(harness.Uri.Query);
        Assert.Equal("Bearer fake-provider-secret", harness.Headers["Authorization"]);
        Assert.Contains("my-deployment", harness.Body!);
    }

    [Theory]
    [InlineData("https://resource.services.ai.azure.com/api/projects/example")]
    [InlineData("https://resource.services.ai.azure.com/anthropic")]
    [InlineData("https://key@example.test/openai/v1")]
    [InlineData("https://example.test/openai/v1?api-key=secret")]
    [InlineData("http://example.test/openai/v1")]
    public void Foundry_does_not_enable_invalid_or_different_api_endpoints(string endpoint)
    {
        var connection = new FoundryConnectionOptions { BaseUrl = endpoint, ApiKey = "fake" };
        Assert.False(connection.IsConfigured);
    }

    [Fact]
    public async Task Entra_tokens_use_the_configured_scope_and_cancellation_token()
    {
        using var harness = new ProviderHarness(Responses.OpenAi);
        harness.Options.Foundry.ApiKey = "";
        harness.Options.Foundry.Authentication = FoundryAuthentication.EntraId;
        harness.Options.Foundry.TokenScope = "https://ai.azure.com/.default";
        var credential = new TestCredential();
        using var cancellation = new CancellationTokenSource();
        var provider = new FoundryProvider(new(harness), harness.Options, credential);
        Assert.True(provider.IsConfigured);
        await provider.ChatCompletionAsync(ProviderHarness.Request(), cancellation.Token);
        Assert.Equal(["https://ai.azure.com/.default"], credential.Scopes);
        Assert.Equal(cancellation.Token, credential.Cancellation);
        Assert.Equal("Bearer fake-entra-token", harness.Headers["Authorization"]);
        Assert.DoesNotContain("fake-entra-token", harness.Uri!.ToString());
        Assert.DoesNotContain("fake-entra-token", harness.Body!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_failures_are_sanitized_before_any_provider_request(bool unavailable)
    {
        using var harness = new ProviderHarness(Responses.OpenAi);
        harness.Options.Foundry.Authentication = FoundryAuthentication.EntraId;
        var credential = new TestCredential
        {
            Failure = unavailable ? new CredentialUnavailableException("sensitive-credential-detail")
                : new AuthenticationFailedException("sensitive-credential-detail")
        };
        var provider = new FoundryProvider(new(harness), harness.Options, credential);
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.ChatCompletionAsync(ProviderHarness.Request(), default));
        Assert.Equal("provider_authentication_failed", error.Code);
        Assert.False(error.IsTransient);
        Assert.DoesNotContain("sensitive-credential-detail", error.ToString());
        Assert.Null(harness.Body);
    }

    [Fact]
    public async Task Entra_token_acquisition_observes_cancellation()
    {
        using var harness = new ProviderHarness(Responses.OpenAi);
        harness.Options.Foundry.Authentication = FoundryAuthentication.EntraId;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var provider = new FoundryProvider(new(harness), harness.Options, new TestCredential());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ChatCompletionAsync(ProviderHarness.Request(), cancellation.Token));
        Assert.Null(harness.Body);
    }

    [Fact]
    public async Task Dependency_injection_uses_an_injected_credential_for_Entra_streams()
    {
        using var harness = new ProviderHarness(Responses.Stream("foundry"), "text/event-stream");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FOUNDRY_ENDPOINT"] = "https://example.test/openai/v1",
            ["FOUNDRY_AUTHENTICATION"] = "EntraId",
            ["FOUNDRY_TOKEN_SCOPE"] = "https://cognitiveservices.azure.com/.default"
        }).Build();
        var credential = new TestCredential();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TokenCredential>(credential);
        services.AddLlmProxyProviders(configuration);
        services.AddSingleton<IHttpClientFactory>(harness);
        await using var container = services.BuildServiceProvider();
        var provider = container.GetServices<ILlmProvider>().Single(provider => provider.Name == "foundry");
        var content = "";
        await foreach (var chunk in provider.StreamChatCompletionAsync(ProviderHarness.Request(true), default)) content += chunk.Delta?.Content;
        Assert.Equal("Hello", content);
        Assert.Equal(["https://cognitiveservices.azure.com/.default"], credential.Scopes);
    }

    private sealed class TestCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];
        public CancellationToken Cancellation { get; private set; }
        public Exception? Failure { get; init; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Only asynchronous token acquisition is expected.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            Cancellation = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is { } failure) throw failure;
            return ValueTask.FromResult(new AccessToken("fake-entra-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
