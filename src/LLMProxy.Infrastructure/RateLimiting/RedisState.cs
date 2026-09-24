using LLMProxy.Domain;
using StackExchange.Redis;

namespace LLMProxy.Infrastructure.RateLimiting;

public sealed class RedisRateLimiter(IConnectionMultiplexer redis, StorageOptions options) : IRateLimiter
{
    // All keys share a hash tag, so the all-or-nothing script also works on Redis Cluster.
    private const string Script = """
        local retry = 0
        for i, key in ipairs(KEYS) do
            local count = tonumber(redis.call('GET', key) or '0')
            if count >= tonumber(ARGV[i]) then
                retry = math.max(retry, redis.call('PTTL', key), 1)
            end
        end
        if retry > 0 then return retry end
        for _, key in ipairs(KEYS) do
            local count = redis.call('INCR', key)
            if count == 1 then redis.call('PEXPIRE', key, 60000) end
        end
        return 0
        """;

    public async Task<RateLimitDecision> AcquireAsync(IReadOnlyList<RateLimitScope> scopes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var keys = scopes.Select(x => (RedisKey)$"{{{options.RedisKeyPrefix}}}:rate:{x.Key}").ToArray();
            var values = scopes.Select(x => (RedisValue)x.Limit).ToArray();
            var result = (long)await redis.GetDatabase().ScriptEvaluateAsync(Script, keys, values).WaitAsync(cancellationToken);
            return result == 0 ? new RateLimitDecision(true) : new RateLimitDecision(false, Math.Max(1, (int)Math.Ceiling(result / 1000d)));
        }
        catch (RedisException) { throw new GatewayException("Distributed rate limiting is unavailable.", "rate_limit_unavailable", 503); }
    }
}

public sealed class RedisRoutingState(IConnectionMultiplexer redis, StorageOptions options) : IRoutingState
{
    public async Task<long> NextAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            var key = (RedisKey)$"{{{options.RedisKeyPrefix}}}:route:{model}";
            return (long)await redis.GetDatabase().ScriptEvaluateAsync(
                "local n = redis.call('INCR', KEYS[1]); redis.call('EXPIRE', KEYS[1], 86400); return n - 1",
                [key]).WaitAsync(cancellationToken);
        }
        catch (RedisException) { throw new GatewayException("Distributed routing state is unavailable.", "routing_unavailable", 503); }
    }
}
