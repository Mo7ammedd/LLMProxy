using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace LLMProxy.IntegrationTests;

public sealed class ProviderOperationsTests
{
    // Fixed test fixture, never used outside isolated temporary databases.
    private static readonly string EncryptionKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray());
    private const string ManagedKey = "managed-provider-integration-fixture";
    private const string EnvironmentKey = "fake-provider-secret";

    [Theory]
    [InlineData("")]
    [InlineData("bad key")]
    [InlineData("bad\nheader")]
    public async Task Malformed_credentials_are_rejected_without_persistence(string key)
    {
        await using var factory = Factory();
        using var admin = factory.AdminClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/providers/openai/keys", new { key })).StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<IProviderOperationsStore>().ListProviderKeysAsync(default));
    }

    [Fact]
    public async Task An_empty_update_does_not_disable_a_key()
    {
        await using var factory = Factory();
        using var admin = factory.AdminClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/providers/openai/keys/" + ProviderKeyId.FromSecret(EnvironmentKey), new { })).StatusCode);
        var summary = (await admin.GetFromJsonAsync<ProviderSummary[]>("/admin/providers", LlmJson.Options))!.Single(x => x.Name == "openai");
        Assert.True(Assert.Single(summary.Keys).Enabled);
    }

    [Fact]
    public async Task Undecryptable_shared_credentials_fail_readiness_and_new_inference()
    {
        await using var factory = Factory();
        var (client, _) = await factory.ClientAsync();
        using (client)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            await factory.Services.GetRequiredService<IProviderOperationsStore>().SaveProviderKeyAsync(new StoredProviderKey
            {
                Provider = "openai",
                KeyId = ProviderKeyId.FromSecret(ManagedKey),
                Ciphertext = "tampered-fixture",
                UpdatedAt = DateTimeOffset.UtcNow
            }, [ProviderKeyId.FromSecret(EnvironmentKey)], true, "fixture", default);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/v1/chat/completions", Chat())).StatusCode);
            Assert.Empty(factory.Backend.Hosts);
        }
    }

    [Fact]
    public async Task Managed_keys_are_encrypted_audited_and_refreshed_between_instances()
    {
        await using var first = Factory();
        using var admin = first.AdminClient();
        await using var second = Factory();
        second.Overrides["LLMProxy:Storage:SqlitePath"] = first.DatabasePath;
        var (client, _) = await second.ClientAsync();
        using (client)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/admin/providers/openai/keys", new { key = ManagedKey, label = "secondary" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/admin/providers/openai/keys", new { key = ManagedKey })).StatusCode);
            await SetEnabled(admin, EnvironmentKey, false);
            second.Backend.Reset();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/chat/completions", Chat())).StatusCode);
            Assert.Equal(ManagedKey, Assert.Single(second.Backend.Credentials));
            var providers = await admin.GetStringAsync("/admin/providers");
            Assert.DoesNotContain(ManagedKey, providers);
            Assert.DoesNotContain(EnvironmentKey, providers);
            var provider = (await admin.GetFromJsonAsync<ProviderSummary[]>("/admin/providers", LlmJson.Options))!.Single(x => x.Name == "openai");
            Assert.Equal(1, provider.Keys.Single(x => x.Id == ProviderKeyId.FromSecret(ManagedKey)).Attempts);
            var row = Assert.Single(await first.Services.GetRequiredService<IProviderOperationsStore>().ListProviderKeysAsync(default), x => x.Ciphertext is not null);
            Assert.DoesNotContain(ManagedKey, row.Ciphertext!);
            Assert.DoesNotContain(ManagedKey, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(row.Ciphertext!)));
            Assert.Equal(ManagedKey, first.Services.GetRequiredService<IProviderSecretProtector>().Unprotect(row));
            var audit = await admin.GetStringAsync("/admin/audit");
            Assert.Contains("provider_key.add", audit);
            Assert.DoesNotContain(ManagedKey, audit);
            Assert.DoesNotContain(ManagedKey, string.Join('\n', first.Logs.Messages));
            await SetEnabled(admin, ManagedKey, false);
            second.Backend.Reset();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/chat/completions", Chat())).StatusCode);
            Assert.DoesNotContain("openai.test", second.Backend.Hosts); // Falls back after disabling the last key.
        }
        await using var restarted = Factory();
        restarted.Overrides["LLMProxy:Storage:SqlitePath"] = first.DatabasePath;
        using var restartedAdmin = restarted.AdminClient();
        var persisted = (await restartedAdmin.GetFromJsonAsync<ProviderSummary[]>("/admin/providers", LlmJson.Options))!.Single(x => x.Name == "openai");
        Assert.All(persisted.Keys, key => Assert.False(key.Enabled));
    }

    [Fact]
    public async Task Missing_or_wrong_encryption_key_and_ciphertext_tampering_fail_safely()
    {
        await using var factory = new GatewayFactory();
        using var admin = factory.AdminClient();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/admin/providers/openai/keys", new { key = ManagedKey })).StatusCode);
        using var correct = new ProviderSecretProtector(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["LLMPROXY_PROVIDER_KEY_ENCRYPTION_KEY"] = EncryptionKey }).Build());
        var id = ProviderKeyId.FromSecret(ManagedKey);
        var row = new StoredProviderKey { Provider = "openai", KeyId = id, Ciphertext = correct.Protect("openai", id, ManagedKey) };
        row.Provider = "other-account";
        Assert.Equal("key_decryption_failed", Assert.Throws<GatewayException>(() => correct.Unprotect(row)).Code);
        row.Provider = "openai";
        var bytes = Convert.FromBase64String(row.Ciphertext);
        bytes[^1] ^= 1;
        row.Ciphertext = Convert.ToBase64String(bytes);
        Assert.Equal("key_decryption_failed", Assert.Throws<GatewayException>(() => correct.Unprotect(row)).Code);
        using var wrong = new ProviderSecretProtector(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["LLMPROXY_PROVIDER_KEY_ENCRYPTION_KEY"] = Convert.ToBase64String(new byte[32]) }).Build());
        row.Ciphertext = correct.Protect("openai", id, ManagedKey);
        Assert.Equal("key_decryption_failed", Assert.Throws<GatewayException>(() => wrong.Unprotect(row)).Code);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("auditor")]
    public async Task Only_administrators_can_change_or_probe_provider_keys(string role)
    {
        await using var factory = Factory();
        using var admin = factory.AdminClient();
        const string password = "provider-operations-test-password";
        (await admin.PostAsJsonAsync("/admin/operators", new { username = role, password, role })).EnsureSuccessStatusCode();
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/admin/auth/login", new { username = role, password });
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/admin/providers/openai/keys", new { key = ManagedKey })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/admin/providers/openai/keys/" + ProviderKeyId.FromSecret(EnvironmentKey), new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/admin/providers/openai/check", new { })).StatusCode);
        Assert.Empty(factory.Backend.Hosts);
    }

    [Fact]
    public async Task Live_checks_are_opt_in_per_key_bounded_and_do_not_retry_or_change_pool_state()
    {
        await using var disabled = Factory();
        using var disabledAdmin = disabled.AdminClient();
        Assert.Equal(HttpStatusCode.Conflict, (await disabledAdmin.PostAsJsonAsync("/admin/providers/openai/check", new { })).StatusCode);
        Assert.Empty(disabled.Backend.Hosts);
        await using var factory = Factory();
        factory.Overrides["LLMProxy:Operations:LiveChecksEnabled"] = "true";
        factory.Overrides["LLMProxy:Providers:Resilience:RetryCount"] = "2";
        using var admin = factory.AdminClient();
        (await admin.PostAsJsonAsync("/admin/providers/openai/keys", new { key = ManagedKey })).EnsureSuccessStatusCode();
        factory.Backend.ResponseOverride = request => request.Headers.Authorization?.Parameter == EnvironmentKey
            ? new(HttpStatusCode.TooManyRequests) { Content = new StringContent("secret " + EnvironmentKey) } : null;
        var check = await admin.PostAsJsonAsync("/admin/providers/openai/check", new { });
        check.EnsureSuccessStatusCode();
        var body = await check.Content.ReadAsStringAsync();
        Assert.DoesNotContain(EnvironmentKey, body);
        Assert.DoesNotContain(ManagedKey, body);
        var results = JsonSerializer.Deserialize<ProviderCheckResult[]>(body, LlmJson.Options)!;
        Assert.Equal(2, results.Length);
        Assert.Contains(results, x => x.Code == "provider_rate_limited");
        Assert.Contains(results, x => x.Success && x.Models.Contains("fixture-model"));
        Assert.Equal(2, factory.Backend.Hosts.Count);
        var states = await factory.Services.GetRequiredService<IProviderPoolState>().ReadAsync("openai",
            [ProviderKeyId.FromSecret(EnvironmentKey), ProviderKeyId.FromSecret(ManagedKey)], false, default);
        Assert.All(states, x => Assert.Equal(0, x.RetryAfterSeconds));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/providers/openai/check", new { model = "unconfigured" })).StatusCode);
    }

    [RedisFact]
    public async Task Redis_rotation_and_cooldowns_are_shared_but_account_names_are_isolated()
    {
        using var first = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("LLMPROXY_TEST_REDIS")!);
        using var second = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("LLMPROXY_TEST_REDIS")!);
        var options = new StorageOptions { RedisKeyPrefix = "llmproxy-pool-tests-" + Guid.NewGuid().ToString("N") };
        IProviderPoolState[] states = [new RedisProviderPoolState(first, options), new RedisProviderPoolState(second, options)];
        var keys = new[] { ProviderKeyId.FromSecret("fixture-a"), ProviderKeyId.FromSecret("fixture-b") };
        Assert.Equal(keys[0], (await states[0].ReadAsync("account-a", keys, true, default))[0].KeyId);
        Assert.Equal(keys[1], (await states[1].ReadAsync("account-a", keys, true, default))[0].KeyId);
        await states[0].CoolDownAsync("account-a", keys[0], 60, default);
        await states[1].CoolDownAsync("account-a", keys[0], 1, default);
        Assert.InRange((await states[1].ReadAsync("account-a", [keys[0]], false, default))[0].RetryAfterSeconds, 55, 60);
        Assert.Equal(keys[1], (await states[1].ReadAsync("account-a", keys, true, default))[0].KeyId);
        Assert.Equal(0, (await states[1].ReadAsync("account-b", [keys[0]], false, default))[0].RetryAfterSeconds);
        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => states[i % 2].ReadAsync("account-b", keys, true, default)));
        Assert.Equal(50, simultaneous.Count(x => x[0].KeyId == keys[0]));
        var redisKey = (RedisKey)$"{{{options.RedisKeyPrefix}}}:provider-pool:account-a";
        await first.GetDatabase().KeyDeleteAsync(redisKey);
        await first.GetDatabase().KeyDeleteAsync($"{{{options.RedisKeyPrefix}}}:provider-pool:account-b");
    }

    [Fact]
    public async Task Provider_key_mutations_enforce_the_limit_under_concurrency() => await CheckLimit(false);
    [PostgresFact]
    public async Task Postgres_provider_key_mutations_enforce_the_limit_under_concurrency() => await CheckLimit(true);
    private static async Task CheckLimit(bool postgres)
    {
        await using var fixture = await StoreFixture.CreateAsync(postgres);
        var store = (IProviderOperationsStore)fixture.Store;
        var configured = Enumerable.Range(0, 63).Select(x => ProviderKeyId.FromSecret("configured-" + x)).ToArray();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
        {
            try
            {
                await store.SaveProviderKeyAsync(new StoredProviderKey
                {
                    Provider = "openai",
                    KeyId = ProviderKeyId.FromSecret("new-" + i),
                    Ciphertext = "encrypted-fixture",
                    UpdatedAt = DateTimeOffset.UtcNow
                }, configured, true, "test", default);
                return true;
            }
            catch (GatewayException ex) when (ex.Code == "provider_key_limit") { return false; }
        }));
        Assert.Single(outcomes, x => x);
        Assert.Equal(1, await store.RevisionAsync(default));
    }

    private static GatewayFactory Factory()
    {
        var factory = new GatewayFactory();
        factory.Overrides["LLMPROXY_PROVIDER_KEY_ENCRYPTION_KEY"] = EncryptionKey;
        return factory;
    }
    private static async Task SetEnabled(HttpClient admin, string key, bool enabled) =>
        (await admin.PutAsJsonAsync("/admin/providers/openai/keys/" + ProviderKeyId.FromSecret(key), new { enabled })).EnsureSuccessStatusCode();
    private static object Chat() => new { model = "fast", messages = new[] { new { role = "user", content = "Hello" } } };
}
