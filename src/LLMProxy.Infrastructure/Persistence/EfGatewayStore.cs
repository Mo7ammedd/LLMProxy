using LLMProxy.Domain;
using Microsoft.EntityFrameworkCore;

namespace LLMProxy.Infrastructure.Persistence;

public sealed partial class EfGatewayStore(IDbContextFactory<GatewayDbContext> factory, TimeProvider? time = null)
    : IGatewayStore, IKeyLifecycleStore, IManagementStore, IBatchStore
{
    public async Task<ApiKey?> FindKeyByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<ApiKey?> FindKeyAsync(string hash, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(x => x.KeyHash == hash, cancellationToken);
        if (key is not null) return key;
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var retired = await db.RetiredCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.KeyHash == hash && x.ExpiresAt > now, cancellationToken);
        if (retired is null) return null;
        key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(x => x.Id == retired.ApiKeyId, cancellationToken);
        // Authenticate against the matched credential while retaining the same policy and allowance identity.
        if (key is not null) key.KeyHash = hash;
        return key;
    }

    public async Task TouchKeyAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.ApiKeys.Where(x => x.Id == id && (x.LastUsedAt == null || x.LastUsedAt < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, now), cancellationToken);
    }

    public Task CreateKeyAsync(ApiKey key, CancellationToken cancellationToken) => TransactionAsync(async db =>
    {
        if (await db.ApiKeys.AnyAsync(existing => existing.Id == key.Id && existing.KeyHash == key.KeyHash, cancellationToken)) return true;
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }, cancellationToken);

    public async Task<IReadOnlyList<ApiKey>> ListKeysAsync(int limit, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ApiKeys.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(cancellationToken);
    }

    public Task<ApiKey?> UpdateKeyAsync(Guid id, ApiKeyPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
        => TransactionAsync(async db =>
        {
            var key = await db.ApiKeys.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (key is null) return null;
            key.Enabled = policy.Enabled;
            key.AllowedModels = policy.AllowedModels;
            key.RequestsPerMinute = policy.RequestsPerMinute;
            key.TokenLimit = policy.TokenLimit;
            key.BudgetUnits = policy.BudgetUnits;
            key.ExpiresAt = policy.ExpiresAt;
            key.MonthlyTokenLimit = policy.MonthlyTokenLimit;
            key.MonthlyBudgetUnits = policy.MonthlyBudgetUnits;
            key.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return key;
        }, cancellationToken);

    public Task<bool> TryReserveAsync(QuotaReservation reservation, CancellationToken cancellationToken)
        => TransactionAsync(async db =>
        {
            if (await db.Reservations.AnyAsync(existing => existing.RequestId == reservation.RequestId
                && existing.ApiKeyId == reservation.ApiKeyId, cancellationToken)) return true;
            var changed = await db.ApiKeys.Where(key => key.Id == reservation.ApiKeyId && key.Enabled
                && (key.ExpiresAt == null || key.ExpiresAt > reservation.CreatedAt)
                && (key.TokenLimit == null || key.UsedTokens + key.ReservedTokens + reservation.Tokens <= key.TokenLimit)
                && (key.BudgetUnits == null || key.SpentUnits + key.ReservedUnits + reservation.CostUnits <= key.BudgetUnits))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(key => key.ReservedTokens, key => key.ReservedTokens + reservation.Tokens)
                    .SetProperty(key => key.ReservedUnits, key => key.ReservedUnits + reservation.CostUnits), cancellationToken);
            if (changed == 0) return false;
            reservation.QuotaPeriod = Period(reservation.CreatedAt);
            await EnsureWindowAsync(db, reservation.ApiKeyId, reservation.QuotaPeriod, cancellationToken);
            var policy = await db.ApiKeys.AsNoTracking().SingleAsync(x => x.Id == reservation.ApiKeyId, cancellationToken);
            var windowId = WindowId(reservation.ApiKeyId, reservation.QuotaPeriod);
            var monthly = await db.QuotaWindows.Where(window => window.Id == windowId
                && (policy.MonthlyTokenLimit == null || window.UsedTokens + window.ReservedTokens + reservation.Tokens <= policy.MonthlyTokenLimit)
                && (policy.MonthlyBudgetUnits == null || window.SpentUnits + window.ReservedUnits + reservation.CostUnits <= policy.MonthlyBudgetUnits))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.ReservedTokens, x => x.ReservedTokens + reservation.Tokens)
                    .SetProperty(x => x.ReservedUnits, x => x.ReservedUnits + reservation.CostUnits), cancellationToken);
            if (monthly == 0)
            {
                await db.ApiKeys.Where(x => x.Id == reservation.ApiKeyId).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.ReservedTokens, x => x.ReservedTokens - reservation.Tokens)
                    .SetProperty(x => x.ReservedUnits, x => x.ReservedUnits - reservation.CostUnits), cancellationToken);
                return false;
            }
            db.Reservations.Add(reservation);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task CompleteAsync(UsageRecord record, CancellationToken cancellationToken) => TransactionAsync(async db =>
    {
        var reservation = await db.Reservations.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == record.RequestId, cancellationToken);
        if (reservation is null) return false; // Already committed by another finalizer/recovery worker.
        if (record.ApiKeyId != reservation.ApiKeyId) throw new InvalidOperationException("Reservation owner mismatch.");
        var claimed = await db.Reservations.Where(x => x.RequestId == record.RequestId).ExecuteDeleteAsync(cancellationToken);
        if (claimed == 0) return false;
        var cost = Money.ToUnits(record.EstimatedCost);
        await db.ApiKeys.Where(x => x.Id == record.ApiKeyId).ExecuteUpdateAsync(s => s
            .SetProperty(key => key.ReservedTokens, key => key.ReservedTokens - reservation.Tokens)
            .SetProperty(key => key.ReservedUnits, key => key.ReservedUnits - reservation.CostUnits)
            .SetProperty(key => key.UsedTokens, key => key.UsedTokens + record.TotalTokens)
            .SetProperty(key => key.SpentUnits, key => key.SpentUnits + cost), cancellationToken);
        var period = string.IsNullOrEmpty(reservation.QuotaPeriod) ? Period(reservation.CreatedAt) : reservation.QuotaPeriod;
        await EnsureWindowAsync(db, record.ApiKeyId, period, cancellationToken, reservation);
        var windowId = WindowId(record.ApiKeyId, period);
        await db.QuotaWindows.Where(x => x.Id == windowId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.ReservedTokens, x => x.ReservedTokens - reservation.Tokens)
            .SetProperty(x => x.ReservedUnits, x => x.ReservedUnits - reservation.CostUnits)
            .SetProperty(x => x.UsedTokens, x => x.UsedTokens + record.TotalTokens)
            .SetProperty(x => x.SpentUnits, x => x.SpentUnits + cost), cancellationToken);
        db.Attempts.AddRange(record.Attempts);
        db.Usage.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }, cancellationToken);

    public async Task<IReadOnlyList<UsageRecord>> ListUsageAsync(Guid? apiKeyId, int limit, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Usage.AsNoTracking();
        if (apiKeyId is { } id) query = query.Where(x => x.ApiKeyId == id);
        return await query.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(cancellationToken);
    }

    public async Task<int> RecoverExpiredReservationsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var expired = await db.Reservations.AsNoTracking().Where(x => x.ExpiresAt < now).OrderBy(x => x.ExpiresAt)
            .Take(100).ToListAsync(cancellationToken);
        foreach (var item in expired)
        {
            // Unknown outcomes are charged at the reservation ceiling, preventing restart-based quota bypass.
            await CompleteAsync(new UsageRecord
            {
                RequestId = item.RequestId,
                ApiKeyId = item.ApiKeyId,
                Model = item.Model,
                Operation = item.Operation,
                TotalTokens = item.Tokens,
                InputTokens = item.Tokens,
                EstimatedCost = Money.FromUnits(item.CostUnits),
                Status = "abandoned",
                ErrorCode = "reservation_expired",
                UsageEstimated = true,
                CreatedAt = item.CreatedAt
            }, cancellationToken);
        }
        return expired.Count;
    }

    private async Task<T> TransactionAsync<T>(Func<GatewayDbContext, Task<T>> operation, CancellationToken cancellationToken)
    {
        await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A fresh context per attempt avoids retaining tracked state after a rolled-back transaction.
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await operation(db);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    private static string Period(DateTimeOffset now) => now.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
    private static string WindowId(Guid key, string period) => $"{key:N}:{period}";

    private static async Task EnsureWindowAsync(GatewayDbContext db, Guid key, string period, CancellationToken ct,
        QuotaReservation? removed = null)
    {
        var id = WindowId(key, period);
        if (await db.QuotaWindows.AnyAsync(x => x.Id == id, ct)) return;
        var start = new DateTimeOffset(DateTime.ParseExact(period, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);
        var end = start.AddMonths(1);
        var usage = db.Usage.Where(x => x.ApiKeyId == key && x.CreatedAt >= start && x.CreatedAt < end);
        var reservations = db.Reservations.Where(x => x.ApiKeyId == key && x.CreatedAt >= start && x.CreatedAt < end);
        db.QuotaWindows.Add(new QuotaWindow
        {
            Id = id,
            ApiKeyId = key,
            Period = period,
            UsedTokens = await usage.SumAsync(x => x.TotalTokens, ct),
            SpentUnits = Money.ToUnits(await usage.SumAsync(x => x.EstimatedCost, ct)),
            ReservedTokens = await reservations.SumAsync(x => x.Tokens, ct) + (removed?.Tokens ?? 0),
            ReservedUnits = await reservations.SumAsync(x => x.CostUnits, ct) + (removed?.CostUnits ?? 0)
        });
        await db.SaveChangesAsync(ct);
    }

    public Task<ApiKey?> RotateKeyAsync(Guid id, string hash, string prefix, DateTimeOffset now,
        DateTimeOffset graceUntil, CancellationToken cancellationToken) => TransactionAsync(async db =>
    {
        // Lock the key before reading its previous credential. Rotation never resets allowances.
        if (await db.ApiKeys.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, now), cancellationToken) == 0) return null;
        var key = await db.ApiKeys.SingleAsync(x => x.Id == id, cancellationToken);
        await db.RetiredCredentials.Where(x => x.ApiKeyId == id).ExecuteDeleteAsync(cancellationToken);
        if (graceUntil > now) db.RetiredCredentials.Add(new RetiredKeyCredential { ApiKeyId = id, KeyHash = key.KeyHash, ExpiresAt = graceUntil });
        key.KeyHash = hash;
        key.KeyPrefix = prefix;
        await db.SaveChangesAsync(cancellationToken);
        return key;
    }, cancellationToken);
}
