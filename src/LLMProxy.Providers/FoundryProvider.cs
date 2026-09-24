using Azure.Core;
using Azure.Identity;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public sealed class FoundryProvider(ProviderHttpTransport transport, ProviderOptions options, TokenCredential? credential = null)
    : OpenAiCompatibleProvider(transport, options.Foundry)
{
    public override string Name => "foundry";
    protected override ProviderConnectionOptions? KeyConnection => options.Foundry.Authentication == FoundryAuthentication.ApiKey
        ? options.Foundry : null;

    protected override Uri Endpoint(LlmRequest request) => options.Foundry.Endpoint(
        new Uri(options.Foundry.BaseUrl).AbsolutePath.TrimEnd('/') == ""
            ? "openai/v1/chat/completions" : "chat/completions");

    protected override async ValueTask<Dictionary<string, string>> HeadersAsync(CancellationToken cancellationToken)
    {
        if (!options.Foundry.IsConfigured) throw new ProviderException("provider_not_configured", false, 503);
        if (options.Foundry.Authentication == FoundryAuthentication.ApiKey) return Headers();
        if (credential is null) throw new ProviderException("provider_authentication_failed", false);
        try
        {
            // Azure.Identity owns caching and refresh; request a token for every gateway call.
            var token = await credential.GetTokenAsync(new TokenRequestContext([options.Foundry.TokenScope]), cancellationToken);
            if (string.IsNullOrWhiteSpace(token.Token)) throw new ProviderException("provider_authentication_failed", false);
            return new Dictionary<string, string> { ["Authorization"] = "Bearer " + token.Token };
        }
        catch (AuthenticationFailedException) { throw new ProviderException("provider_authentication_failed", false); }
    }
}
