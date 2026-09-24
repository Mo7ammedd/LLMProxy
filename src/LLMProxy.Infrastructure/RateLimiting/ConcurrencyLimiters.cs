using LLMProxy.Domain;
using StackExchange.Redis;

namespace LLMProxy.Infrastructure.RateLimiting;

public sealed class MemoryConcurrencyLimiter(TimeProvider time) : IConcurrencyLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<Guid, DateTimeOffset>> _leases = new(StringComparer.Ordinal);
    public Task<IAsyncDisposable> AcquireAsync(IReadOnlyList<ConcurrencyScope> scopes, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        lock (_gate)
        {
            var now = time.GetUtcNow();
            foreach (var key in _leases.Keys.ToArray())
            {
                foreach (var expired in _leases[key].Where(x => x.Value <= now).Select(x => x.Key).ToArray())
                    _leases[key].Remove(expired);
                if (_leases[key].Count == 0) _leases.Remove(key);
            }
            if (scopes.Any(scope => _leases.TryGetValue(scope.Key, out var leases) && leases.Count >= scope.Limit))
                throw Full();
            foreach (var scope in scopes)
            {
                if (!_leases.TryGetValue(scope.Key, out var leases)) _leases[scope.Key] = leases = [];
                leases.Add(id, now.Add(lifetime));
            }
        }
        return Task.FromResult<IAsyncDisposable>(new Lease(() =>
        {
            lock (_gate)
                foreach (var scope in scopes)
                    if (_leases.TryGetValue(scope.Key, out var leases))
                    {
                        leases.Remove(id);
                        if (leases.Count == 0) _leases.Remove(scope.Key);
                    }
            return ValueTask.CompletedTask;
        }));
    }

    internal static GatewayException Full() => new("Too many simultaneous requests. Retry shortly.", "concurrency_limit_exceeded", 429)
    { RetryAfterSeconds = 1 };
    internal sealed class Lease(Func<ValueTask> release) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _released, 1) == 0 ? release() : ValueTask.CompletedTask;
    }
}

public sealed class RedisConcurrencyLimiter(IConnectionMultiplexer redis, StorageOptions options) : IConcurrencyLimiter
{
    private const string Acquire = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        for i, key in ipairs(KEYS) do
            redis.call('ZREMRANGEBYSCORE', key, '-inf', now)
            if redis.call('ZCARD', key) >= tonumber(ARGV[i + 2]) then return 0 end
        end
        for _, key in ipairs(KEYS) do
            redis.call('ZADD', key, now + tonumber(ARGV[2]), ARGV[1])
            local ttl = redis.call('PTTL', key)
            if ttl < tonumber(ARGV[2]) then redis.call('PEXPIRE', key, ARGV[2]) end
        end
        return 1
        """;
    public async Task<IAsyncDisposable> AcquireAsync(IReadOnlyList<ConcurrencyScope> scopes, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var keys = scopes.Select(scope => (RedisKey)$"{{{options.RedisKeyPrefix}}}:concurrency:{scope.Key}").ToArray();
        RedisValue[] values = [id, (long)lifetime.TotalMilliseconds, .. scopes.Select(scope => (RedisValue)scope.Limit)];
        try
        {
            var acquired = (long)await redis.GetDatabase().ScriptEvaluateAsync(Acquire, keys, values).WaitAsync(cancellationToken);
            if (acquired == 0) throw MemoryConcurrencyLimiter.Full();
        }
        catch (RedisException) { throw new GatewayException("Distributed concurrency control is unavailable.", "concurrency_unavailable", 503); }
        return new MemoryConcurrencyLimiter.Lease(async () =>
        {
            try
            {
                await redis.GetDatabase().ScriptEvaluateAsync(
                    "for _, key in ipairs(KEYS) do redis.call('ZREM', key, ARGV[1]) end return 1", keys, [id]);
            }
            catch (RedisException) { /* Expiry releases a lease if Redis disappears during cleanup. */ }
        });
    }
}
