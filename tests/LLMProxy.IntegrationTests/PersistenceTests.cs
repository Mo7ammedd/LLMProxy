using LLMProxy.Domain;
using Microsoft.EntityFrameworkCore;

namespace LLMProxy.IntegrationTests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task Sqlite_migrations_and_concurrent_token_reservations_work()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await AssertConcurrentQuota(fixture, useBudget: false);
    }

    [Fact]
    public async Task Sqlite_spending_reservations_prevent_concurrent_overspend()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await AssertConcurrentQuota(fixture, useBudget: true);
    }

    [PostgresFact]
    public async Task Postgres_migrations_and_concurrent_token_reservations_work()
    {
        await using var fixture = await StoreFixture.CreateAsync(postgres: true);
        await AssertConcurrentQuota(fixture, useBudget: false);
    }

    [PostgresFact]
    public async Task Postgres_spending_reservations_prevent_concurrent_overspend()
    {
        await using var fixture = await StoreFixture.CreateAsync(postgres: true);
        await AssertConcurrentQuota(fixture, useBudget: true);
    }

    [Fact]
    public async Task Usage_and_allowance_updates_commit_once_even_with_duplicate_finalizers()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await AssertIdempotentFinalization(fixture);
    }

    [PostgresFact]
    public async Task Postgres_finalization_is_idempotent_under_concurrency()
    {
        await using var fixture = await StoreFixture.CreateAsync(postgres: true);
        await AssertIdempotentFinalization(fixture);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("embeddings")]
    [InlineData("responses")]
    public Task Expired_reservations_become_conservative_usage_records(string operation)
        => AssertRecoveredOperation(false, operation);

    [PostgresFact]
    public Task Postgres_recovery_preserves_the_original_operation()
        => AssertRecoveredOperation(true, "embeddings");

    private static async Task AssertRecoveredOperation(bool postgres, string operation)
    {
        await using var fixture = await StoreFixture.CreateAsync(postgres);
        var key = await fixture.KeyAsync();
        var reservation = StoreFixture.Reservation(key);
        reservation.Operation = operation;
        reservation.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.True(await fixture.Store.TryReserveAsync(reservation, default));
        Assert.Equal(1, await fixture.Store.RecoverExpiredReservationsAsync(DateTimeOffset.UtcNow, default));
        Assert.Equal(0, await fixture.Store.RecoverExpiredReservationsAsync(DateTimeOffset.UtcNow, default));
        var updated = await fixture.Store.FindKeyByIdAsync(key.Id, default);
        Assert.Equal(0, updated!.ReservedTokens);
        Assert.Equal(100, updated.UsedTokens);
        Assert.Equal(1000, updated.SpentUnits);
        var usage = Assert.Single(await fixture.Store.ListUsageAsync(key.Id, 100, default));
        Assert.Equal("abandoned", usage.Status);
        Assert.Equal(operation, usage.Operation);
        Assert.True(usage.UsageEstimated);
    }

    [Fact]
    public async Task Disabling_a_key_prevents_new_reservations()
    {
        var fixture = await StoreFixture.CreateAsync();
        await using (fixture)
        {
            var key = await fixture.KeyAsync();
            await fixture.Store.UpdateKeyAsync(key.Id, new ApiKeyPolicy(false, ["fast"], 60, null, null), DateTimeOffset.UtcNow, default);
            Assert.False(await fixture.Store.TryReserveAsync(StoreFixture.Reservation(key), default));
        }
    }

    private static async Task AssertConcurrentQuota(StoreFixture fixture, bool useBudget)
    {
        var key = await fixture.KeyAsync(tokens: useBudget ? null : 1700, budgetUnits: useBudget ? 17_000 : null);
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
            fixture.Store.TryReserveAsync(StoreFixture.Reservation(key), default))));
        Assert.Equal(17, results.Count(value => value));
        var updated = await fixture.Store.FindKeyByIdAsync(key.Id, default);
        Assert.Equal(1700, updated!.ReservedTokens);
        Assert.Equal(17_000, updated.ReservedUnits);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(17, await db.Reservations.CountAsync());
    }

    private static async Task AssertIdempotentFinalization(StoreFixture fixture)
    {
        var key = await fixture.KeyAsync();
        var reservation = StoreFixture.Reservation(key);
        Assert.True(await fixture.Store.TryReserveAsync(reservation, default));
        var record = new UsageRecord
        {
            RequestId = reservation.RequestId,
            ApiKeyId = key.Id,
            Model = "fast",
            Provider = "fake",
            InputTokens = 3,
            OutputTokens = 2,
            TotalTokens = 5,
            Status = "success",
            EstimatedCost = 0.0000005m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => fixture.Store.CompleteAsync(record, default))));
        var updated = await fixture.Store.FindKeyByIdAsync(key.Id, default);
        Assert.Equal(5, updated!.UsedTokens);
        Assert.Equal(500, updated.SpentUnits);
        Assert.Equal(0, updated.ReservedUnits);
        Assert.Equal(0, updated.ReservedTokens);
        Assert.Single(await fixture.Store.ListUsageAsync(key.Id, 100, default));
    }
}
