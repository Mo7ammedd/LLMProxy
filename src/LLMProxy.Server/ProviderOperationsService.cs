using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Azure.Core;
using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Providers;

namespace LLMProxy.Server;

public sealed class ProviderOperationsService(IConfiguration configuration, IProviderOperationsStore store,
    IProviderSecretProtector protector, IProviderPoolState state, IRuntimeConfiguration runtime, RuntimeCatalog catalog,
    IHttpClientFactory clients, TimeProvider time, IServiceProvider services) : IProviderOperations
{
    private bool LiveChecksEnabled => configuration.GetValue<bool>("LLMProxy:Operations:LiveChecksEnabled");
    private static void Label(string label)
    {
        if (label is null || label.Length > 128 || label.Any(char.IsControl))
            throw new GatewayException("Use a label of at most 128 characters without control characters.", "invalid_provider_key_label");
    }
    private (string Name, string Adapter, ProviderConnectionOptions Connection) Account(string provider) =>
        ProviderAccounts.Connections(ProviderAccounts.Read(configuration)).SingleOrDefault(x => x.Name == provider) is var account
            && account.Connection is not null ? account : throw new GatewayException("Provider account not found.", "provider_not_found", 404);

    public async Task<IReadOnlyList<ProviderSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await runtime.RefreshProviderKeysAsync(cancellationToken);
        var stored = await store.ListProviderKeysAsync(cancellationToken);
        var usage = (await store.ProviderUsageAsync(time.GetUtcNow().AddHours(-24), cancellationToken))
            .ToDictionary(x => (x.Provider, x.KeyId));
        var result = new List<ProviderSummary>();
        foreach (var (name, adapter, connection) in ProviderAccounts.Connections(ProviderAccounts.Read(configuration)))
        {
            var keys = Inventory(name, connection, stored);
            var statuses = (await state.ReadAsync(name, keys.Select(x => x.Id).ToArray(), false, cancellationToken)).ToDictionary(x => x.KeyId);
            result.Add(new(name, adapter, connection.BaseUrl, catalog.Providers.Single(x => x.Name == name).IsConfigured,
                protector.IsConfigured && !UsesEntra(connection), LiveChecksEnabled, connection.ApiKeyCooldownSeconds, keys.Select(key =>
                {
                    var sample = usage.GetValueOrDefault((name, key.Id));
                    return new ProviderKeySummary(key.Id, key.Label, key.Source, key.Enabled, statuses[key.Id].CooldownUntil,
                        sample?.Attempts ?? 0, sample?.Failures ?? 0, sample?.InputTokens ?? 0, sample?.OutputTokens ?? 0,
                        sample?.Cost ?? 0, sample?.AverageLatencyMs ?? 0);
                }).ToArray()));
        }
        return result;
    }

    private sealed record KeyEntry(string Id, string Label, string Source, bool Enabled, string? Value);
    private IReadOnlyList<KeyEntry> Inventory(string provider, ProviderConnectionOptions connection, IReadOnlyList<StoredProviderKey> stored,
        bool decrypt = false)
    {
        var entries = stored.Where(x => x.Provider == provider).ToDictionary(x => x.KeyId);
        var keys = ProviderAccounts.Keys(connection).Select(value =>
        {
            var id = ProviderKeyId.FromSecret(value);
            var entry = entries.GetValueOrDefault(id);
            return new KeyEntry(id, entry?.Label ?? "", "configuration", entry?.Enabled ?? true, decrypt ? value : null);
        }).ToDictionary(x => x.Id);
        foreach (var entry in entries.Values.Where(x => x.Ciphertext is not null))
            if (!keys.ContainsKey(entry.KeyId)) keys[entry.KeyId] = new(entry.KeyId, entry.Label, "managed", entry.Enabled,
                decrypt && entry.Enabled ? protector.Unprotect(entry) : null);
        return keys.Values.ToArray();
    }

    public async Task AddKeyAsync(string provider, AddProviderKey request, string actor, CancellationToken cancellationToken)
    {
        var (_, _, connection) = Account(provider);
        Label(request.Label);
        if (UsesEntra(connection)) throw new GatewayException("This account uses Entra ID instead of API keys.", "provider_uses_entra", 409);
        if (request.Key is not { Length: > 0 and <= 8192 } || request.Key.Any(c => c is < '!' or > '~'))
            throw new GatewayException("Provide a nonempty API key without whitespace (at most 8192 characters).", "invalid_provider_key");
        var id = ProviderKeyId.FromSecret(request.Key);
        await store.SaveProviderKeyAsync(new StoredProviderKey
        {
            Provider = provider,
            KeyId = id,
            Label = request.Label,
            Ciphertext = protector.Protect(provider, id, request.Key),
            Enabled = true,
            UpdatedAt = time.GetUtcNow()
        }, ProviderAccounts.Keys(connection).Select(ProviderKeyId.FromSecret).ToArray(), true, actor, cancellationToken);
        await runtime.RefreshProviderKeysAsync(cancellationToken);
    }

    public async Task UpdateKeyAsync(string provider, string keyId, UpdateProviderKey request, string actor, CancellationToken cancellationToken)
    {
        var (_, _, connection) = Account(provider);
        Label(request.Label);
        if (keyId.Length != 36 || !keyId.StartsWith("key_", StringComparison.Ordinal) || !keyId[4..].All(char.IsAsciiHexDigit))
            throw new GatewayException("Invalid provider key ID.", "invalid_provider_key_id");
        await store.SaveProviderKeyAsync(new StoredProviderKey
        {
            Provider = provider,
            KeyId = keyId,
            Label = request.Label,
            Enabled = request.Enabled,
            UpdatedAt = time.GetUtcNow()
        }, ProviderAccounts.Keys(connection).Select(ProviderKeyId.FromSecret).ToArray(), false, actor, cancellationToken);
        await runtime.RefreshProviderKeysAsync(cancellationToken);
    }

    private static bool UsesEntra(ProviderConnectionOptions connection) => connection is FoundryConnectionOptions { Authentication: FoundryAuthentication.EntraId }
        or ProviderAccountOptions { Adapter: "foundry", Authentication: FoundryAuthentication.EntraId };

    public async Task<IReadOnlyList<ProviderCheckResult>> CheckAsync(string provider, ProviderCheckRequest request, CancellationToken cancellationToken)
    {
        if (!LiveChecksEnabled) throw new GatewayException("Live provider checks are disabled. Enable LLMProxy:Operations:LiveChecksEnabled to use them.", "live_checks_disabled", 409);
        var (_, adapter, connection) = Account(provider);
        if (request.Model is not null && !catalog.Models.SelectMany(x => x.Targets).Any(x => x.Provider == provider && x.Model == request.Model))
            throw new GatewayException("Choose an upstream model configured for this provider account.", "invalid_check_model");
        var keys = Inventory(provider, connection, await store.ListProviderKeysAsync(cancellationToken), true).Where(x => x.Enabled).ToList();
        if (keys.Count == 0 && (UsesEntra(connection) || adapter == "ollama")) keys.Add(new("default", "", "identity", true, ""));
        if (keys.Count == 0) throw new GatewayException("This provider has no enabled keys.", "provider_keys_unavailable", 409);
        // A check is bounded, one attempt per key. It never changes production cooldown state.
        var results = new List<ProviderCheckResult>();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var watch = Stopwatch.StartNew();
            try
            {
                var models = request.Model is null
                    ? await DiscoverAsync(provider, adapter, connection, key.Value!, timeout.Token)
                    : await ProbeAsync(provider, adapter, connection, key.Value!, request.Model, timeout.Token);
                results.Add(new(key.Id, true, request.Model is null ? "models_available" : "model_available", 200, watch.ElapsedMilliseconds, models));
            }
            catch (GatewayException ex) { results.Add(new(key.Id, false, ex.Code, ex.StatusCode, watch.ElapsedMilliseconds, [])); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { results.Add(new(key.Id, false, "provider_check_timeout", null, watch.ElapsedMilliseconds, [])); }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or Polly.Timeout.TimeoutRejectedException)
            { results.Add(new(key.Id, false, "provider_check_failed", null, watch.ElapsedMilliseconds, [])); }
        }
        return results;
    }

    private async Task<IReadOnlyList<string>> ProbeAsync(string name, string adapter, ProviderConnectionOptions connection, string key,
        string model, CancellationToken cancellationToken)
    {
        var account = new ProviderAccountOptions
        {
            Adapter = adapter,
            BaseUrl = connection.BaseUrl,
            AllowInsecureHttp = connection.AllowInsecureHttp,
            ApiKey = key,
            ApiVersion = connection is AzureConnectionOptions azure ? azure.ApiVersion : (connection as ProviderAccountOptions)?.ApiVersion ?? "2024-10-21",
            Authentication = UsesEntra(connection) ? FoundryAuthentication.EntraId : FoundryAuthentication.ApiKey,
            TokenScope = connection is FoundryConnectionOptions foundry ? foundry.TokenScope : (connection as ProviderAccountOptions)?.TokenScope ?? "https://ai.azure.com/.default"
        };
        var provider = ProviderAccounts.CreateAccount(name, account, new CheckClients(clients), services.GetService<TokenCredential>(), time);
        if (!provider.IsConfigured) throw new GatewayException("Provider endpoint is not configured.", "provider_not_configured", 409);
        await provider.ChatCompletionAsync(new LlmRequest { Model = model, Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement("Reply OK.") }], MaxCompletionTokens = 16 }, cancellationToken);
        return [model];
    }

    private async Task<IReadOnlyList<string>> DiscoverAsync(string provider, string adapter, ProviderConnectionOptions connection,
        string key, CancellationToken cancellationToken)
    {
        if (UsesEntra(connection) || adapter == "azure")
            throw new GatewayException("Provide a configured model to run a generation check for this account.", "check_model_required", 409);
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != "https" && !(connection.AllowInsecureHttp && baseUri.Scheme == "http")
            || baseUri.UserInfo.Length > 0 || baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0)
            throw new GatewayException("Provider endpoint is not configured.", "provider_not_configured", 409);
        var endpoint = adapter == "cohere" ? new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/v1/models")
            : connection.Endpoint(adapter == "foundry" && baseUri.AbsolutePath.TrimEnd('/') == "" ? "openai/v1/models" : "models");
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (adapter == "anthropic") { message.Headers.Add("x-api-key", key); message.Headers.Add("anthropic-version", "2023-06-01"); }
        else if (adapter == "gemini") message.Headers.Add("x-goog-api-key", key);
        else if (key.Length > 0) message.Headers.Authorization = new("Bearer", key);
        using var response = await clients.CreateClient("llmproxy-check." + provider).SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new GatewayException("Provider check failed.", response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "provider_authentication_failed",
            HttpStatusCode.TooManyRequests => "provider_rate_limited",
            _ => "provider_check_failed"
        }, (int)response.StatusCode);
        await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        var root = json.RootElement;
        var collection = root.TryGetProperty("data", out var data) ? data : root.GetProperty("models");
        if (collection.ValueKind != JsonValueKind.Array) throw new JsonException();
        return collection.EnumerateArray().Take(1000).Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : x.GetProperty("name").GetString())
            .Where(x => x is { Length: > 0 and <= 256 } && !x.Any(char.IsControl) && (key.Length == 0 || !x.Contains(key, StringComparison.Ordinal)))
            .Select(x => x!).ToArray();
    }

    private sealed class CheckClients(IHttpClientFactory clients) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => clients.CreateClient(name.Replace("llmproxy.", "llmproxy-check.", StringComparison.Ordinal));
    }
}
