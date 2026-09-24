namespace LLMProxy.Application;

public sealed class GatewayOptions
{
    public Dictionary<string, ModelOptions> Models { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, PriceOptions> Pricing { get; set; } = new(StringComparer.Ordinal);
    public RequestOptions Requests { get; set; } = new();
    public RateLimitOptions RateLimiting { get; set; } = new();
    public ConcurrencyOptions Concurrency { get; set; } = new();
    public BatchOptions Batches { get; set; } = new();
}

public sealed class BatchOptions
{
    public int MaxFileBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxRequests { get; set; } = 1000;
    public int Workers { get; set; } = 4;
}

public sealed class ModelOptions
{
    public string[] Providers { get; set; } = [];
    public Dictionary<string, string> ProviderModels { get; set; } = new(StringComparer.Ordinal);
    public string Routing { get; set; } = "priority";
    public bool EnableFallback { get; set; } = true;
    public int RequestsPerMinute { get; set; } = 600;
    public int MaxOutputTokens { get; set; } = 4096;
    public int MaxConcurrentRequests { get; set; } = 100;
    public Dictionary<string, LLMProxy.Domain.ModelCapabilities> ProviderCapabilities { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PriceOptions
{
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
    public decimal? CachedInputPerMillion { get; set; }
    public decimal? CacheCreationPerMillion { get; set; }
    public List<PriceTierOptions> Tiers { get; set; } = [];
}

public sealed class PriceTierOptions
{
    public long FromInputTokens { get; set; }
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
    public decimal? CachedInputPerMillion { get; set; }
    public decimal? CacheCreationPerMillion { get; set; }
}

public sealed class ConcurrencyOptions
{
    public int GlobalLimit { get; set; } = 1000;
    public int PerKeyLimit { get; set; } = 20;
    public int PerProviderLimit { get; set; } = 100;
}

public sealed class RequestOptions
{
    public int MaxBodyBytes { get; set; } = 1_048_576;
    public int MaxMessages { get; set; } = 256;
    public int DefaultMaxOutputTokens { get; set; } = 1024;
    public int TimeoutSeconds { get; set; } = 180;
    public int ReservationTtlSeconds { get; set; } = 600;
}

public sealed class RateLimitOptions
{
    public int PerUserRequestsPerMinute { get; set; } = 300;
    public Dictionary<string, int> Users { get; set; } = new(StringComparer.Ordinal);
}
