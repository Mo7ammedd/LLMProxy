namespace LLMProxy.Domain;

public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string KeyHash { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string Owner { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string[] AllowedModels { get; set; } = [];
    public int RequestsPerMinute { get; set; } = 60;
    public long? TokenLimit { get; set; }
    public long? BudgetUnits { get; set; }
    public long UsedTokens { get; set; }
    public long ReservedTokens { get; set; }
    public long SpentUnits { get; set; }
    public long ReservedUnits { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public bool Allows(string model) => AllowedModels.Contains("*", StringComparer.Ordinal)
        || AllowedModels.Contains(model, StringComparer.Ordinal);
}

// Integer nano-USD keeps concurrent quota arithmetic exact in both databases.
public static class Money
{
    public const decimal UnitsPerDollar = 1_000_000_000m;
    public static long ToUnits(decimal value) => checked((long)decimal.Ceiling(value * UnitsPerDollar));
    public static decimal FromUnits(long value) => value / UnitsPerDollar;
}

public sealed class QuotaReservation
{
    public Guid RequestId { get; set; }
    public Guid ApiKeyId { get; set; }
    public string Model { get; set; } = "";
    public long Tokens { get; set; }
    public long CostUnits { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class UsageRecord
{
    public Guid RequestId { get; set; }
    public Guid ApiKeyId { get; set; }
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long LatencyMs { get; set; }
    public string Status { get; set; } = "";
    public string? ErrorCode { get; set; }
    public decimal EstimatedCost { get; set; }
    public bool UsageEstimated { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record ApiKeyPolicy(bool Enabled, string[] AllowedModels, int RequestsPerMinute,
    long? TokenLimit, long? BudgetUnits);

public interface IGatewayStore
{
    Task<ApiKey?> FindKeyAsync(string hash, CancellationToken cancellationToken);
    Task<ApiKey?> FindKeyByIdAsync(Guid id, CancellationToken cancellationToken);
    Task TouchKeyAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);
    Task CreateKeyAsync(ApiKey key, CancellationToken cancellationToken);
    Task<IReadOnlyList<ApiKey>> ListKeysAsync(int limit, CancellationToken cancellationToken);
    Task<ApiKey?> UpdateKeyAsync(Guid id, ApiKeyPolicy policy, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> TryReserveAsync(QuotaReservation reservation, CancellationToken cancellationToken);
    Task CompleteAsync(UsageRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<UsageRecord>> ListUsageAsync(Guid? apiKeyId, int limit, CancellationToken cancellationToken);
    Task<int> RecoverExpiredReservationsAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record RateLimitScope(string Key, int Limit);
public sealed record RateLimitDecision(bool Allowed, int RetryAfterSeconds = 0);

public interface IRateLimiter
{
    Task<RateLimitDecision> AcquireAsync(IReadOnlyList<RateLimitScope> scopes, CancellationToken cancellationToken);
}
