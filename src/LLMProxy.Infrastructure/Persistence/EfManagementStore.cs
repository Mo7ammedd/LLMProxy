using System.Globalization;
using System.Text;
using LLMProxy.Domain;
using Microsoft.EntityFrameworkCore;

namespace LLMProxy.Infrastructure.Persistence;

public sealed partial class EfGatewayStore
{
    public async Task<OperatorAccount?> FindOperatorAsync(string username, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Operators.AsNoTracking().SingleOrDefaultAsync(x => x.Username == username, cancellationToken);
    }

    public async Task<IReadOnlyList<OperatorAccount>> ListOperatorsAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Operators.AsNoTracking().OrderBy(x => x.Username).Take(1000).ToListAsync(cancellationToken);
    }

    public Task SaveOperatorAsync(OperatorAccount account, bool create, CancellationToken cancellationToken)
        => TransactionAsync(async db =>
        {
            if (create)
            {
                if (await db.Operators.AnyAsync(x => x.Username == account.Username, cancellationToken))
                    throw new GatewayException("An operator with this username already exists.", "operator_exists", 409);
                db.Operators.Add(account);
            }
            else
            {
                // Serialize role changes across administrators to preserve the final administrator.
                await db.Operators.Where(x => x.Role == OperatorRoles.Administrator)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, x => x.Enabled), cancellationToken);
                var existing = await db.Operators.SingleOrDefaultAsync(x => x.Id == account.Id, cancellationToken)
                    ?? throw new GatewayException("Operator not found.", "not_found", 404);
                if (existing.Enabled && existing.Role == OperatorRoles.Administrator
                    && (!account.Enabled || account.Role != OperatorRoles.Administrator)
                    && await db.Operators.CountAsync(x => x.Enabled && x.Role == OperatorRoles.Administrator, cancellationToken) <= 1)
                    throw new GatewayException("Keep at least one enabled administrator.", "last_administrator", 409);
                existing.Enabled = account.Enabled;
                existing.Role = account.Role;
                existing.PasswordHash = account.PasswordHash;
                await db.Sessions.Where(x => x.OperatorId == account.Id).ExecuteDeleteAsync(cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public async Task<OperatorAccount?> AuthenticateOperatorAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await (from session in db.Sessions
                      join account in db.Operators on session.OperatorId equals account.Id
                      where session.TokenHash == tokenHash && session.ExpiresAt > now && account.Enabled
                      select account).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }

    public async Task SaveSessionAsync(OperatorSession session, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteSessionAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Sessions.Where(x => x.TokenHash == tokenHash).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task AppendAuditAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Audit.Add(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Page<AuditRecord>> ListAuditAsync(string? cursor, int limit, CancellationToken cancellationToken)
    {
        CheckLimit(limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Audit.AsNoTracking();
        if (Decode(cursor) is { } after)
            query = query.Where(x => x.CreatedAt < after.Time || x.CreatedAt == after.Time && x.Id.CompareTo(after.Id) < 0);
        return Paginate(await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken),
            limit, x => x.CreatedAt, x => x.Id);
    }

    public async Task<Page<UsageRecord>> QueryUsageAsync(UsageQuery query, CancellationToken cancellationToken)
    {
        CheckQuery(query);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = FilterUsage(db, query);
        if (Decode(query.Cursor) is { } after)
            rows = rows.Where(x => x.CreatedAt < after.Time || x.CreatedAt == after.Time && x.RequestId.CompareTo(after.Id) < 0);
        return Paginate(await rows.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.RequestId)
            .Take(query.Limit + 1).ToListAsync(cancellationToken), query.Limit, x => x.CreatedAt, x => x.RequestId);
    }

    public async Task<IReadOnlyList<UsageRollup>> SummarizeUsageAsync(UsageQuery query, CancellationToken cancellationToken)
    {
        CheckQuery(query);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await FilterUsage(db, query).GroupBy(x => new { x.Model, x.Provider })
            .OrderBy(group => group.Key.Model).ThenBy(group => group.Key.Provider).Select(group =>
            new UsageRollup(group.Key.Model, group.Key.Provider, group.LongCount(), group.Sum(x => x.InputTokens),
                group.Sum(x => x.OutputTokens), group.Sum(x => x.EstimatedCost), group.Average(x => (double)x.LatencyMs)))
            .ToListAsync(cancellationToken);
    }

    public async Task<Page<ApiKey>> QueryKeysAsync(string? owner, string? cursor, int limit, CancellationToken cancellationToken)
    {
        CheckLimit(limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.ApiKeys.AsNoTracking();
        if (owner is not null) query = query.Where(x => x.Owner == owner);
        if (Decode(cursor) is { } after)
            query = query.Where(x => x.CreatedAt < after.Time || x.CreatedAt == after.Time && x.Id.CompareTo(after.Id) < 0);
        return Paginate(await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken),
            limit, x => x.CreatedAt, x => x.Id);
    }

    public async Task<IReadOnlyList<QuotaWindow>> QuotaWindowsAsync(Guid keyId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.QuotaWindows.AsNoTracking().Where(x => x.ApiKeyId == keyId).OrderByDescending(x => x.Period)
            .Take(120).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UpstreamAttempt>> ListAttemptsAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Attempts.AsNoTracking().Where(x => x.RequestId == requestId).OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id).ToListAsync(cancellationToken);
    }

    public Task ReconcileAsync(IReadOnlyList<ReconciliationInput> records, string actor, DateTimeOffset now, CancellationToken cancellationToken)
        => TransactionAsync(async db =>
        {
            if (records.Count is < 1 or > 1000 || records.Select(x => x.AttemptId).Distinct().Count() != records.Count
                || records.Select(x => x.Reference).Distinct(StringComparer.Ordinal).Count() != records.Count
                || records.Any(x => x.ActualCost is < 0 or > 1_000_000 || string.IsNullOrWhiteSpace(x.Reference) || x.Reference.Length > 128))
                throw new GatewayException("Provide 1–1000 unique attempt IDs, references and nonnegative costs.", "invalid_reconciliation");
            foreach (var record in records.OrderBy(x => x.AttemptId))
            {
                await db.Attempts.Where(x => x.Id == record.AttemptId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, x => x.Status), cancellationToken);
                var receipts = await db.Reconciliations.Where(x => x.Reference == record.Reference || x.AttemptId == record.AttemptId).ToListAsync(cancellationToken);
                if (receipts.Count > 0)
                {
                    if (receipts.Count == 1 && receipts[0].Reference == record.Reference && receipts[0].AttemptId == record.AttemptId
                        && receipts[0].ActualCost == record.ActualCost) continue;
                    throw new GatewayException("This attempt or reference was already reconciled with different values.", "reconciliation_conflict", 409);
                }
                var attempt = await db.Attempts.SingleOrDefaultAsync(x => x.Id == record.AttemptId, cancellationToken)
                    ?? throw new GatewayException("Upstream attempt not found.", "attempt_not_found", 404);
                var usage = await db.Usage.SingleOrDefaultAsync(x => x.RequestId == attempt.RequestId, cancellationToken)
                    ?? throw new GatewayException("Usage is outside the retained reconciliation window.", "usage_not_found", 404);
                var delta = Money.ToUnits(record.ActualCost) - Money.ToUnits(attempt.EstimatedCost);
                await db.ApiKeys.Where(x => x.Id == usage.ApiKeyId).ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.SpentUnits, x => x.SpentUnits + delta), cancellationToken);
                var period = Period(usage.CreatedAt);
                await EnsureWindowAsync(db, usage.ApiKeyId, period, cancellationToken);
                var windowId = WindowId(usage.ApiKeyId, period);
                await db.QuotaWindows.Where(x => x.Id == windowId).ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.SpentUnits, x => x.SpentUnits + delta), cancellationToken);
                attempt.ActualCost = record.ActualCost;
                usage.EstimatedCost += Money.FromUnits(delta);
                db.Reconciliations.Add(new ReconciliationRecord
                { Reference = record.Reference, AttemptId = attempt.Id, ActualCost = record.ActualCost, Actor = actor, CreatedAt = now });
                await db.SaveChangesAsync(cancellationToken);
            }
            return true;
        }, cancellationToken);

    public async Task PruneAsync(DateTimeOffset? usageBefore, DateTimeOffset? auditBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (usageBefore is { } usage)
        {
            await db.Attempts.Where(x => x.CreatedAt < usage).ExecuteDeleteAsync(cancellationToken);
            await db.Usage.Where(x => x.CreatedAt < usage).ExecuteDeleteAsync(cancellationToken);
        }
        if (auditBefore is { } audit) await db.Audit.Where(x => x.CreatedAt < audit).ExecuteDeleteAsync(cancellationToken);
        await db.Sessions.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        await db.RetiredCredentials.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
    }

    private static IQueryable<UsageRecord> FilterUsage(GatewayDbContext db, UsageQuery query)
    {
        var rows = db.Usage.AsNoTracking();
        if (query.ApiKeyId is { } id) rows = rows.Where(x => x.ApiKeyId == id);
        if (query.Owner is { } owner) rows = rows.Where(x => db.ApiKeys.Any(key => key.Id == x.ApiKeyId && key.Owner == owner));
        if (query.From is { } from) rows = rows.Where(x => x.CreatedAt >= from);
        if (query.To is { } to) rows = rows.Where(x => x.CreatedAt < to);
        if (query.Model is { } model) rows = rows.Where(x => x.Model == model);
        if (query.Provider is { } provider) rows = rows.Where(x => x.Provider == provider);
        if (query.Status is { } status) rows = rows.Where(x => x.Status == status);
        return rows;
    }

    private static void CheckQuery(UsageQuery query)
    {
        CheckLimit(query.Limit);
        if (query.From >= query.To) throw new GatewayException("from must precede to.", "invalid_date_range");
    }
    private static void CheckLimit(int limit)
    {
        if (limit is < 1 or > 1000) throw new GatewayException("limit must be 1–1000.", "invalid_limit");
    }
    private static Page<T> Paginate<T>(List<T> rows, int limit, Func<T, DateTimeOffset> date, Func<T, Guid> id)
    {
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        return new(rows, more ? Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{date(rows[^1]).UtcTicks.ToString(CultureInfo.InvariantCulture)}:{id(rows[^1]):N}")) : null);
    }
    private static (DateTimeOffset Time, Guid Id)? Decode(string? cursor)
    {
        if (cursor is null) return null;
        try
        {
            if (cursor.Length > 128) throw new FormatException();
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            if (parts.Length != 2) throw new FormatException();
            return (new DateTimeOffset(long.Parse(parts[0], CultureInfo.InvariantCulture), TimeSpan.Zero), Guid.ParseExact(parts[1], "N"));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        { throw new GatewayException("Invalid pagination cursor.", "invalid_cursor"); }
    }
}
