using Azure.Core;
using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Providers;

namespace LLMProxy.Server;

public sealed class RuntimeConfigurationService(IConfiguration configuration, RuntimeCatalog catalog,
    IHttpClientFactory clients, IEnumerable<IRoutingStrategy> strategies, IServiceProvider services) : IRuntimeConfiguration
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation = 1;
    public async Task<ConfigurationReloadResult> ReloadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (configuration is IConfigurationRoot root) root.Reload();
            var gateway = configuration.GetSection("LLMProxy").Get<GatewayOptions>() ?? new();
            var options = ProviderAccounts.Read(configuration);
            var registry = new ModelRegistry(gateway);
            var pricing = new ConfiguredPricing(gateway);
            var transport = new ProviderHttpTransport(clients, services.GetService<TimeProvider>());
            var credential = services.GetService<TokenCredential>();
            var providers = ProviderAccounts.BuiltIns.Select(name => ProviderAccounts.Create(name, options, transport, credential))
                .Concat(options.Accounts.Select(pair => ProviderAccounts.CreateAccount(pair.Key, pair.Value, clients, credential,
                    services.GetService<TimeProvider>()))).ToArray();
            _ = new ModelRouter(registry, providers, strategies);
            foreach (var target in registry.Models.SelectMany(model => model.Targets))
                pricing.GetPrice($"{target.Provider}/{target.Model}", 0);
            cancellationToken.ThrowIfCancellationRequested();
            catalog.Replace(new CatalogSnapshot(registry, pricing, providers));
            return new(++_generation, registry.Models.Count, providers.Length);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or IOException)
        { throw new GatewayException("Configuration validation failed. The previous catalog remains active.", "invalid_configuration"); }
        finally { _gate.Release(); }
    }
}
