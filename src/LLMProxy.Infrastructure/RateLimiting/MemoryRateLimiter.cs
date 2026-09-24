using System.Collections.Concurrent;
using LLMProxy.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace LLMProxy.Infrastructure.RateLimiting;

public sealed class MemoryRateLimiter(TimeProvider time) : IRateLimiter, IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private readonly Lock _gate = new();

    public Task<RateLimitDecision> AcquireAsync(IReadOnlyList<RateLimitScope> scopes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var buckets = scopes.Select(scope =>
            {
                var bucket = _cache.Get<Bucket>(scope.Key);
                return (scope, bucket: bucket is null || bucket.Expires <= now ? new Bucket(now.AddMinutes(1)) : bucket);
            }).ToArray();
            var retry = buckets.Where(x => x.bucket.Count >= x.scope.Limit)
                .Select(x => Math.Max(1, (int)Math.Ceiling((x.bucket.Expires - now).TotalSeconds))).DefaultIfEmpty(0).Max();
            if (retry > 0) return Task.FromResult(new RateLimitDecision(false, retry));
            foreach (var item in buckets)
            {
                item.bucket.Count++;
                _cache.Set(item.scope.Key, item.bucket, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1), Size = 1 });
            }
            return Task.FromResult(new RateLimitDecision(true));
        }
    }

    public void Dispose() => _cache.Dispose();
    private sealed class Bucket(DateTimeOffset expires)
    {
        public DateTimeOffset Expires { get; } = expires;
        public int Count { get; set; }
    }
}

public sealed class MemoryRoutingState : IRoutingState
{
    private readonly ConcurrentDictionary<string, long> _cursors = new(StringComparer.Ordinal);
    public Task<long> NextAsync(string model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_cursors.AddOrUpdate(model, 0, (_, previous) => unchecked(previous + 1)));
    }
}
