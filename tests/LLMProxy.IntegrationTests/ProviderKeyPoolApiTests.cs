using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Application;
using LLMProxy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LLMProxy.IntegrationTests;

public sealed class ProviderKeyPoolApiTests
{
    private const string FirstKey = "pool-first-integration-fixture";
    private const string SecondKey = "pool-second-integration-fixture";
    private const string KeySetting = "LLMProxy:Providers:OpenAI:ApiKeys:";

    [Theory]
    [InlineData("chat/completions", false)]
    [InlineData("chat/completions", true)]
    [InlineData("embeddings", false)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    public async Task Pool_failover_records_both_keys_and_charges_one_request(string endpoint, bool streaming)
    {
        await using var factory = PoolFactory();
        factory.Backend.ResponseOverride = request => request.Headers.Authorization?.Parameter == FirstKey
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("confidential " + FirstKey) } : null;
        var (client, key) = await factory.ClientAsync(models: ["fast", "embeddings"]);
        using (client)
        using (var admin = factory.AdminClient())
        {
            var response = await client.PostAsJsonAsync("/v1/" + endpoint, Payload(endpoint, streaming));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(FirstKey, body);
            Assert.DoesNotContain(SecondKey, body);
            Assert.Equal(new[] { FirstKey, SecondKey }, factory.Backend.Credentials);
            Assert.Equal(new[] { "openai.test", "openai.test" }, factory.Backend.Hosts);
            var store = factory.Services.GetRequiredService<IGatewayStore>();
            var usage = Assert.Single(await store.ListUsageAsync(key.Details.Id, 10, default));
            Assert.Equal("openai", usage.Provider);
            Assert.Equal(endpoint == "chat/completions" ? "chat" : endpoint, usage.Operation);
            Assert.Equal(endpoint == "embeddings" ? 3 : 5, usage.TotalTokens);
            var saved = (await store.FindKeyByIdAsync(key.Details.Id, default))!;
            Assert.Equal(usage.TotalTokens, saved.UsedTokens);
            Assert.Equal(Money.ToUnits(usage.EstimatedCost), saved.SpentUnits);
            Assert.Equal(0, saved.ReservedTokens);
            Assert.Equal(0, saved.ReservedUnits);
            var attemptsJson = await admin.GetStringAsync($"/admin/usage/{usage.RequestId}/attempts");
            Assert.DoesNotContain(FirstKey, attemptsJson);
            Assert.DoesNotContain(SecondKey, attemptsJson);
            using var attempts = JsonDocument.Parse(attemptsJson);
            var rows = attempts.RootElement.EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal(Fingerprint(FirstKey), rows.Single(row => row.GetProperty("http_status").GetInt32() == 401).GetProperty("provider_key_id").GetString());
            Assert.Equal(Fingerprint(SecondKey), rows.Single(row => row.GetProperty("http_status").GetInt32() == 200).GetProperty("provider_key_id").GetString());
            Assert.All(factory.Logs.Messages, message => { Assert.DoesNotContain(FirstKey, message); Assert.DoesNotContain(SecondKey, message); });
            var models = await admin.GetStringAsync("/admin/models");
            Assert.DoesNotContain(FirstKey, models);
            Assert.DoesNotContain(SecondKey, models);
        }
    }

    [Fact]
    public async Task Exhausted_pool_can_fall_back_to_the_next_provider()
    {
        await using var factory = PoolFactory();
        factory.Backend.ResponseOverride = request => request.RequestUri!.Host == "openai.test"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : null;
        var (client, key) = await factory.ClientAsync();
        using (client)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/chat/completions", Payload())).StatusCode);
            Assert.Equal(new[] { "openai.test", "openai.test", "anthropic.test" }, factory.Backend.Hosts);
            var usage = Assert.Single(await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 10, default));
            Assert.Equal("anthropic", usage.Provider);
            Assert.Equal(3, (await factory.Services.GetRequiredService<IManagementStore>().ListAttemptsAsync(usage.RequestId, default)).Count);
        }
    }

    [Theory]
    [InlineData("chat/completions")]
    [InlineData("responses")]
    public async Task A_stream_error_after_output_never_replays_on_another_key(string endpoint)
    {
        await using var factory = PoolFactory();
        factory.Backend.Mode = "midstream";
        var (client, key) = await factory.ClientAsync();
        using (client)
        {
            var response = await client.PostAsJsonAsync("/v1/" + endpoint, Payload(endpoint, true));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Hello", body);
            Assert.Contains("provider_stream_error", body);
            Assert.Equal(FirstKey, Assert.Single(factory.Backend.Credentials));
            var usage = Assert.Single(await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 10, default));
            Assert.True(usage.UsageEstimated);
            var attempt = Assert.Single(await factory.Services.GetRequiredService<IManagementStore>().ListAttemptsAsync(usage.RequestId, default));
            Assert.Equal(Fingerprint(FirstKey), attempt.ProviderKeyId);
        }
    }

    [Fact]
    public async Task Reload_preserves_shared_rotation_without_changing_inflight_credentials()
    {
        await using var factory = PoolFactory();
        var (client, key) = await factory.ClientAsync();
        using (client)
        using (var admin = factory.AdminClient())
        {
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Backend.BeforeResponse = async ct => { arrived.TrySetResult(); await release.Task.WaitAsync(ct); };
            var pending = client.PostAsJsonAsync("/v1/chat/completions", Payload());
            const string replacement = "replacement-pool-integration-fixture";
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var configuration = factory.Services.GetRequiredService<IConfiguration>();
                configuration[KeySetting + "0"] = replacement;
                configuration[KeySetting + "1"] = "other-replacement-integration-fixture";
                Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/admin/config/reload", null)).StatusCode);
            }
            finally { release.TrySetResult(); }
            Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
            factory.Backend.BeforeResponse = null;
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/chat/completions", Payload())).StatusCode);
            // Shared pool rotation survives a catalog reload.
            Assert.Equal(new[] { FirstKey, "other-replacement-integration-fixture" }, factory.Backend.Credentials);
            var usage = await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 10, default);
            var management = factory.Services.GetRequiredService<IManagementStore>();
            var attempts = await Task.WhenAll(usage.Select(row => management.ListAttemptsAsync(row.RequestId, default)));
            Assert.Equal(new[] { Fingerprint(FirstKey), Fingerprint("other-replacement-integration-fixture") }.Order(), attempts.SelectMany(x => x).Select(x => x.ProviderKeyId).Order());
        }
    }

    [Fact]
    public async Task Invalid_key_reload_retains_the_previous_pool()
    {
        await using var factory = PoolFactory();
        var (client, _) = await factory.ClientAsync();
        using (client)
        using (var admin = factory.AdminClient())
        {
            var catalog = factory.Services.GetRequiredService<RuntimeCatalog>();
            var before = catalog.Current;
            factory.Services.GetRequiredService<IConfiguration>()[KeySetting + "1"] = FirstKey;
            var response = await admin.PostAsync("/admin/config/reload", null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(FirstKey, await response.Content.ReadAsStringAsync());
            Assert.Same(before, catalog.Current);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/chat/completions", Payload())).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/chat/completions", Payload())).StatusCode);
            Assert.Equal(new[] { FirstKey, SecondKey }, factory.Backend.Credentials);
        }
    }

    [Fact]
    public async Task Provider_concurrency_limit_is_shared_by_all_keys()
    {
        await using var factory = PoolFactory(fallback: false);
        factory.Overrides["LLMProxy:Concurrency:PerProviderLimit"] = "1";
        var (client, _) = await factory.ClientAsync();
        using (client)
        {
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Backend.BeforeResponse = async ct => { arrived.TrySetResult(); await release.Task.WaitAsync(ct); };
            var pending = client.PostAsJsonAsync("/v1/chat/completions", Payload());
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var limited = await client.PostAsJsonAsync("/v1/chat/completions", Payload());
                Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
                Assert.Contains("provider_concurrency_limited", await limited.Content.ReadAsStringAsync());
                Assert.Single(factory.Backend.Credentials);
            }
            finally { release.TrySetResult(); }
            Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
            factory.Backend.BeforeResponse = null;
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/chat/completions", Payload())).StatusCode);
            Assert.Equal(new[] { FirstKey, SecondKey }, factory.Backend.Credentials);
        }
    }

    private static GatewayFactory PoolFactory(bool fallback = true)
    {
        var factory = fallback ? new GatewayFactory() : new GatewayFactory("openai");
        factory.Overrides[KeySetting + "0"] = FirstKey;
        factory.Overrides[KeySetting + "1"] = SecondKey;
        return factory;
    }

    private static JsonObject Payload(string endpoint = "chat/completions", bool streaming = false)
    {
        var payload = new JsonObject { ["model"] = endpoint == "embeddings" ? "embeddings" : "fast" };
        if (endpoint == "chat/completions")
            payload["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hello" });
        else payload["input"] = "Hello";
        if (endpoint != "embeddings") payload["stream"] = streaming;
        return payload;
    }

    private static string Fingerprint(string value)
        => "key_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
