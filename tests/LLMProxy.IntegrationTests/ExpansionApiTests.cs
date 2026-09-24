using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMProxy.Application;
using LLMProxy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LLMProxy.IntegrationTests;

public sealed class ExpansionApiTests
{
    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/V1/CHAT/COMPLETIONS/", true)]
    [InlineData("/v1/embeddings", false)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/responses", true)]
    public async Task Request_deadlines_record_timeouts_and_release_all_concurrency_scopes(string path, bool stream)
    {
        await using var factory = new GatewayFactory();
        factory.Overrides["LLMProxy:Requests:TimeoutSeconds"] = "1";
        factory.Overrides["LLMProxy:Concurrency:GlobalLimit"] = "1";
        factory.Overrides["LLMProxy:Concurrency:PerKeyLimit"] = "1";
        factory.Overrides["LLMProxy:Concurrency:PerProviderLimit"] = "1";
        factory.Overrides["LLMProxy:Models:fast:MaxConcurrentRequests"] = "1";
        factory.Overrides["LLMProxy:Models:embeddings:MaxConcurrentRequests"] = "1";
        var (client, key) = await factory.ClientAsync(models: ["fast", "embeddings"]);
        using (client)
        {
            object request = path.Contains("chat", StringComparison.OrdinalIgnoreCase)
                ? new { model = "fast", messages = new[] { new { role = "user", content = "hello" } }, stream }
                : path.EndsWith("embeddings", StringComparison.Ordinal)
                    ? new { model = "embeddings", input = "hello" }
                    : new { model = "fast", input = "hello", stream };
            factory.Backend.Reset("deadline");
            var response = await client.PostAsJsonAsync(path, request);
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.Contains("request_timeout", await response.Content.ReadAsStringAsync());
            var store = factory.Services.GetRequiredService<IGatewayStore>();
            var usage = Assert.Single(await store.ListUsageAsync(key.Details.Id, 10, default));
            Assert.Equal("error", usage.Status);
            Assert.Equal("request_timeout", usage.ErrorCode);
            Assert.True(usage.UsageEstimated);
            var saved = await store.FindKeyByIdAsync(key.Details.Id, default);
            Assert.Equal(0, saved!.ReservedTokens);
            Assert.Equal(0, saved.ReservedUnits);
            var management = factory.Services.GetRequiredService<IManagementStore>();
            Assert.Equal("request_timeout", Assert.Single(await management.ListAttemptsAsync(usage.RequestId, default)).Status);
            Assert.Contains((await management.ListAuditAsync(null, 100, default)).Data,
                row => row.RequestId == usage.RequestId && row.StatusCode == 504);
            factory.Backend.Reset();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(path, request)).StatusCode);
        }
    }

    [Theory]
    [InlineData("auditor", false)]
    [InlineData("operator", true)]
    public async Task Operator_roles_are_enforced_and_logout_revokes_sessions(string role, bool canWrite)
    {
        await using var factory = new GatewayFactory();
        using var admin = factory.AdminClient();
        const string password = "test-only-password-123456";
        var created = await admin.PostAsJsonAsync("/admin/operators", new { username = role, password, role });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/admin/auth/login", new { username = role, password });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/usage/page")).StatusCode);
        var key = await client.PostAsJsonAsync("/admin/keys", new { owner = "role-test", allowed_models = new[] { "fast" } });
        Assert.Equal(canWrite ? HttpStatusCode.Created : HttpStatusCode.Forbidden, key.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/operators")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/admin/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/usage/page")).StatusCode);
        var audit = await admin.GetStringAsync("/admin/audit");
        Assert.DoesNotContain(password, audit);
        Assert.DoesNotContain(token, audit);
        Assert.Contains("/admin/auth/logout", audit);
    }

    [Fact]
    public async Task Operator_sessions_expire_and_the_final_administrator_is_preserved()
    {
        var clock = new MutableClock();
        await using var factory = new GatewayFactory { Clock = clock };
        using var admin = factory.AdminClient();
        const string password = "administrator-test-password";
        var created = await admin.PostAsJsonAsync("/admin/operators", new { username = "admin", password, role = "administrator" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/admin/operators/" + id,
            new { enabled = false, role = "administrator" })).StatusCode);
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/admin/auth/login", new { username = "admin", password });
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/auth/me")).StatusCode);
        clock.Advance(TimeSpan.FromHours(9));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Gateway_keys_can_expire_and_rotate_through_the_management_api()
    {
        var clock = new MutableClock();
        await using var factory = new GatewayFactory { Clock = clock };
        using var admin = factory.AdminClient();
        var created = await admin.PostAsJsonAsync("/admin/keys", new
        {
            owner = "expiring",
            allowed_models = new[] { "fast" },
            expires_at = clock.GetUtcNow().AddMinutes(10),
            monthly_token_limit = 1000
        });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("details").GetProperty("id").GetGuid();
        using var old = factory.CreateClient();
        old.DefaultRequestHeaders.Authorization = new("Bearer", body.GetProperty("key").GetString());
        var rotated = await admin.PostAsJsonAsync($"/admin/keys/{id}/rotate", new { grace_seconds = 0 });
        var current = await rotated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, current.GetProperty("details").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/v1/models")).StatusCode);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", current.GetProperty("key").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models")).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/models")).StatusCode);
    }

    [Fact]
    public async Task Embeddings_and_responses_use_normal_quota_and_usage_accounting()
    {
        await using var factory = new GatewayFactory();
        var (client, key) = await factory.ClientAsync(models: ["fast", "embeddings"]);
        using (client)
        {
            var embedding = await client.PostAsJsonAsync("/v1/embeddings", new { model = "embeddings", input = "hello", dimensions = 2 });
            Assert.Equal(HttpStatusCode.OK, embedding.StatusCode);
            var vector = await embedding.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("embeddings", vector.GetProperty("model").GetString());
            Assert.Equal(2, vector.GetProperty("data")[0].GetProperty("embedding").GetArrayLength());
            var response = await client.PostAsJsonAsync("/v1/responses", new { model = "fast", input = "hello", max_output_tokens = 100 });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var completed = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("response", completed.GetProperty("object").GetString());
            Assert.Equal("fast", completed.GetProperty("model").GetString());
            var usage = await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 10, default);
            Assert.Equal(2, usage.Count);
            Assert.Contains(usage, row => row.Operation == "embeddings" && row.OutputTokens == 0 && row.InputTokens == 3);
            Assert.Contains(usage, row => row.Operation == "responses" && row.TotalTokens == 5);
            Assert.All(usage, row => Assert.Equal("success", row.Status));
        }
    }

    [Fact]
    public async Task Response_streaming_preserves_events_and_never_replays_after_output()
    {
        await using var factory = new GatewayFactory();
        var (client, _) = await factory.ClientAsync();
        using (client)
        {
            var request = new { model = "fast", input = "hello", stream = true };
            var response = await client.PostAsJsonAsync("/v1/responses", request);
            var stream = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("event: response.completed", stream);
            Assert.Contains("\"model\":\"fast\"", stream);
            Assert.DoesNotContain("[DONE]", stream);
            factory.Backend.Reset("midstream");
            var interrupted = await client.PostAsJsonAsync("/v1/responses", request);
            var failed = await interrupted.Content.ReadAsStringAsync();
            Assert.Contains("provider_stream_error", failed);
            Assert.Contains("event: error\n", failed);
            var errorFrame = failed.Split('\n').Last(line => line.StartsWith("data: ", StringComparison.Ordinal));
            using var error = JsonDocument.Parse(errorFrame[6..]);
            Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
            Assert.Equal(2, error.RootElement.GetProperty("sequence_number").GetInt32());
            Assert.DoesNotContain("confidential", failed);
            Assert.Single(factory.Backend.Hosts);
        }
    }

    [Fact]
    public async Task Fallback_attempts_are_visible_and_invoice_adjustments_are_idempotent()
    {
        await using var factory = new GatewayFactory();
        var (client, key) = await factory.ClientAsync();
        using (client)
        {
            factory.Backend.Reset("fallback");
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/chat/completions", Chat())).StatusCode);
            var usage = Assert.Single(await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 10, default));
            using var admin = factory.AdminClient();
            var attempts = await admin.GetFromJsonAsync<JsonElement>($"/admin/usage/{usage.RequestId}/attempts");
            Assert.Equal(2, attempts.GetArrayLength());
            var successful = attempts.EnumerateArray().Single(x => x.GetProperty("status").GetString() == "success");
            var records = new[] { new { attempt_id = successful.GetProperty("id").GetGuid(), actual_cost = .02m, reference = "invoice/api-test" } };
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/admin/billing/reconcile", records)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/admin/billing/reconcile", records)).StatusCode);
            var saved = await factory.Services.GetRequiredService<IGatewayStore>().FindKeyByIdAsync(key.Details.Id, default);
            Assert.Equal(.02m, Money.FromUnits(saved!.SpentUnits));
            var export = await admin.GetStringAsync("/admin/usage/export?owner=" + Uri.EscapeDataString(key.Details.Owner));
            Assert.Contains("cost_usd", export);
            Assert.Contains(usage.RequestId.ToString(), export);
        }
    }

    [Fact]
    public async Task Configuration_reload_changes_new_requests_but_preserves_in_flight_prices()
    {
        await using var factory = new GatewayFactory("openai");
        var (client, key) = await factory.ClientAsync();
        using (client)
        using (var admin = factory.AdminClient())
        {
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Backend.BeforeResponse = async ct => { arrived.TrySetResult(); await release.Task.WaitAsync(ct); };
            var catalog = factory.Services.GetRequiredService<RuntimeCatalog>();
            var before = catalog.Current;
            var pending = client.PostAsJsonAsync("/v1/chat/completions", Chat());
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var configuration = factory.Services.GetRequiredService<IConfiguration>();
                configuration["LLMProxy:Pricing:openai/gpt-4o-mini:InputPerMillion"] = "10";
                configuration["LLMProxy:Pricing:openai/gpt-4o-mini:OutputPerMillion"] = "20";
                Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/admin/config/reload", null)).StatusCode);
                Assert.NotSame(before, catalog.Current);
            }
            finally { release.TrySetResult(); }
            Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
            factory.Backend.BeforeResponse = null;
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/chat/completions", Chat())).StatusCode);
            var usage = await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 10, default);
            Assert.Equal(2, usage.Count);
            Assert.Contains(usage, row => row.EstimatedCost == before.Pricing.GetPrice("openai/gpt-4o-mini", 3).Calculate(TokenUsage.From(3, 2)));
            Assert.Contains(usage, row => row.EstimatedCost == .00007m);
        }
    }

    [Fact]
    public async Task Invalid_reload_keeps_the_previous_catalog()
    {
        await using var factory = new GatewayFactory();
        using var admin = factory.AdminClient();
        var catalog = factory.Services.GetRequiredService<RuntimeCatalog>();
        var before = catalog.Current;
        factory.Services.GetRequiredService<IConfiguration>()["LLMProxy:Models:fast:Routing"] = "unregistered";
        var response = await admin.PostAsync("/admin/config/reload", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Same(before, catalog.Current);
    }

    [Fact]
    public async Task Dashboard_is_served_with_a_restrictive_content_security_policy()
    {
        await using var factory = new GatewayFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("Gateway console", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/console.js")).StatusCode);
    }

    [Fact]
    public async Task Batches_run_through_the_gateway_and_files_are_isolated_by_key()
    {
        await using var factory = new GatewayFactory();
        factory.Overrides["LLMProxy:Batches:Workers"] = "1";
        var (client, key) = await factory.ClientAsync(rpm: 1000);
        using (client)
        {
            var fileId = await Upload(client);
            var created = await client.PostAsJsonAsync("/v1/batches", new { input_file_id = fileId, endpoint = "/v1/chat/completions", completion_window = "24h" });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var job = await created.Content.ReadFromJsonAsync<JsonElement>();
            var id = job.GetProperty("id").GetString();
            var timeout = DateTime.UtcNow.AddSeconds(15);
            while (job.GetProperty("status").GetString() == "in_progress" && DateTime.UtcNow < timeout)
            {
                await Task.Delay(100);
                job = await client.GetFromJsonAsync<JsonElement>("/v1/batches/" + id);
            }
            Assert.Equal("completed", job.GetProperty("status").GetString());
            Assert.Equal(2, job.GetProperty("request_counts").GetProperty("completed").GetInt32());
            var output = await client.GetStringAsync("/v1/files/" + job.GetProperty("output_file_id").GetString() + "/content");
            Assert.Equal(2, output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal(2, (await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 10, default)).Count);
            var (other, _) = await factory.ClientAsync();
            using (other)
            {
                Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/v1/files/" + fileId)).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/v1/batches/" + id)).StatusCode);
            }
        }
    }

    [Fact]
    public async Task Queued_batches_can_be_cancelled_without_provider_calls()
    {
        await using var factory = new GatewayFactory();
        var (client, _) = await factory.ClientAsync();
        using (client)
        {
            var fileId = await Upload(client);
            var created = await client.PostAsJsonAsync("/v1/batches", new { input_file_id = fileId, endpoint = "/v1/chat/completions", completion_window = "24h" });
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/v1/batches/" + id + "/cancel", null)).StatusCode);
            await factory.Services.GetRequiredService<IBatchStore>().MaintainBatchesAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-7), default);
            var job = await client.GetFromJsonAsync<JsonElement>("/v1/batches/" + id);
            Assert.Equal("cancelled", job.GetProperty("status").GetString());
            Assert.Empty(factory.Backend.Hosts);
        }
    }

    private static object Chat() => new { model = "fast", messages = new[] { new { role = "user", content = "Hello" } } };
    private static async Task<string> Upload(HttpClient client)
    {
        var lines = string.Join('\n', Enumerable.Range(0, 2).Select(i => JsonSerializer.Serialize(new
        { custom_id = "item-" + i, method = "POST", url = "/v1/chat/completions", body = Chat() })));
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("batch"), "purpose");
        multipart.Add(new StringContent(lines, Encoding.UTF8, "application/jsonl"), "file", "requests.jsonl");
        var upload = await client.PostAsync("/v1/files", multipart);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        return (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
}
