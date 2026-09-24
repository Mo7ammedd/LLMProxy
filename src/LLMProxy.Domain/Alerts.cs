namespace LLMProxy.Domain;

public sealed class OperationalAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Fingerprint { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Resource { get; set; } = "";
    public string Severity { get; set; } = "warning";
    public string Message { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public int Occurrences { get; set; } = 1;
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? DeliveryLeaseUntil { get; set; }
    public int DeliveryAttempts { get; set; }
    public DateTimeOffset? NextDeliveryAt { get; set; }
}

public sealed class AlertEvaluationLock { public int Id { get; set; } = 1; }
public sealed record AlertObservation(string Fingerprint, string Kind, string Resource, string Severity, string Message);
public sealed record ProviderHealthSample(string Provider, long Attempts, long Failures, double AverageLatencyMs);
public sealed record AlertInputs(IReadOnlyList<AlertObservation> Budgets, IReadOnlyList<ProviderHealthSample> Providers);

public interface IAlertStore
{
    Task<AlertInputs> InputsAsync(DateTimeOffset since, DateTimeOffset now, double budgetFraction, CancellationToken cancellationToken);
    Task ApplyAsync(IReadOnlyList<AlertObservation> observations, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<OperationalAlert>> ListAsync(bool includeResolved, int limit, CancellationToken cancellationToken);
    Task AcknowledgeAsync(Guid id, string actor, DateTimeOffset now, CancellationToken cancellationToken);
    Task<OperationalAlert?> ClaimDeliveryAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task CompleteDeliveryAsync(Guid id, DateTimeOffset leaseUntil, bool success, DateTimeOffset now, CancellationToken cancellationToken);
}
