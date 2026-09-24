using LLMProxy.Domain;
using Microsoft.EntityFrameworkCore;

namespace LLMProxy.Infrastructure.Persistence;

public sealed partial class EfGatewayStore
{
    public async Task<AlertInputs> InputsAsync(DateTimeOffset since, DateTimeOffset now, double budgetFraction, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var lifetime = await db.ApiKeys.AsNoTracking().Where(x => x.Enabled && (x.ExpiresAt == null || x.ExpiresAt > now)
            && x.BudgetUnits > 0 && (x.SpentUnits + x.ReservedUnits) / (double)x.BudgetUnits >= budgetFraction)
            .Select(x => x.Id).ToListAsync(cancellationToken);
        var period = Period(now);
        var monthly = await (from key in db.ApiKeys.AsNoTracking()
                             join window in db.QuotaWindows.AsNoTracking() on key.Id equals window.ApiKeyId
                             where key.Enabled && (key.ExpiresAt == null || key.ExpiresAt > now) && key.MonthlyBudgetUnits > 0
                                 && window.Period == period && (window.SpentUnits + window.ReservedUnits) / (double)key.MonthlyBudgetUnits >= budgetFraction
                             select key.Id).ToListAsync(cancellationToken);
        var budgets = lifetime.Select(id => new AlertObservation("budget:lifetime:" + id, "budget", id.ToString(), "warning",
            "Lifetime spending, including reservations, reached the configured budget threshold."))
            .Concat(monthly.Select(id => new AlertObservation("budget:monthly:" + id, "budget", id.ToString(), "warning",
                "Monthly spending, including reservations, reached the configured budget threshold."))).ToArray();
        var providers = await db.Attempts.AsNoTracking().Where(x => x.CreatedAt >= since && x.CreatedAt <= now)
            .GroupBy(x => x.Provider).Select(g => new ProviderHealthSample(g.Key, g.LongCount(),
                g.Sum(x => x.HttpStatus == null || x.HttpStatus >= 400 ? 1L : 0L), g.Average(x => (double)x.LatencyMs)))
            .ToListAsync(cancellationToken);
        return new(budgets, providers);
    }

    public Task ApplyAsync(IReadOnlyList<AlertObservation> observations, DateTimeOffset now, CancellationToken cancellationToken)
        => TransactionAsync(async db =>
        {
            await db.AlertLocks.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Id, x => x.Id), cancellationToken);
            var fingerprints = observations.Select(x => x.Fingerprint).ToArray();
            var existing = await db.Alerts.Where(x => x.ResolvedAt == null || fingerprints.Contains(x.Fingerprint)).ToListAsync(cancellationToken);
            foreach (var row in existing.Where(x => !fingerprints.Contains(x.Fingerprint) && x.LastSeenAt <= now)) row.ResolvedAt = now;
            foreach (var observation in observations)
            {
                var row = existing.SingleOrDefault(x => x.Fingerprint == observation.Fingerprint);
                if (row is null)
                {
                    row = new OperationalAlert { Fingerprint = observation.Fingerprint, StartedAt = now };
                    db.Alerts.Add(row);
                }
                else if (row.LastSeenAt > now || row.ResolvedAt > now) continue;
                else if (row.ResolvedAt is not null)
                {
                    row.Occurrences++;
                    row.StartedAt = now;
                    row.ResolvedAt = null;
                    row.AcknowledgedAt = null;
                    row.AcknowledgedBy = null;
                    row.DeliveredAt = null;
                    row.DeliveryLeaseUntil = null;
                    row.DeliveryAttempts = 0;
                    row.NextDeliveryAt = null;
                }
                row.LastSeenAt = now;
                row.Kind = observation.Kind;
                row.Resource = observation.Resource;
                row.Severity = observation.Severity;
                row.Message = observation.Message;
            }
            await db.SaveChangesAsync(cancellationToken);
            // Resolved history is retained for 90 days; active incidents are never pruned.
            await db.Alerts.Where(x => x.ResolvedAt < now.AddDays(-90)).ExecuteDeleteAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public async Task<IReadOnlyList<OperationalAlert>> ListAsync(bool includeResolved, int limit, CancellationToken cancellationToken)
    {
        CheckLimit(limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Alerts.AsNoTracking().Where(x => includeResolved || x.ResolvedAt == null)
            .OrderByDescending(x => x.StartedAt).ThenBy(x => x.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public Task AcknowledgeAsync(Guid id, string actor, DateTimeOffset now, CancellationToken cancellationToken) => TransactionAsync(async db =>
    {
        var changed = await db.Alerts.Where(x => x.Id == id).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.AcknowledgedAt, now).SetProperty(x => x.AcknowledgedBy, actor), cancellationToken);
        if (changed == 0) throw new GatewayException("Alert not found.", "alert_not_found", 404);
        db.Audit.Add(new AuditRecord { Actor = actor, Action = "alert.acknowledge", Resource = id.ToString(), CreatedAt = now, StatusCode = 200 });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }, cancellationToken);

    public Task<OperationalAlert?> ClaimDeliveryAsync(DateTimeOffset now, CancellationToken cancellationToken) => TransactionAsync(async db =>
    {
        await db.AlertLocks.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Id, x => x.Id), cancellationToken);
        var alert = await db.Alerts.Where(x => x.ResolvedAt == null && x.AcknowledgedAt == null && x.DeliveredAt == null
            && (x.NextDeliveryAt == null || x.NextDeliveryAt <= now) && (x.DeliveryLeaseUntil == null || x.DeliveryLeaseUntil <= now))
            .OrderBy(x => x.StartedAt).FirstOrDefaultAsync(cancellationToken);
        if (alert is null) return null;
        alert.DeliveryLeaseUntil = now.AddMinutes(1);
        alert.DeliveryAttempts++;
        await db.SaveChangesAsync(cancellationToken);
        return alert;
    }, cancellationToken);

    public async Task CompleteDeliveryAsync(Guid id, DateTimeOffset leaseUntil, bool success, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Alerts.Where(x => x.Id == id && x.DeliveryLeaseUntil == leaseUntil).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.DeliveredAt, success ? now : (DateTimeOffset?)null)
                .SetProperty(x => x.NextDeliveryAt, now.AddMinutes(5)).SetProperty(x => x.DeliveryLeaseUntil, (DateTimeOffset?)null), cancellationToken);
    }
}
