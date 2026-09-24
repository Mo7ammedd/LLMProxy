namespace LLMProxy.Api;

public sealed class ApiOptions
{
    public string[] AllowedOrigins { get; set; } = [];
    public string[] TrustedProxies { get; set; } = [];
    public bool RequireHttps { get; set; }
    public int HttpsPort { get; set; } = 443;
    public string AdminKey { get; set; } = "";
}
