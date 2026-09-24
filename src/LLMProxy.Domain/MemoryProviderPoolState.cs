namespace LLMProxy.Domain;

public sealed class MemoryProviderPoolState(TimeProvider time) : IProviderPoolState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _cursors = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Provider, string Key), DateTimeOffset> _cooldowns = [];

    public Task<IReadOnlyList<ProviderKeyAvailability>> ReadAsync(string provider, IReadOnlyList<string> keyIds,
        bool rotate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = time.GetUtcNow();
            foreach (var expired in _cooldowns.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
                _cooldowns.Remove(expired);
            var states = keyIds.Select(id =>
            {
                var until = _cooldowns.GetValueOrDefault((provider, id));
                return new ProviderKeyAvailability(id, until > now ? until : null,
                    until > now ? (int)Math.Ceiling((until - now).TotalSeconds) : 0);
            }).ToArray();
            var available = states.Where(x => x.RetryAfterSeconds == 0).ToArray();
            if (!rotate || available.Length == 0) return Task.FromResult<IReadOnlyList<ProviderKeyAvailability>>(states);
            var cursor = _cursors.GetValueOrDefault(provider);
            _cursors[provider] = cursor == long.MaxValue ? 0 : cursor + 1;
            var offset = Array.IndexOf(states, available[(int)(cursor % available.Length)]);
            return Task.FromResult<IReadOnlyList<ProviderKeyAvailability>>(
                Enumerable.Range(0, states.Length).Select(i => states[(offset + i) % states.Length]).ToArray());
        }
    }

    public Task CoolDownAsync(string provider, string keyId, int seconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var until = time.GetUtcNow().AddSeconds(Math.Clamp(seconds, 0, 86400));
            if (until > _cooldowns.GetValueOrDefault((provider, keyId))) _cooldowns[(provider, keyId)] = until;
        }
        return Task.CompletedTask;
    }
}
