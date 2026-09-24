using LLMProxy.Domain;
using LLMProxy.Infrastructure.RateLimiting;

namespace LLMProxy.UnitTests;

public sealed class RateLimitTests
{
    [Theory]
    [InlineData("key")]
    [InlineData("user")]
    [InlineData("model")]
    public async Task Each_scope_enforces_its_limit_under_concurrency(string scope)
    {
        using var limiter = new MemoryRateLimiter(new TestClock());
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => limiter.AcquireAsync([new RateLimitScope(scope, 10)], default))));
        Assert.Equal(10, results.Count(x => x.Allowed));
        Assert.All(results.Where(x => !x.Allowed), result => Assert.InRange(result.RetryAfterSeconds, 1, 60));
    }

    [Fact]
    public async Task Denied_multi_scope_requests_do_not_consume_other_allowances()
    {
        using var limiter = new MemoryRateLimiter(new TestClock());
        Assert.True((await limiter.AcquireAsync([new RateLimitScope("full", 1)], default)).Allowed);
        Assert.False((await limiter.AcquireAsync([new RateLimitScope("free", 1), new RateLimitScope("full", 1)], default)).Allowed);
        Assert.True((await limiter.AcquireAsync([new RateLimitScope("free", 1)], default)).Allowed);
    }

    [Fact]
    public async Task Windows_expire_and_new_keys_do_not_bypass_a_user_limit()
    {
        var clock = new TestClock();
        using var limiter = new MemoryRateLimiter(clock);
        Assert.True((await limiter.AcquireAsync([new RateLimitScope("key1", 5), new RateLimitScope("owner", 1)], default)).Allowed);
        Assert.False((await limiter.AcquireAsync([new RateLimitScope("key2", 5), new RateLimitScope("owner", 1)], default)).Allowed);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await limiter.AcquireAsync([new RateLimitScope("key2", 5), new RateLimitScope("owner", 1)], default)).Allowed);
    }
}
