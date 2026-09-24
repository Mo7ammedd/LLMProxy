using LLMProxy.Domain;

namespace LLMProxy.Providers;

internal sealed class ProviderKeyPool
{
    internal static readonly HttpRequestOptionsKey<string> KeyIdOption = new("LLMProxy.ProviderKeyId");
    internal static readonly HttpRequestOptionsKey<bool> MultipleKeysOption = new("LLMProxy.MultipleProviderKeys");
    private readonly Credential[] _keys;
    private readonly IProviderPoolState _state;
    private readonly string _provider;
    public bool HasMultipleKeys => _keys.Length > 1;
    public int CooldownSeconds { get; }

    public ProviderKeyPool(ProviderConnectionOptions connection, IProviderPoolState state, string provider)
    {
        connection.ValidateApiKeys();
        var values = connection.ApiKeys.Length > 0 ? connection.ApiKeys
            : string.IsNullOrWhiteSpace(connection.ApiKey) ? [] : [connection.ApiKey];
        _keys = values.Select(value => new Credential(value)).ToArray();
        _state = state;
        _provider = provider;
        CooldownSeconds = connection.ApiKeyCooldownSeconds;
    }

    public async Task<IReadOnlyList<Credential>> NextAsync(CancellationToken cancellationToken)
    {
        if (!HasMultipleKeys) return _keys;
        var ordered = await _state.ReadAsync(_provider, _keys.Select(x => x.Id).ToArray(), true, cancellationToken);
        return ordered.Select(status => _keys.Single(key => key.Id == status.KeyId)).ToArray();
    }

    public async Task<bool> IsAvailableAsync(Credential key, CancellationToken cancellationToken) => !HasMultipleKeys
        || (await _state.ReadAsync(_provider, [key.Id], false, cancellationToken))[0].RetryAfterSeconds == 0;

    public Task CoolDownAsync(Credential key, int? retryAfterSeconds, CancellationToken cancellationToken) => !HasMultipleKeys
        ? Task.CompletedTask : _state.CoolDownAsync(_provider, key.Id, Math.Max(CooldownSeconds, retryAfterSeconds ?? 0), cancellationToken);

    public async Task<ProviderException> UnavailableAsync(CancellationToken cancellationToken)
    {
        var states = await _state.ReadAsync(_provider, _keys.Select(x => x.Id).ToArray(), false, cancellationToken);
        return new("provider_keys_unavailable", true, 503) { RetryAfterSeconds = Math.Max(1, states.Min(x => x.RetryAfterSeconds)) };
    }

    internal sealed class Credential(string value)
    {
        public string Value { get; } = value;
        public string Id { get; } = ProviderKeyId.FromSecret(value);
    }
}
