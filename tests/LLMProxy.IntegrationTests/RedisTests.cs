using LLMProxy.Domain;
using LLMProxy.Infrastructure;
using LLMProxy.Infrastructure.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace LLMProxy.IntegrationTests;

public sealed class RedisTests
{
    [RedisFact]
    public async Task Multiple_instances_share_atomic_rate_limits_for_all_scopes()
    {
        using var first = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("LLMPROXY_TEST_REDIS")!);
        using var second = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("LLMPROXY_TEST_REDIS")!);
        var options = new StorageOptions { RedisKeyPrefix = "llmproxy-test-" + Guid.NewGuid().ToString("N") };
        IRateLimiter[] limiters = [new RedisRateLimiter(first, options), new RedisRateLimiter(second, options)];
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => limiters[i % 2].AcquireAsync(
            [new RateLimitScope($"key:{i}", 100), new RateLimitScope("user:one", 70), new RateLimitScope("model:fast", 17)], default)));
        Assert.Equal(17, results.Count(value => value.Allowed));
        Assert.All(results.Where(value => !value.Allowed), value => Assert.InRange(value.RetryAfterSeconds, 1, 60));
        // A denied model quota must not consume the still-available shared user quota.
        var otherModel = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => limiters[i % 2].AcquireAsync(
            [new RateLimitScope("user:one", 70), new RateLimitScope("model:other", 100)], default)));
        Assert.Equal(53, otherModel.Count(value => value.Allowed));
    }

    [RedisFact]
    public async Task Round_robin_cursors_are_shared_between_instances()
    {
        using var first = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("LLMPROXY_TEST_REDIS")!);
        using var second = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("LLMPROXY_TEST_REDIS")!);
        var options = new StorageOptions { RedisKeyPrefix = "llmproxy-test-" + Guid.NewGuid().ToString("N") };
        IRoutingState[] states = [new RedisRoutingState(first, options), new RedisRoutingState(second, options)];
        var cursors = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => states[i % 2].NextAsync("fast", default)));
        Assert.Equal(Enumerable.Range(0, 100).Select(x => (long)x), cursors.Order());
    }

    [Fact]
    public async Task Redis_outages_fail_closed_and_health_reports_the_dependency()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { "127.0.0.1:1" },
            AbortOnConnectFail = false,
            ConnectTimeout = 100,
            AsyncTimeout = 100,
            SyncTimeout = 100,
            ConnectRetry = 0,
            BacklogPolicy = BacklogPolicy.FailFast
        });
        var limiter = new RedisRateLimiter(redis, new StorageOptions());
        var error = await Assert.ThrowsAsync<GatewayException>(() => limiter.AcquireAsync([new RateLimitScope("key", 10)], default));
        Assert.Equal("rate_limit_unavailable", error.Code);
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(HealthStatus.Unhealthy, (await new RedisHealthCheck(redis).CheckHealthAsync(new HealthCheckContext())).Status);
    }
}
