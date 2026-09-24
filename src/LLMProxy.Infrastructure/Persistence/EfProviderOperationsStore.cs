using LLMProxy.Domain;
using Microsoft.EntityFrameworkCore;

namespace LLMProxy.Infrastructure.Persistence;

public sealed partial class EfGatewayStore
{
    public async Task<long> RevisionAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.OperationsRevisions.Where(x => x.Id == 1).Select(x => x.Version).SingleAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<StoredProviderKey>> ListProviderKeysAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ProviderKeys.AsNoTracking().OrderBy(x => x.Provider).ThenBy(x => x.KeyId).ToListAsync(cancellationToken);
    }

    public Task SaveProviderKeyAsync(StoredProviderKey key, IReadOnlyList<string> configuredKeyIds, bool create,
        string actor, CancellationToken cancellationToken) => TransactionAsync(async db =>
    {
        // This row serializes mutations across replicas, including pool-size checks and first inserts.
        await db.OperationsRevisions.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
        var rows = await db.ProviderKeys.Where(x => x.Provider == key.Provider).ToListAsync(cancellationToken);
        var existing = rows.SingleOrDefault(x => x.KeyId == key.KeyId);
        if (create && (existing is not null || configuredKeyIds.Contains(key.KeyId)))
            throw new GatewayException("This provider key already exists.", "provider_key_exists", 409);
        if (rows.Select(x => x.KeyId).Concat(configuredKeyIds).Append(key.KeyId).Distinct(StringComparer.Ordinal).Count() > 64)
            throw new GatewayException("A provider can have at most 64 keys, including disabled keys.", "provider_key_limit", 409);
        if (!create && existing is null && !configuredKeyIds.Contains(key.KeyId))
            throw new GatewayException("Provider key not found.", "provider_key_not_found", 404);
        if (existing is null) db.ProviderKeys.Add(key);
        else
        {
            existing.Enabled = key.Enabled;
            existing.Label = key.Label;
            existing.UpdatedAt = key.UpdatedAt;
        }
        db.Audit.Add(new AuditRecord
        {
            Actor = actor,
            Action = create ? "provider_key.add" : "provider_key.update",
            Resource = key.Provider + "/" + key.KeyId,
            CreatedAt = key.UpdatedAt,
            StatusCode = 200
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }, cancellationToken);

    public async Task<IReadOnlyList<ProviderKeyUsage>> ProviderUsageAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Attempts.AsNoTracking().Where(x => x.CreatedAt >= since && x.ProviderKeyId != null)
            .GroupBy(x => new { x.Provider, x.ProviderKeyId }).Select(g => new ProviderKeyUsage(g.Key.Provider, g.Key.ProviderKeyId!,
                g.LongCount(), g.Sum(x => x.HttpStatus == null || x.HttpStatus >= 400 ? 1L : 0L),
                g.Sum(x => x.InputTokens), g.Sum(x => x.OutputTokens), g.Sum(x => x.ActualCost ?? x.EstimatedCost),
                g.Average(x => (double)x.LatencyMs))).ToListAsync(cancellationToken);
    }
}
