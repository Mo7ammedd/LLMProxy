using System.Security.Cryptography;
using System.Text;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

internal sealed class ProviderKeyPool
{
    internal static readonly HttpRequestOptionsKey<string> KeyIdOption = new("LLMProxy.ProviderKeyId");
    internal static readonly HttpRequestOptionsKey<bool> MultipleKeysOption = new("LLMProxy.MultipleProviderKeys");
    private readonly Credential[] _keys;
    private readonly TimeProvider _time;
    private long _cursor = -1;
    public bool HasMultipleKeys => _keys.Length > 1;
    public int CooldownSeconds { get; }

    public ProviderKeyPool(ProviderConnectionOptions connection, TimeProvider time)
    {
        connection.ValidateApiKeys();
        var values = connection.ApiKeys.Length > 0 ? connection.ApiKeys
            : string.IsNullOrWhiteSpace(connection.ApiKey) ? [] : [connection.ApiKey];
        _keys = values.Select(value => new Credential(value)).ToArray();
        _time = time;
        CooldownSeconds = connection.ApiKeyCooldownSeconds;
    }

    public IReadOnlyList<Credential> Next()
    {
        if (_keys.Length == 0) return [];
        var available = _keys.Where(IsAvailable).ToArray();
        if (available.Length == 0) return _keys;
        var selected = available[(int)((ulong)Interlocked.Increment(ref _cursor) % (ulong)available.Length)];
        var offset = Array.IndexOf(_keys, selected);
        return Enumerable.Range(0, _keys.Length).Select(index => _keys[(offset + index) % _keys.Length]).ToArray();
    }

    public bool IsAvailable(Credential key) => !HasMultipleKeys || key.UnavailableUntil <= _time.GetUtcNow().UtcTicks;

    public void CoolDown(Credential key, int? retryAfterSeconds)
    {
        if (!HasMultipleKeys) return;
        var delay = Math.Max(CooldownSeconds, retryAfterSeconds ?? 0);
        key.CoolDown(_time.GetUtcNow().AddSeconds(delay).UtcTicks);
    }

    public ProviderException Unavailable() => new("provider_keys_unavailable", true, 503)
    {
        RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling(TimeSpan.FromTicks(
            _keys.Min(key => key.UnavailableUntil) - _time.GetUtcNow().UtcTicks).TotalSeconds))
    };

    internal sealed class Credential(string value)
    {
        private long _unavailableUntil;
        public string Value { get; } = value;
        public string Id { get; } = "key_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
        public long UnavailableUntil => Volatile.Read(ref _unavailableUntil);
        public void CoolDown(long until)
        {
            long previous;
            do
            {
                previous = UnavailableUntil;
                if (previous >= until) return;
            } while (Interlocked.CompareExchange(ref _unavailableUntil, until, previous) != previous);
        }
    }
}
