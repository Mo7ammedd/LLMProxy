using LLMProxy.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LLMProxy.Infrastructure;

public sealed class RetentionService(IManagementStore store, StorageOptions options, TimeProvider time,
    ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        try
        {
            do
            {
                var now = time.GetUtcNow();
                try
                {
                    await store.PruneAsync(options.UsageRetentionDays > 0 ? now.AddDays(-options.UsageRetentionDays) : null,
                        options.AuditRetentionDays > 0 ? now.AddDays(-options.AuditRetentionDays) : null, now, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError("Retention failed: {FailureType}", ex.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
