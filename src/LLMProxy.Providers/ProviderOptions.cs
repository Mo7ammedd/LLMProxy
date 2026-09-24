namespace LLMProxy.Providers;

public class ProviderConnectionOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool AllowInsecureHttp { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey)
        && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || AllowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public Uri Endpoint(string relative) => new(new Uri(BaseUrl.TrimEnd('/') + "/"), relative);
}

public sealed class AzureConnectionOptions : ProviderConnectionOptions
{
    public string ApiVersion { get; set; } = "2024-10-21";
}

public sealed class ProviderOptions
{
    public ProviderConnectionOptions OpenAI { get; set; } = new() { BaseUrl = "https://api.openai.com/v1" };
    public ProviderConnectionOptions Anthropic { get; set; } = new() { BaseUrl = "https://api.anthropic.com/v1" };
    public ProviderConnectionOptions Gemini { get; set; } = new() { BaseUrl = "https://generativelanguage.googleapis.com/v1beta" };
    public AzureConnectionOptions AzureOpenAI { get; set; } = new();
    public HttpResilienceOptions Resilience { get; set; } = new();
}

public sealed class HttpResilienceOptions
{
    public int RetryCount { get; set; } = 2;
    public double RetryDelaySeconds { get; set; } = 0.5;
    public int AttemptTimeoutSeconds { get; set; } = 30;
    public int TotalTimeoutSeconds { get; set; } = 100;
}
