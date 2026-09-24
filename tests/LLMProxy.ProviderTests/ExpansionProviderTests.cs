using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LLMProxy.Domain;
using LLMProxy.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LLMProxy.ProviderTests;

public sealed class ExpansionProviderTests
{
    [Fact]
    public async Task Named_accounts_keep_credentials_and_endpoints_separate()
    {
        var settings = new Dictionary<string, string?>();
        foreach (var name in new[] { "east", "west" })
        {
            settings[$"LLMProxy:Providers:Accounts:{name}:Adapter"] = "openai";
            settings[$"LLMProxy:Providers:Accounts:{name}:BaseUrl"] = $"https://{name}.test/v1";
            settings[$"LLMProxy:Providers:Accounts:{name}:ApiKey"] = name + "-fixture-secret";
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLlmProxyProviders(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var observed = new ConcurrentDictionary<string, string?>();
        foreach (var name in new[] { "east", "west" })
            services.AddHttpClient("llmproxy." + name).ConfigurePrimaryHttpMessageHandler(() => new CallbackHandler((request, _) =>
            {
                observed[request.RequestUri!.Host] = request.Headers.Authorization?.Parameter;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(Responses.OpenAi, Encoding.UTF8, "application/json") });
            }));
        using var container = services.BuildServiceProvider();
        var providers = container.GetServices<ILlmProvider>().Where(x => x.IsConfigured).ToArray();
        Assert.Equal(2, providers.Length);
        await Task.WhenAll(providers.Select(x => x.ChatCompletionAsync(ProviderHarness.Request(), default)));
        Assert.Equal("east-fixture-secret", observed["east.test"]);
        Assert.Equal("west-fixture-secret", observed["west.test"]);
        Assert.All(providers, provider => Assert.True(provider.Capabilities.Features.HasFlag(ModelCapability.Responses)));
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("openai")]
    public async Task Images_are_translated_without_losing_text_or_mutating_the_request(string adapter)
    {
        using var harness = new ProviderHarness(Responses.Body(adapter));
        var content = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "text", text = "Describe this" },
            new { type = "image_url", image_url = new { url = "data:image/png;base64,AQID" } }
        });
        var request = ProviderHarness.Request() with { Messages = [new() { Role = "user", Content = content }] };
        await harness.Provider(adapter).ChatCompletionAsync(request, default);
        using var body = JsonDocument.Parse(harness.Body!);
        var root = body.RootElement;
        if (adapter == "anthropic")
        {
            var parts = root.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal("Describe this", parts[0].GetProperty("text").GetString());
            Assert.Equal("base64", parts[1].GetProperty("source").GetProperty("type").GetString());
            Assert.Equal("image/png", parts[1].GetProperty("source").GetProperty("media_type").GetString());
            Assert.Equal("AQID", parts[1].GetProperty("source").GetProperty("data").GetString());
        }
        else if (adapter == "gemini")
        {
            var parts = root.GetProperty("contents")[0].GetProperty("parts");
            Assert.Equal("Describe this", parts[0].GetProperty("text").GetString());
            Assert.Equal("image/png", parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString());
            Assert.Equal("AQID", parts[1].GetProperty("inlineData").GetProperty("data").GetString());
        }
        else Assert.Equal(content.GetRawText(), root.GetProperty("messages")[0].GetProperty("content").GetRawText());
        Assert.Equal("image_url", request.Messages[0].Content!.Value[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Gemini_receives_audio_and_thinking_budget_in_native_fields()
    {
        using var harness = new ProviderHarness(Responses.Gemini);
        var request = ProviderHarness.Request() with
        {
            MaxTokens = 4096,
            ThinkingBudgetTokens = 1024,
            Messages = [new() { Role = "user", Content = JsonSerializer.SerializeToElement(new[]
            { new { type = "input_audio", input_audio = new { data = "AQID", format = "wav" } } }) }]
        };
        await harness.Provider("gemini").ChatCompletionAsync(request, default);
        using var body = JsonDocument.Parse(harness.Body!);
        Assert.Equal("audio/wav", body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal(1024, body.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
    }

    [Fact]
    public async Task Anthropic_cache_tokens_are_included_in_total_input_and_priced_separately()
    {
        var response = Responses.Anthropic.Replace("\"input_tokens\":3", "\"input_tokens\":3,\"cache_read_input_tokens\":10,\"cache_creation_input_tokens\":5", StringComparison.Ordinal);
        using var harness = new ProviderHarness(response);
        var result = await harness.Provider("anthropic").ChatCompletionAsync(ProviderHarness.Request(), default);
        Assert.Equal(18, result.Usage.InputTokens);
        Assert.Equal(20, result.Usage.TotalTokens);
        Assert.Equal(new PromptTokenDetails(10, 5), result.Usage.PromptTokensDetails);
    }
}
