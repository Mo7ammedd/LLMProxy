using LLMProxy.Infrastructure;
using LLMProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LLMProxy.IntegrationTests;

public sealed class DependencyHealthTests
{
    [Fact]
    public async Task Unreachable_postgres_has_a_sanitized_unhealthy_result()
    {
        var check = new StorageHealthCheck(new UnavailablePostgresFactory());
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("storage", result.Description!);
        Assert.DoesNotContain("fixture-password", result.Description!);
        Assert.Null(result.Exception);
    }

    private sealed class UnavailablePostgresFactory : IDbContextFactory<GatewayDbContext>
    {
        public GatewayDbContext CreateDbContext() => new PostgresGatewayDbContext(new DbContextOptionsBuilder<PostgresGatewayDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unreachable;Username=fixture;Password=fixture-password;Timeout=1").Options);
        public Task<GatewayDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
