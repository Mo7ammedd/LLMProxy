using Azure.Core;
using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Providers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LLMProxy.Server;

public sealed class RuntimeConfigurationService(IConfiguration configuration, RuntimeCatalog catalog,
    IHttpClientFactory clients, IEnumerable<IRoutingStrategy> strategies, IServiceProvider services,
    IProviderOperationsStore store, IProviderSecretProtector protector) : IRuntimeConfiguration
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation = 1;
    private long _revision = -1;
    public Task<ConfigurationReloadResult> ReloadAsync(CancellationToken cancellationToken) => RebuildAsync(true, cancellationToken);

    public async Task RefreshProviderKeysAsync(CancellationToken cancellationToken)
    {
        if (await store.RevisionAsync(cancellationToken) != Volatile.Read(ref _revision))
            await RebuildAsync(false, cancellationToken);
    }

    private async Task<ConfigurationReloadResult> RebuildAsync(bool reloadSources, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (reloadSources && configuration is IConfigurationRoot root) root.Reload();
            var revision = await store.RevisionAsync(cancellationToken);
            var gateway = configuration.GetSection("LLMProxy").Get<GatewayOptions>() ?? new();
            var options = ProviderAccounts.Read(configuration);
            ApplyKeys(options, await store.ListProviderKeysAsync(cancellationToken), protector);
            var registry = new ModelRegistry(gateway);
            var pricing = new ConfiguredPricing(gateway);
            var state = services.GetRequiredService<IProviderPoolState>();
            var transport = new ProviderHttpTransport(clients, services.GetService<TimeProvider>(), state);
            var credential = services.GetService<TokenCredential>();
            var providers = ProviderAccounts.BuiltIns.Select(name => ProviderAccounts.Create(name, options, transport, credential))
                .Concat(options.Accounts.Select(pair => ProviderAccounts.CreateAccount(pair.Key, pair.Value, clients, credential,
                    services.GetService<TimeProvider>(), state))).ToArray();
            _ = new ModelRouter(registry, providers, strategies);
            foreach (var target in registry.Models.SelectMany(model => model.Targets))
                pricing.GetPrice($"{target.Provider}/{target.Model}", 0);
            cancellationToken.ThrowIfCancellationRequested();
            catalog.Replace(new CatalogSnapshot(registry, pricing, providers));
            Volatile.Write(ref _revision, revision);
            return new(++_generation, registry.Models.Count, providers.Length);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or IOException)
        { throw new GatewayException("Configuration validation failed. The previous catalog remains active.", "invalid_configuration"); }
        finally { _gate.Release(); }
    }

    internal static void ApplyKeys(ProviderOptions options, IReadOnlyList<StoredProviderKey> stored, IProviderSecretProtector protector)
    {
        foreach (var (name, _, connection) in ProviderAccounts.Connections(options))
        {
            var overrides = stored.Where(x => x.Provider == name).ToDictionary(x => x.KeyId);
            var configured = ProviderAccounts.Keys(connection);
            var keys = configured.Where(value => !overrides.TryGetValue(ProviderKeyId.FromSecret(value), out var key) || key.Enabled)
                .Concat(overrides.Values.Where(x => x.Enabled && x.Ciphertext is not null).Select(protector.Unprotect))
                .Distinct(StringComparer.Ordinal).ToArray();
            connection.ApiKey = "";
            connection.ApiKeys = keys;
            // Preserve the original single-key header used by adapters before transport replacement.
            if (keys.Length == 1) connection.ApiKey = keys[0];
            if (keys.Length > 64) throw new InvalidOperationException("Too many provider keys.");
        }
    }
}

public sealed class ProviderKeyRefreshWorker(IRuntimeConfiguration configuration, ILogger<ProviderKeyRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await configuration.RefreshProviderKeysAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError("Provider key refresh failed: {FailureType}.", ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

public sealed class ProviderKeyConfigurationHealthCheck(IRuntimeConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await configuration.RefreshProviderKeysAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return HealthCheckResult.Unhealthy("Managed provider keys could not be refreshed."); }
    }
}
