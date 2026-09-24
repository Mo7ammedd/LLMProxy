using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace LLMProxy.Infrastructure;

public sealed class StorageInitializer(IDbContextFactory<GatewayDbContext> factory, StorageOptions options,
    ApiKeyService keys, IGatewayStore store, IConfiguration configuration)
{
    public async Task InitializeAsync(bool forceMigrate, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (options.AutoMigrate || forceMigrate) await db.Database.MigrateAsync(cancellationToken);
        var raw = configuration["LLMPROXY_BOOTSTRAP_KEY"];
        if (string.IsNullOrEmpty(raw)) return;
        if (!ApiKeyHasher.IsValidFormat(raw)) throw new InvalidOperationException("LLMPROXY_BOOTSTRAP_KEY has an invalid format.");
        var hash = ApiKeyHasher.Hash(raw);
        // Existing keys are never re-enabled or granted new limits by a restart.
        if (await store.FindKeyAsync(hash, cancellationToken) is not null) return;
        try
        {
            await keys.CreateAsync(new CreateApiKey(configuration["LLMPROXY_BOOTSTRAP_OWNER"] ?? "bootstrap", ["*"]), cancellationToken, raw);
        }
        catch (DbUpdateException)
        {
            // Concurrent first starts may insert the same bootstrap key; only that duplicate is benign.
            if (await store.FindKeyAsync(hash, cancellationToken) is null) throw;
        }
    }
}

public sealed class StorageHealthCheck(IDbContextFactory<GatewayDbContext> factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await db.ApiKeys.AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception) { return HealthCheckResult.Unhealthy("Persistent storage is unavailable or its schema is not initialized."); }
    }
}

public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception) { return HealthCheckResult.Unhealthy("Redis is unavailable."); }
    }
}

public sealed class ReservationRecoveryService(IGatewayStore store, TimeProvider time, ILogger<ReservationRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60), time);
        try
        {
            do
            {
                try
                {
                    var count = await store.RecoverExpiredReservationsAsync(time.GetUtcNow(), stoppingToken);
                    if (count > 0) logger.LogWarning("Recovered {Count} expired request reservations at their reserved allowance", count);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError("Reservation recovery unavailable: {FailureType}", ex.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
