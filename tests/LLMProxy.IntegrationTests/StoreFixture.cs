using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LLMProxy.IntegrationTests;

internal sealed class StoreFixture(IDbContextFactory<GatewayDbContext> factory, string? directory) : IAsyncDisposable
{
    public IDbContextFactory<GatewayDbContext> Factory { get; } = factory;
    public IGatewayStore Store { get; } = new EfGatewayStore(factory);

    public static async Task<StoreFixture> CreateAsync(bool postgres = false)
    {
        StoreFixture fixture;
        if (postgres)
        {
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("LLMPROXY_TEST_POSTGRES"))
            { Database = "llmproxy_tests_" + Guid.NewGuid().ToString("N"), Pooling = false };
            var options = new DbContextOptionsBuilder<PostgresGatewayDbContext>()
                .UseNpgsql(connection.ToString(), provider => provider.EnableRetryOnFailure(3)).Options;
            fixture = new StoreFixture(new TestContextFactory(() => new PostgresGatewayDbContext(options)), null);
        }
        else
        {
            var directory = Path.Combine(Path.GetTempPath(), "llmproxy-store-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var options = new DbContextOptionsBuilder<SqliteGatewayDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "test.db")};Pooling=false;Default Timeout=30").Options;
            fixture = new StoreFixture(new TestContextFactory(() => new SqliteGatewayDbContext(options)), directory);
        }
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        return fixture;
    }

    public async Task<ApiKey> KeyAsync(long? tokens = null, long? budgetUnits = null)
    {
        var key = new ApiKey
        {
            KeyHash = ApiKeyHasher.Hash(ApiKeyHasher.Generate()),
            KeyPrefix = "llmp_sk_fixture",
            Owner = "test-owner",
            AllowedModels = ["fast"],
            TokenLimit = tokens,
            BudgetUnits = budgetUnits,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await Store.CreateKeyAsync(key, default);
        return key;
    }

    public static QuotaReservation Reservation(ApiKey key, long tokens = 100, long cost = 1000) => new()
    {
        RequestId = Guid.NewGuid(),
        ApiKeyId = key.Id,
        Model = "fast",
        Tokens = tokens,
        CostUnits = cost,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
    };

    public async ValueTask DisposeAsync()
    {
        await using (var db = await Factory.CreateDbContextAsync()) await db.Database.EnsureDeletedAsync();
        if (directory is not null) Directory.Delete(directory, true);
    }

    private sealed class TestContextFactory(Func<GatewayDbContext> create) : IDbContextFactory<GatewayDbContext>
    {
        public GatewayDbContext CreateDbContext() => create();
        public Task<GatewayDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(create());
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LLMPROXY_TEST_POSTGRES"))) Skip = "Set LLMPROXY_TEST_POSTGRES to run real PostgreSQL integration tests.";
    }
}

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LLMPROXY_TEST_REDIS"))) Skip = "Set LLMPROXY_TEST_REDIS to run real Redis integration tests.";
    }
}
