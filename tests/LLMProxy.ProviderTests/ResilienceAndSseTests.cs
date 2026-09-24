using System.Net;
using System.Text;
using LLMProxy.Domain;
using LLMProxy.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace LLMProxy.ProviderTests;

public sealed class ResilienceAndSseTests
{
    [Fact]
    public async Task Http_resilience_retries_429_and_5xx_errors()
    {
        var count = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProviderHttpClient("openai", new HttpResilienceOptions { RetryCount = 2, RetryDelaySeconds = 0 })
            .ConfigurePrimaryHttpMessageHandler(() => new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(
                ++count switch { 1 => HttpStatusCode.TooManyRequests, 2 => HttpStatusCode.BadGateway, _ => HttpStatusCode.OK })
            { Content = new StringContent(Responses.OpenAi) })));
        await using var provider = services.BuildServiceProvider();
        var adapter = new OpenAiProvider(new(provider.GetRequiredService<IHttpClientFactory>()), new ProviderOptions());
        var result = await adapter.ChatCompletionAsync(ProviderHarness.Request(), default);
        Assert.Equal(3, count);
        Assert.Equal(5, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task Http_resilience_does_not_retry_authentication_failures()
    {
        var count = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProviderHttpClient("openai", new HttpResilienceOptions { RetryCount = 2, RetryDelaySeconds = 0 })
            .ConfigurePrimaryHttpMessageHandler(() => new CallbackHandler((_, _) =>
            { count++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }));
        await using var provider = services.BuildServiceProvider();
        var adapter = new OpenAiProvider(new(provider.GetRequiredService<IHttpClientFactory>()), new ProviderOptions());
        await Assert.ThrowsAsync<ProviderException>(() => adapter.ChatCompletionAsync(ProviderHarness.Request(), default));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Attempt_timeout_becomes_a_transient_provider_error()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProviderHttpClient("openai", new HttpResilienceOptions { RetryCount = 0, AttemptTimeoutSeconds = 1, TotalTimeoutSeconds = 3 })
            .ConfigurePrimaryHttpMessageHandler(() => new CallbackHandler(async (_, ct) =>
            { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new HttpResponseMessage(); }));
        await using var provider = services.BuildServiceProvider();
        var adapter = new OpenAiProvider(new(provider.GetRequiredService<IHttpClientFactory>()), new ProviderOptions());
        var error = await Assert.ThrowsAsync<ProviderException>(() => adapter.ChatCompletionAsync(ProviderHarness.Request(), default));
        Assert.Equal("provider_timeout", error.Code);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task Sse_handles_multiline_data_comments_crlf_and_unicode()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(": ping\r\nevent: update\r\ndata: first 🌍\r\ndata: second\r\n\r\n"));
        var events = new List<SseEvent>();
        await foreach (var item in SseReader.ReadAsync(stream, default)) events.Add(item);
        Assert.Equal(new SseEvent("update", "first 🌍\nsecond"), Assert.Single(events));
    }

    [Fact]
    public async Task Sse_bounds_upstream_event_size()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: " + new string('x', 1_048_577)));
        await Assert.ThrowsAsync<ProviderException>(async () => { await foreach (var _ in SseReader.ReadAsync(stream, default)) { } });
    }
}
