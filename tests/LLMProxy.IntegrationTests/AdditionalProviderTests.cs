using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMProxy.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace LLMProxy.IntegrationTests;

public sealed class AdditionalProviderTests
{
    [Theory]
    [InlineData("foundry", "gpt-4o-mini")]
    [InlineData("mistral", "mistral-small-latest")]
    [InlineData("cohere", "command-r7b-12-2024")]
    [InlineData("deepseek", "deepseek-flash")]
    [InlineData("groq", "llama-3.1-8b-instant")]
    [InlineData("ollama", "llama3.1:8b")]
    public async Task A_new_provider_serves_the_public_alias_with_both_response_modes_and_persisted_usage(string provider, string upstream)
    {
        await using var factory = new GatewayFactory(provider);
        var (client, key) = await factory.ClientAsync();
        using (client)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            var models = await client.GetFromJsonAsync<JsonElement>("/v1/models");
            Assert.Equal("fast", Assert.Single(models.GetProperty("data").EnumerateArray()).GetProperty("id").GetString());
            var normal = await client.PostAsJsonAsync("/v1/chat/completions", Request(false));
            Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
            var json = await normal.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("fast", json.GetProperty("model").GetString());
            Assert.Equal("Hello", json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
            var streamed = await client.PostAsJsonAsync("/v1/chat/completions", Request(true));
            Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
            Assert.Equal("text/event-stream", streamed.Content.Headers.ContentType!.MediaType);
            var events = (await streamed.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
            Assert.Equal("[DONE]", events.Last());
            var chunks = events[..^1].Select(item => JsonSerializer.Deserialize<JsonElement>(item)).ToArray();
            Assert.All(chunks, chunk => Assert.Equal("fast", chunk.GetProperty("model").GetString()));
            var usageChunk = Assert.Single(chunks, chunk => chunk.GetProperty("choices").GetArrayLength() == 0);
            Assert.Equal(5, usageChunk.GetProperty("usage").GetProperty("total_tokens").GetInt32());
            Assert.Equal(new[] { provider + ".test", provider + ".test" }, factory.Backend.Hosts);
            Assert.All(factory.Backend.Bodies, body => Assert.Equal(upstream, JsonSerializer.Deserialize<JsonElement>(body).GetProperty("model").GetString()));
            var rows = await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 100, default);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Equal("success", row.Status);
                Assert.Equal(provider, row.Provider);
                Assert.Equal("fast", row.Model);
                Assert.Equal(5, row.TotalTokens);
                Assert.Equal(provider != "ollama", row.EstimatedCost > 0);
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transient_failure_falls_back_to_cohere_without_changing_the_client_contract(bool stream)
    {
        await using var factory = new GatewayFactory("openai", "cohere");
        factory.Backend.Reset("fallback");
        var (client, key) = await factory.ClientAsync();
        using (client)
        {
            var response = await client.PostAsJsonAsync("/v1/chat/completions", Request(stream));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Hello", body);
            Assert.Contains("fast", body);
            Assert.DoesNotContain("upstream-secret", body);
            if (stream) Assert.Contains("[DONE]", body);
            Assert.Equal(["openai.test", "cohere.test"], factory.Backend.Hosts);
            var row = Assert.Single(await factory.Services.GetRequiredService<IGatewayStore>().ListUsageAsync(key.Details.Id, 10, default));
            Assert.Equal("cohere", row.Provider);
            Assert.Equal("success", row.Status);
        }
    }

    private static object Request(bool stream) => new
    {
        model = "fast",
        messages = new[] { new { role = "user", content = "Hello" } },
        max_completion_tokens = 64,
        stream,
        stream_options = stream ? new { include_usage = true } : null
    };
}
