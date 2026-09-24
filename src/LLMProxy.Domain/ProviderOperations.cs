using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace LLMProxy.Domain;

public static class ProviderKeyId
{
    public static string FromSecret(string value) => "key_" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}

// A null ciphertext represents an override for a credential supplied by configuration.
public sealed class StoredProviderKey
{
    public string Provider { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public string? Ciphertext { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OperationsRevision
{
    public int Id { get; set; } = 1;
    public long Version { get; set; }
}

public interface IProviderSecretProtector
{
    bool IsConfigured { get; }
    string Protect(string provider, string keyId, string secret);
    string Unprotect(StoredProviderKey key);
}

public sealed record ProviderKeyUsage(string Provider, string KeyId, long Attempts, long Failures,
    long InputTokens, long OutputTokens, decimal Cost, double AverageLatencyMs);

public interface IProviderOperationsStore
{
    Task<long> RevisionAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredProviderKey>> ListProviderKeysAsync(CancellationToken cancellationToken);
    Task SaveProviderKeyAsync(StoredProviderKey key, IReadOnlyList<string> configuredKeyIds, bool create,
        string actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderKeyUsage>> ProviderUsageAsync(DateTimeOffset since, CancellationToken cancellationToken);
}

public sealed record ProviderKeyAvailability(string KeyId, DateTimeOffset? CooldownUntil, int RetryAfterSeconds);
public interface IProviderPoolState
{
    // The rotating read is atomic across replicas. Cooldown time comes from the state server.
    Task<IReadOnlyList<ProviderKeyAvailability>> ReadAsync(string provider, IReadOnlyList<string> keyIds,
        bool rotate, CancellationToken cancellationToken);
    Task CoolDownAsync(string provider, string keyId, int seconds, CancellationToken cancellationToken);
}

public sealed record ProviderKeySummary(string Id, string Label, string Source, bool Enabled,
    DateTimeOffset? CooldownUntil, long Attempts, long Failures, long InputTokens, long OutputTokens,
    decimal Cost, double AverageLatencyMs);
public sealed record ProviderSummary(string Name, string Adapter, string Endpoint, bool Configured,
    bool KeyManagementAvailable, bool LiveChecksEnabled, int CooldownSeconds, IReadOnlyList<ProviderKeySummary> Keys);
public sealed record AddProviderKey([property: JsonRequired] string Key, string Label = "");
public sealed record UpdateProviderKey([property: JsonRequired] bool Enabled, string Label = "");
public sealed record ProviderCheckRequest(string? Model = null);
public sealed record ProviderCheckResult(string KeyId, bool Success, string Code, int? HttpStatus,
    long LatencyMs, IReadOnlyList<string> Models);
public interface IProviderOperations
{
    Task<IReadOnlyList<ProviderSummary>> ListAsync(CancellationToken cancellationToken);
    Task AddKeyAsync(string provider, AddProviderKey request, string actor, CancellationToken cancellationToken);
    Task UpdateKeyAsync(string provider, string keyId, UpdateProviderKey request, string actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderCheckResult>> CheckAsync(string provider, ProviderCheckRequest request, CancellationToken cancellationToken);
}
