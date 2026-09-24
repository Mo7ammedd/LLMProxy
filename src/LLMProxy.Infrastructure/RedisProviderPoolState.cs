using LLMProxy.Domain;
using StackExchange.Redis;

namespace LLMProxy.Infrastructure;

public sealed class RedisProviderPoolState(IConnectionMultiplexer redis, StorageOptions options) : IProviderPoolState
{
    private const string Read = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local states = {}
        local available = {}
        for i = 2, #ARGV do
            local untilAt = tonumber(redis.call('HGET', KEYS[1], ARGV[i]) or '0')
            if untilAt <= now then
                untilAt = 0
                redis.call('HDEL', KEYS[1], ARGV[i])
                table.insert(available, i - 1)
            end
            table.insert(states, untilAt)
        end
        local offset = 0
        if ARGV[1] == '1' and #available > 0 then
            local cursor = redis.call('HINCRBY', KEYS[1], 'cursor', 1) - 1
            offset = available[cursor % #available + 1] - 1
            redis.call('EXPIRE', KEYS[1], 172800)
        end
        local result = {now, offset}
        for _, value in ipairs(states) do table.insert(result, value) end
        return result
        """;
    private const string CoolDown = """
        local t = redis.call('TIME')
        local untilAt = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000) + tonumber(ARGV[2]) * 1000
        local previous = tonumber(redis.call('HGET', KEYS[1], ARGV[1]) or '0')
        if untilAt > previous then redis.call('HSET', KEYS[1], ARGV[1], untilAt) end
        redis.call('EXPIRE', KEYS[1], 172800)
        return 1
        """;

    private RedisKey Key(string provider) => $"{{{options.RedisKeyPrefix}}}:provider-pool:{provider}";
    public async Task<IReadOnlyList<ProviderKeyAvailability>> ReadAsync(string provider, IReadOnlyList<string> keyIds,
        bool rotate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(Read, [Key(provider)],
                [rotate ? 1 : 0, .. keyIds.Select(id => (RedisValue)id)]).WaitAsync(cancellationToken))!;
            var now = (long)result[0];
            var offset = (int)result[1];
            return Enumerable.Range(0, keyIds.Count).Select(i =>
            {
                var index = (offset + i) % keyIds.Count;
                var until = (long)result[index + 2];
                return new ProviderKeyAvailability(keyIds[index], until > now ? DateTimeOffset.FromUnixTimeMilliseconds(until) : null,
                    until > now ? (int)Math.Ceiling((until - now) / 1000d) : 0);
            }).ToArray();
        }
        catch (RedisException) { throw Unavailable(); }
    }

    public async Task CoolDownAsync(string provider, string keyId, int seconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(CoolDown, [Key(provider)], [keyId, Math.Clamp(seconds, 0, 86400)])
                .WaitAsync(cancellationToken);
        }
        catch (RedisException) { throw Unavailable(); }
    }
    private static GatewayException Unavailable() => new("Shared provider key state is unavailable.", "provider_state_unavailable", 503);
}
