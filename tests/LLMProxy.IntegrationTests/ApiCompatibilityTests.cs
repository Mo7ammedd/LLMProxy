using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMProxy.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace LLMProxy.IntegrationTests;

public sealed class ApiCompatibilityTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static object Request(bool stream = false, bool usage = false, string model = "fast", string text = "Hello") => new
    {
        model,
        messages = new[] { new { role = "user", content = text } },
        max_tokens = 64,
        stream,
        stream_options = stream ? new { include_usage = usage } : null
    };

    [Fact]
    public async Task Normal_chat_has_openai_shape_alias_correlation_and_persisted_usage()
    {
        factory.Backend.Reset();
        var (client, key) = await factory.ClientAsync();
        using (client)
        {
            client.DefaultRequestHeaders.Add("X-Correlation-Id", "client-123");
            var response = await client.PostAsJsonAsync("/v1/chat/completions", Request());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("chat.completion", body.GetProperty("object").GetString());
            Assert.Equal("fast", body.GetProperty("model").GetString());
            Assert.Equal("Hello", body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
            Assert.Equal(5, body.GetProperty("usage").GetProperty("total_tokens").GetInt32());
            var requestId = Assert.Single(response.Headers.GetValues("X-Request-Id"));
            Assert.Equal("client-123", Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
            var usage = Assert.Single(await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 100, default));
            Assert.Equal(Guid.Parse(requestId), usage.RequestId);
            Assert.Equal("openai", usage.Provider);
            Assert.Equal("success", usage.Status);
            Assert.Equal(0.000001650m, usage.EstimatedCost);
            var persisted = await factory.Services.GetRequiredService<IGatewayStore>().FindKeyByIdAsync(key.Details.Id, default);
            Assert.NotNull(persisted!.LastUsedAt);
            Assert.Equal(5, persisted.UsedTokens);
            Assert.Equal(0, persisted.ReservedTokens);
        }
    }

    [Fact]
    public async Task Models_are_filtered_to_the_keys_allowed_aliases()
    {
        var (client, _) = await factory.ClientAsync();
        using (client)
        {
            var body = await client.GetFromJsonAsync<JsonElement>("/v1/models");
            Assert.Equal("list", body.GetProperty("object").GetString());
            Assert.Equal("fast", Assert.Single(body.GetProperty("data").EnumerateArray()).GetProperty("id").GetString());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong-key")]
    [InlineData("Basic invalid")]
    public async Task Authentication_errors_have_openai_shape(string? authorization)
    {
        using var client = factory.CreateClient();
        if (authorization is not null) client.DefaultRequestHeaders.Add("Authorization", authorization);
        var response = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal("invalid_api_key", error.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("param").ValueKind);
    }

    [Fact]
    public async Task Rate_and_quota_limits_prevent_provider_calls()
    {
        factory.Backend.Reset();
        var (client, _) = await factory.ClientAsync(rpm: 1);
        using (client)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/chat/completions", Request())).StatusCode);
            var limited = await client.PostAsJsonAsync("/v1/chat/completions", Request());
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.NotNull(limited.Headers.RetryAfter);
        }
        var (tokenLimited, _) = await factory.ClientAsync(tokens: 1);
        using (tokenLimited) Assert.Equal(HttpStatusCode.TooManyRequests, (await tokenLimited.PostAsJsonAsync("/v1/chat/completions", Request())).StatusCode);
        var (budgetLimited, _) = await factory.ClientAsync(budget: 0);
        using (budgetLimited) Assert.Equal(HttpStatusCode.TooManyRequests, (await budgetLimited.PostAsJsonAsync("/v1/chat/completions", Request())).StatusCode);
        Assert.Single(factory.Backend.Hosts);
    }

    [Fact]
    public async Task Streaming_has_stable_ids_finish_reasons_optional_usage_and_done()
    {
        factory.Backend.Reset();
        var (client, key) = await factory.ClientAsync();
        using (client)
        {
            var response = await client.PostAsJsonAsync("/v1/chat/completions", Request(stream: true, usage: true));
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            var data = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
            Assert.Equal("[DONE]", data.Last());
            var chunks = data[..^1].Select(item => JsonSerializer.Deserialize<JsonElement>(item)).ToArray();
            Assert.Single(chunks.Select(x => x.GetProperty("id").GetString()).Distinct());
            Assert.All(chunks, chunk => Assert.Equal("fast", chunk.GetProperty("model").GetString()));
            var usage = Assert.Single(chunks, x => x.GetProperty("choices").GetArrayLength() == 0);
            Assert.Equal(5, usage.GetProperty("usage").GetProperty("total_tokens").GetInt32());
            Assert.Equal(JsonValueKind.Null, chunks[0].GetProperty("choices")[0].GetProperty("finish_reason").ValueKind);
            Assert.Equal("stop", chunks[^2].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
            Assert.Equal("success", Assert.Single(await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 10, default)).Status);
        }
    }

    [Fact]
    public async Task Midstream_errors_do_not_fall_back_or_send_done()
    {
        factory.Backend.Reset("midstream");
        var (client, key) = await factory.ClientAsync();
        using (client)
        {
            var response = await client.PostAsJsonAsync("/v1/chat/completions", Request(stream: true));
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains("provider_stream_error", text);
            Assert.DoesNotContain("[DONE]", text);
            Assert.DoesNotContain("confidential prompt", text);
            Assert.Single(factory.Backend.Hosts);
            Assert.Equal("error", Assert.Single(await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 10, default)).Status);
        }
    }

    [Fact]
    public async Task Provider_fallback_translates_the_second_providers_response()
    {
        factory.Backend.Reset("fallback");
        var (client, key) = await factory.ClientAsync();
        using (client)
        {
            var response = await client.PostAsJsonAsync("/v1/chat/completions", Request());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["openai.test", "anthropic.test"], factory.Backend.Hosts);
            Assert.Equal("anthropic", Assert.Single(await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 100, default)).Provider);
        }
    }

    [Fact]
    public async Task Upstream_errors_and_application_logs_do_not_leak_prompts_or_keys()
    {
        factory.Backend.Reset("failure");
        var (client, key) = await factory.ClientAsync();
        const string prompt = "private-user-prompt-3e001c0b";
        using (client)
        {
            var response = await client.PostAsJsonAsync("/v1/chat/completions", Request(text: prompt));
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("upstream-secret", text);
            Assert.DoesNotContain(prompt, text);
            var logs = string.Join('\n', factory.Logs.Messages);
            Assert.DoesNotContain(prompt, logs);
            Assert.DoesNotContain(key.Key, logs);
            Assert.DoesNotContain("fake-provider-secret", logs);
        }
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health/ready")).StatusCode);
    }

    [Theory]
    [InlineData("reasoning", 403)]
    [InlineData("unknown", 404)]
    public async Task Model_errors_are_consistent(string model, int expected)
    {
        var (client, _) = await factory.ClientAsync();
        using (client)
        {
            var response = await client.PostAsJsonAsync("/v1/chat/completions", Request(model: model));
            Assert.Equal(expected, (int)response.StatusCode);
            Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out _));
        }
    }

    [Fact]
    public async Task Invalid_json_and_unknown_endpoints_use_openai_errors()
    {
        var (client, _) = await factory.ClientAsync();
        using (client)
        {
            var response = await client.PostAsync("/v1/chat/completions", new StringContent("{broken", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_json", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/not-supported")).StatusCode);
        }
    }

    [Fact]
    public async Task Administrative_keys_are_separate_and_revocation_is_immediate()
    {
        using var admin = factory.AdminClient();
        var created = await admin.PostAsJsonAsync("/admin/keys", new { owner = "admin-created", allowed_models = new[] { "fast" }, requests_per_minute = 10 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var payload = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = payload.GetProperty("details").GetProperty("id").GetGuid();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.GetProperty("key").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models")).StatusCode);
        var updated = await admin.PutAsJsonAsync($"/admin/keys/{id}", new { enabled = false, allowed_models = new[] { "fast" }, requests_per_minute = 10 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/models")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/keys")).StatusCode);
        var list = await (await admin.GetAsync("/admin/keys")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("key_hash", list);
        Assert.DoesNotContain(payload.GetProperty("key").GetString()!, list);
    }
}

public sealed class HealthTests
{
    [Fact]
    public async Task Invalid_provider_configuration_does_not_fail_liveness()
    {
        await using var factory = new GatewayFactory(configured: false);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var checks = (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checks");
        Assert.Equal("Unhealthy", checks.GetProperty("provider_configuration").GetProperty("status").GetString());
        Assert.Equal("Healthy", checks.GetProperty("sqlite").GetProperty("status").GetString());
    }
}
