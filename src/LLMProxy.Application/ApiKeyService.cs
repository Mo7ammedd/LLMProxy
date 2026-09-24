using System.Security.Cryptography;
using System.Text;
using LLMProxy.Domain;

namespace LLMProxy.Application;

public sealed record CreateApiKey(string Owner, string[] AllowedModels, int RequestsPerMinute = 60,
    long? TokenLimit = null, decimal? SpendingBudget = null);
public sealed record UpdateApiKey(bool Enabled, string[] AllowedModels, int RequestsPerMinute = 60,
    long? TokenLimit = null, decimal? SpendingBudget = null);
public sealed record ApiKeySummary(Guid Id, string Prefix, string Owner, bool Enabled, string[] AllowedModels,
    int RequestsPerMinute, long? TokenLimit, long UsedTokens, long ReservedTokens, decimal? SpendingBudget,
    decimal Spent, decimal ReservedCost, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastUsedAt)
{
    public static ApiKeySummary From(ApiKey key) => new(key.Id, key.KeyPrefix, key.Owner, key.Enabled,
        key.AllowedModels, key.RequestsPerMinute, key.TokenLimit, key.UsedTokens, key.ReservedTokens,
        key.BudgetUnits is { } budget ? Money.FromUnits(budget) : null, Money.FromUnits(key.SpentUnits),
        Money.FromUnits(key.ReservedUnits), key.CreatedAt, key.UpdatedAt, key.LastUsedAt);
}
public sealed record CreatedApiKey(string Key, ApiKeySummary Details);

public static class ApiKeyHasher
{
    public static string Generate() => "llmp_sk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool IsValidFormat(string? raw) => raw is { Length: >= 40 and <= 136 }
        && raw.StartsWith("llmp_sk_", StringComparison.Ordinal)
        && raw.AsSpan(8).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-".AsSpan()) < 0;

    public static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    public static bool Matches(string raw, string expectedHash) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(raw)), Convert.FromHexString(expectedHash));
}

public sealed class ApiKeyService(IGatewayStore store, IModelRegistry registry, TimeProvider time)
{
    public async Task<ApiKey?> AuthenticateAsync(string raw, CancellationToken cancellationToken)
    {
        if (!ApiKeyHasher.IsValidFormat(raw)) return null;
        var key = await store.FindKeyAsync(ApiKeyHasher.Hash(raw), cancellationToken);
        var matches = ApiKeyHasher.Matches(raw, key?.KeyHash ?? new string('0', 64));
        if (!matches || key is not { Enabled: true }) return null;
        await store.TouchKeyAsync(key.Id, time.GetUtcNow(), cancellationToken);
        return key;
    }

    public async Task<CreatedApiKey> CreateAsync(CreateApiKey command, CancellationToken cancellationToken,
        string? suppliedKey = null)
    {
        if (string.IsNullOrWhiteSpace(command.Owner) || command.Owner.Length > 128 || command.Owner.Any(char.IsControl))
            throw new GatewayException("owner must contain 1–128 printable characters.", "invalid_owner", param: "owner");
        Validate(command.AllowedModels, command.RequestsPerMinute, command.TokenLimit, command.SpendingBudget);
        var raw = suppliedKey ?? ApiKeyHasher.Generate();
        if (!ApiKeyHasher.IsValidFormat(raw))
            throw new GatewayException("Keys must start with llmp_sk_ and contain at least 32 random URL-safe characters.", "invalid_key");
        var now = time.GetUtcNow();
        var key = new ApiKey
        {
            KeyHash = ApiKeyHasher.Hash(raw),
            KeyPrefix = raw[..16],
            Owner = command.Owner,
            AllowedModels = command.AllowedModels.Distinct(StringComparer.Ordinal).ToArray(),
            RequestsPerMinute = command.RequestsPerMinute,
            TokenLimit = command.TokenLimit,
            BudgetUnits = command.SpendingBudget is { } budget ? Money.ToUnits(budget) : null,
            CreatedAt = now,
            UpdatedAt = now
        };
        await store.CreateKeyAsync(key, cancellationToken);
        return new CreatedApiKey(raw, ApiKeySummary.From(key));
    }

    public async Task<ApiKeySummary> UpdateAsync(Guid id, UpdateApiKey command, CancellationToken cancellationToken)
    {
        Validate(command.AllowedModels, command.RequestsPerMinute, command.TokenLimit, command.SpendingBudget);
        var policy = new ApiKeyPolicy(command.Enabled, command.AllowedModels.Distinct(StringComparer.Ordinal).ToArray(),
            command.RequestsPerMinute, command.TokenLimit,
            command.SpendingBudget is { } budget ? Money.ToUnits(budget) : null);
        var key = await store.UpdateKeyAsync(id, policy, time.GetUtcNow(), cancellationToken)
            ?? throw new GatewayException("API key not found.", "key_not_found", 404);
        return ApiKeySummary.From(key);
    }

    private void Validate(string[] models, int rpm, long? tokens, decimal? budget)
    {
        if (models is not { Length: > 0 } || models.Length > 100
            || models.Any(model => model != "*" && !registry.Models.Any(x => x.Name == model)))
            throw new GatewayException("allowed_models must contain configured model names or '*'.", "invalid_models", param: "allowed_models");
        if (rpm is < 1 or > 1_000_000 || tokens is < 0 or > 1_000_000_000_000_000 || budget is < 0 or > 1_000_000)
            throw new GatewayException("Invalid rate, token, or spending limit.", "invalid_limit");
    }
}
