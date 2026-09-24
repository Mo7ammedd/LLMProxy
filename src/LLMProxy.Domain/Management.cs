namespace LLMProxy.Domain;

public sealed class QuotaWindow
{
    public string Id { get; set; } = "";
    public Guid ApiKeyId { get; set; }
    public string Period { get; set; } = "";
    public long UsedTokens { get; set; }
    public long ReservedTokens { get; set; }
    public long SpentUnits { get; set; }
    public long ReservedUnits { get; set; }
}

public sealed class RetiredKeyCredential
{
    public string KeyHash { get; set; } = "";
    public Guid ApiKeyId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public interface IKeyLifecycleStore
{
    Task<ApiKey?> RotateKeyAsync(Guid id, string hash, string prefix, DateTimeOffset now,
        DateTimeOffset graceUntil, CancellationToken cancellationToken);
}

public static class OperatorRoles
{
    public const string Administrator = "administrator";
    public const string Operator = "operator";
    public const string Auditor = "auditor";
    public static bool Valid(string role) => role is Administrator or Operator or Auditor;
}

public sealed class OperatorAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = OperatorRoles.Auditor;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OperatorSession
{
    public string TokenHash { get; set; } = "";
    public Guid OperatorId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class AuditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Resource { get; set; } = "";
    public int StatusCode { get; set; }
    public Guid? RequestId { get; set; }
}

public sealed class UpstreamAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string? ProviderKeyId { get; set; }
    public string? ProviderRequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long LatencyMs { get; set; }
    public string Status { get; set; } = "unknown";
    public int? HttpStatus { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public bool UsageEstimated { get; set; } = true;
    public decimal EstimatedCost { get; set; }
    public decimal? ActualCost { get; set; }
}

public sealed class ReconciliationRecord
{
    public string Reference { get; set; } = "";
    public Guid AttemptId { get; set; }
    public decimal ActualCost { get; set; }
    public string Actor { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record Page<T>(IReadOnlyList<T> Data, string? NextCursor);
public sealed record UsageQuery(Guid? ApiKeyId = null, string? Owner = null, DateTimeOffset? From = null,
    DateTimeOffset? To = null, string? Model = null, string? Provider = null, string? Status = null,
    int Limit = 100, string? Cursor = null);
public sealed record UsageRollup(string Model, string Provider, long Requests, long InputTokens,
    long OutputTokens, decimal Cost, double AverageLatencyMs);
public sealed record ReconciliationInput(Guid AttemptId, decimal ActualCost, string Reference);

public interface IManagementStore
{
    Task<OperatorAccount?> FindOperatorAsync(string username, CancellationToken cancellationToken);
    Task<IReadOnlyList<OperatorAccount>> ListOperatorsAsync(CancellationToken cancellationToken);
    Task SaveOperatorAsync(OperatorAccount account, bool create, CancellationToken cancellationToken);
    Task<OperatorAccount?> AuthenticateOperatorAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken);
    Task SaveSessionAsync(OperatorSession session, CancellationToken cancellationToken);
    Task DeleteSessionAsync(string tokenHash, CancellationToken cancellationToken);
    Task AppendAuditAsync(AuditRecord record, CancellationToken cancellationToken);
    Task<Page<AuditRecord>> ListAuditAsync(string? cursor, int limit, CancellationToken cancellationToken);
    Task<Page<UsageRecord>> QueryUsageAsync(UsageQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<UsageRollup>> SummarizeUsageAsync(UsageQuery query, CancellationToken cancellationToken);
    Task<Page<ApiKey>> QueryKeysAsync(string? owner, string? cursor, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<QuotaWindow>> QuotaWindowsAsync(Guid keyId, CancellationToken cancellationToken);
    Task<IReadOnlyList<UpstreamAttempt>> ListAttemptsAsync(Guid requestId, CancellationToken cancellationToken);
    Task ReconcileAsync(IReadOnlyList<ReconciliationInput> records, string actor, DateTimeOffset now, CancellationToken cancellationToken);
    Task PruneAsync(DateTimeOffset? usageBefore, DateTimeOffset? auditBefore, DateTimeOffset now, CancellationToken cancellationToken);
}
