using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LLMProxy.IntegrationTests;

public sealed class ManagementStoreTests
{
    [Fact] public Task Sqlite_monthly_limits_are_atomic() => MonthlyConcurrency(false);
    [PostgresFact] public Task Postgres_monthly_limits_are_atomic() => MonthlyConcurrency(true);
    private static async Task MonthlyConcurrency(bool postgres)
    {
        await using var fixture = await StoreFixture.CreateAsync(postgres);
        var key = await fixture.KeyAsync();
        await fixture.Store.UpdateKeyAsync(key.Id, new ApiKeyPolicy(true, ["fast"], 60, null, null,
            MonthlyTokenLimit: 200, MonthlyBudgetUnits: 2000), DateTimeOffset.UtcNow, default);
        var reservations = Enumerable.Range(0, 12).Select(_ => StoreFixture.Reservation(key)).ToArray();
        var outcomes = await Task.WhenAll(reservations.Select(x => fixture.Store.TryReserveAsync(x, default)));
        Assert.Equal(2, outcomes.Count(x => x));
        var saved = await fixture.Store.FindKeyByIdAsync(key.Id, default);
        Assert.Equal(200, saved!.ReservedTokens);
        var window = Assert.Single(await ((IManagementStore)fixture.Store).QuotaWindowsAsync(key.Id, default));
        Assert.Equal(200, window.ReservedTokens);
        Assert.Equal(2000, window.ReservedUnits);
    }

    [Fact] public Task Sqlite_month_boundary_preserves_inflight_reservations() => MonthBoundary(false);
    [PostgresFact] public Task Postgres_month_boundary_preserves_inflight_reservations() => MonthBoundary(true);
    private static async Task MonthBoundary(bool postgres)
    {
        await using var fixture = await StoreFixture.CreateAsync(postgres);
        var key = await fixture.KeyAsync();
        await fixture.Store.UpdateKeyAsync(key.Id, new ApiKeyPolicy(true, ["fast"], 60, 300, null, MonthlyTokenLimit: 100),
            DateTimeOffset.UtcNow, default);
        var september = StoreFixture.Reservation(key);
        september.CreatedAt = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);
        var october = StoreFixture.Reservation(key);
        october.CreatedAt = september.CreatedAt.AddSeconds(2);
        Assert.True(await fixture.Store.TryReserveAsync(september, default));
        Assert.True(await fixture.Store.TryReserveAsync(october, default));
        await fixture.Store.CompleteAsync(new UsageRecord
        {
            RequestId = september.RequestId,
            ApiKeyId = key.Id,
            Model = "fast",
            TotalTokens = 20,
            InputTokens = 20,
            CreatedAt = september.CreatedAt,
            Status = "success"
        }, default);
        var windows = await ((IManagementStore)fixture.Store).QuotaWindowsAsync(key.Id, default);
        Assert.Equal(20, windows.Single(x => x.Period == "2026-09").UsedTokens);
        Assert.Equal(0, windows.Single(x => x.Period == "2026-09").ReservedTokens);
        Assert.Equal(100, windows.Single(x => x.Period == "2026-10").ReservedTokens);
        var saved = await fixture.Store.FindKeyByIdAsync(key.Id, default);
        Assert.Equal(20, saved!.UsedTokens);
        Assert.Equal(100, saved.ReservedTokens);
    }

    [Fact]
    public async Task Rotation_preserves_counters_and_expires_the_previous_credential()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var clock = new MutableClock();
        var store = new EfGatewayStore(fixture.Factory, clock);
        var registry = new ModelRegistry(new GatewayOptions
        {
            Models = new()
            { ["fast"] = new() { Providers = ["openai"], ProviderModels = new() { ["openai"] = "model" } } }
        });
        var service = new ApiKeyService(store, registry, clock);
        var created = await service.CreateAsync(new CreateApiKey("owner", ["fast"], MonthlyTokenLimit: 1000,
            ExpiresAt: clock.GetUtcNow().AddHours(2)), default);
        var key = (await store.FindKeyByIdAsync(created.Details.Id, default))!;
        var reservation = StoreFixture.Reservation(key);
        Assert.True(await store.TryReserveAsync(reservation, default));
        var rotated = await service.RotateAsync(key.Id, 60, default);
        Assert.Equal(key.Id, rotated.Details.Id);
        Assert.Equal(100, rotated.Details.ReservedTokens);
        Assert.NotNull(await service.AuthenticateAsync(created.Key, default));
        Assert.NotNull(await service.AuthenticateAsync(rotated.Key, default));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(await service.AuthenticateAsync(created.Key, default));
        Assert.NotNull(await service.AuthenticateAsync(rotated.Key, default));
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Null(await service.AuthenticateAsync(rotated.Key, default));
    }

    [Fact] public Task Sqlite_reports_have_stable_cursors_and_filters() => Reports(false);
    [PostgresFact] public Task Postgres_reports_have_stable_cursors_and_filters() => Reports(true);
    private static async Task Reports(bool postgres)
    {
        await using var fixture = await StoreFixture.CreateAsync(postgres);
        var key = await fixture.KeyAsync();
        var timestamp = DateTimeOffset.UtcNow;
        for (var i = 0; i < 7; i++) await AddUsage(fixture, key, timestamp);
        var management = (IManagementStore)fixture.Store;
        var ids = new HashSet<Guid>();
        string? cursor = null;
        do
        {
            var page = await management.QueryUsageAsync(new UsageQuery(Owner: key.Owner, From: timestamp.AddSeconds(-1),
                To: timestamp.AddSeconds(1), Limit: 2, Cursor: cursor), default);
            foreach (var record in page.Data) Assert.True(ids.Add(record.RequestId));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(7, ids.Count);
        Assert.Empty((await management.QueryUsageAsync(new UsageQuery(Owner: "missing"), default)).Data);
        var summary = Assert.Single(await management.SummarizeUsageAsync(new UsageQuery(), default));
        Assert.Equal(7, summary.Requests);
        Assert.Equal(.007m, summary.Cost);
    }

    [Fact] public Task Sqlite_reconciliation_is_idempotent() => Reconciliation(false);
    [PostgresFact] public Task Postgres_reconciliation_is_idempotent() => Reconciliation(true);
    private static async Task Reconciliation(bool postgres)
    {
        await using var fixture = await StoreFixture.CreateAsync(postgres);
        var key = await fixture.KeyAsync();
        var record = await AddUsage(fixture, key, DateTimeOffset.UtcNow);
        var management = (IManagementStore)fixture.Store;
        var attempt = Assert.Single(await management.ListAttemptsAsync(record.RequestId, default));
        Assert.Equal("key_0123456789abcdef0123456789abcdef", attempt.ProviderKeyId);
        ReconciliationInput[] input = [new(attempt.Id, .05m, "invoice-001/line-1")];
        await management.ReconcileAsync(input, "operator", DateTimeOffset.UtcNow, default);
        await management.ReconcileAsync(input, "operator", DateTimeOffset.UtcNow, default);
        Assert.Equal(attempt.ProviderKeyId, Assert.Single(await management.ListAttemptsAsync(record.RequestId, default)).ProviderKeyId);
        Assert.Equal(.05m, Money.FromUnits((await fixture.Store.FindKeyByIdAsync(key.Id, default))!.SpentUnits));
        Assert.Equal(.05m, (await fixture.Store.ListUsageAsync(key.Id, 10, default))[0].EstimatedCost);
        var conflict = await Assert.ThrowsAsync<GatewayException>(() => management.ReconcileAsync(
            [new(attempt.Id, .10m, "invoice-001/line-1")], "operator", DateTimeOffset.UtcNow, default));
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task Retention_does_not_reset_key_or_monthly_allowances()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = await fixture.KeyAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-100);
        await AddUsage(fixture, key, old);
        var management = (IManagementStore)fixture.Store;
        var before = await fixture.Store.FindKeyByIdAsync(key.Id, default);
        await management.PruneAsync(DateTimeOffset.UtcNow.AddDays(-90), null, DateTimeOffset.UtcNow, default);
        Assert.Empty(await fixture.Store.ListUsageAsync(key.Id, 10, default));
        var after = await fixture.Store.FindKeyByIdAsync(key.Id, default);
        Assert.Equal(before!.UsedTokens, after!.UsedTokens);
        Assert.Equal(before.SpentUnits, after.SpentUnits);
        Assert.Equal(10, (await management.QuotaWindowsAsync(key.Id, default)).Sum(x => x.UsedTokens));
    }

    [Fact]
    public async Task Sqlite_online_backup_restores_keys_and_usage()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = await fixture.KeyAsync();
        await AddUsage(fixture, key, DateTimeOffset.UtcNow);
        var path = Path.Combine(Path.GetTempPath(), "llmproxy-backup-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using (var db = await fixture.Factory.CreateDbContextAsync())
            await using (var backup = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                await db.Database.OpenConnectionAsync();
                await backup.OpenAsync();
                ((SqliteConnection)db.Database.GetDbConnection()).BackupDatabase(backup);
            }
            await fixture.Store.UpdateKeyAsync(key.Id, new ApiKeyPolicy(false, ["fast"], 60, null, null), DateTimeOffset.UtcNow, default);
            await using var restored = new SqliteGatewayDbContext(new DbContextOptionsBuilder<SqliteGatewayDbContext>()
                .UseSqlite($"Data Source={path};Pooling=false").Options);
            Assert.True((await restored.ApiKeys.SingleAsync()).Enabled);
            Assert.Equal(1, await restored.Usage.CountAsync());
            Assert.Equal(1, await restored.Attempts.CountAsync());
        }
        finally { File.Delete(path); }
    }

    private static async Task<UsageRecord> AddUsage(StoreFixture fixture, ApiKey key, DateTimeOffset timestamp)
    {
        var reservation = StoreFixture.Reservation(key);
        reservation.CreatedAt = timestamp;
        Assert.True(await fixture.Store.TryReserveAsync(reservation, default));
        var record = new UsageRecord
        {
            RequestId = reservation.RequestId,
            ApiKeyId = key.Id,
            Model = "fast",
            Provider = "openai",
            TotalTokens = 10,
            InputTokens = 6,
            OutputTokens = 4,
            EstimatedCost = .001m,
            CreatedAt = timestamp,
            Status = "success"
        };
        record.Attempts.Add(new UpstreamAttempt
        {
            RequestId = record.RequestId,
            Provider = "openai",
            Model = "upstream",
            ProviderKeyId = "key_0123456789abcdef0123456789abcdef",
            InputTokens = 6,
            OutputTokens = 4,
            EstimatedCost = .001m,
            Status = "success",
            CreatedAt = timestamp
        });
        await fixture.Store.CompleteAsync(record, default);
        return record;
    }
}

internal sealed class MutableClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}
