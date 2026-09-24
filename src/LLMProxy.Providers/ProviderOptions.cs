namespace LLMProxy.Providers;

public class ProviderConnectionOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool AllowInsecureHttp { get; set; }
    public virtual bool IsConfigured => HasValidEndpoint && !string.IsNullOrWhiteSpace(ApiKey);
    protected bool HasValidEndpoint => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || AllowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public Uri Endpoint(string relative) => new(new Uri(BaseUrl.TrimEnd('/') + "/"), relative);
}

public sealed class AzureConnectionOptions : ProviderConnectionOptions
{
    public string ApiVersion { get; set; } = "2024-10-21";
}

public enum FoundryAuthentication { ApiKey, EntraId }

public sealed class FoundryConnectionOptions : ProviderConnectionOptions
{
    public FoundryAuthentication Authentication { get; set; } = FoundryAuthentication.ApiKey;
    public string TokenScope { get; set; } = "https://ai.azure.com/.default";
    public override bool IsConfigured => HasValidEndpoint
        && (new Uri(BaseUrl).AbsolutePath.TrimEnd('/') is "" or "/openai/v1")
        && (Authentication switch
        {
            FoundryAuthentication.ApiKey => !string.IsNullOrWhiteSpace(ApiKey),
            FoundryAuthentication.EntraId => !string.IsNullOrWhiteSpace(TokenScope),
            _ => false
        });
}

public sealed class OllamaConnectionOptions : ProviderConnectionOptions
{
    // An explicit endpoint enables Ollama. Local servers do not require a provider key.
    public override bool IsConfigured => HasValidEndpoint;
}

public sealed class ProviderOptions
{
    public ProviderConnectionOptions OpenAI { get; set; } = new() { BaseUrl = "https://api.openai.com/v1" };
    public ProviderConnectionOptions Anthropic { get; set; } = new() { BaseUrl = "https://api.anthropic.com/v1" };
    public ProviderConnectionOptions Gemini { get; set; } = new() { BaseUrl = "https://generativelanguage.googleapis.com/v1beta" };
    public AzureConnectionOptions AzureOpenAI { get; set; } = new();
    public FoundryConnectionOptions Foundry { get; set; } = new();
    public ProviderConnectionOptions Mistral { get; set; } = new() { BaseUrl = "https://api.mistral.ai/v1" };
    public ProviderConnectionOptions Cohere { get; set; } = new() { BaseUrl = "https://api.cohere.com/v2" };
    public ProviderConnectionOptions DeepSeek { get; set; } = new() { BaseUrl = "https://api.deepseek.com/v1" };
    public ProviderConnectionOptions Groq { get; set; } = new() { BaseUrl = "https://api.groq.com/openai/v1" };
    public OllamaConnectionOptions Ollama { get; set; } = new();
    public HttpResilienceOptions Resilience { get; set; } = new();
}

public sealed class HttpResilienceOptions
{
    public int RetryCount { get; set; } = 2;
    public double RetryDelaySeconds { get; set; } = 0.5;
    public int AttemptTimeoutSeconds { get; set; } = 30;
    public int TotalTimeoutSeconds { get; set; } = 100;
}
