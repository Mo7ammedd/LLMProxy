using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using LLMProxy.Domain;
using LLMProxy.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LLMProxy.ProviderTests;

public sealed class ProviderKeyPoolTests
{
    private const string FirstKey = "first-provider-fixture-key";
    private const string SecondKey = "second-provider-fixture-key";

    public static TheoryData<string, bool> Adapters
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var adapter in ProviderAccounts.BuiltIns) { data.Add(adapter, false); data.Add(adapter, true); }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task All_adapters_rotate_pool_keys_for_normal_and_streaming_chat(string adapter, bool streaming)
    {
        var observed = new List<string>();
        using var container = Configure(adapter, (request, _) =>
        {
            observed.Add(Key(request, adapter));
            return Task.FromResult(Response(streaming ? Responses.Stream(adapter) : Responses.Body(adapter), streaming));
        }, customize: (settings, _) => settings["LLMProxy:Providers:"
            + (adapter switch { "openai" => "OpenAI", "azure" => "AzureOpenAI", _ => adapter }) + ":ApiKey"] = "");
        var provider = Provider(container, adapter);
        Assert.True(provider.IsConfigured);
        for (var i = 0; i < 3; i++)
        {
            if (streaming)
            {
                var text = new StringBuilder();
                await foreach (var chunk in provider.StreamChatCompletionAsync(ProviderHarness.Request(true), default))
                    text.Append(chunk.Delta?.Content);
                Assert.Equal("Hello", text.ToString());
            }
            else Assert.Equal("Hello", (await provider.ChatCompletionAsync(ProviderHarness.Request(), default)).Choices[0].Message.Text());
        }
        Assert.Equal(new[] { FirstKey, SecondKey, FirstKey }, observed);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Named_accounts_keep_pool_keys_and_native_headers(string adapter, bool streaming)
    {
        var observed = new List<string>();
        using var container = Configure(adapter, (request, _) =>
        {
            observed.Add(Key(request, adapter));
            Assert.Equal("account.test", request.RequestUri!.Host);
            return Task.FromResult(Response(streaming ? Responses.Stream(adapter) : Responses.Body(adapter), streaming));
        }, named: true);
        var provider = Provider(container, "account");
        Assert.True(provider.IsConfigured);
        for (var i = 0; i < 2; i++)
        {
            if (streaming) await foreach (var _ in provider.StreamChatCompletionAsync(ProviderHarness.Request(true), default)) { }
            else await provider.ChatCompletionAsync(ProviderHarness.Request(), default);
        }
        Assert.Equal(new[] { FirstKey, SecondKey }, observed);
    }

    [Fact]
    public async Task Concurrent_requests_distribute_evenly_without_mixing_credentials()
    {
        var observed = new ConcurrentBag<string>();
        using var container = Configure("openai", async (request, ct) =>
        {
            var key = Key(request, "openai");
            await Task.Yield();
            Assert.Equal(key, Key(request, "openai"));
            Assert.Contains("Hello", await request.Content!.ReadAsStringAsync(ct));
            observed.Add(key);
            return Response(Responses.OpenAi);
        });
        var provider = Provider(container);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => provider.ChatCompletionAsync(ProviderHarness.Request(), default)));
        Assert.Equal(50, observed.Count(key => key == FirstKey));
        Assert.Equal(50, observed.Count(key => key == SecondKey));
    }

    [Fact]
    public async Task Healthy_keys_remain_balanced_while_another_key_cools_down()
    {
        const string thirdKey = "third-provider-fixture-key";
        var observed = new List<string>();
        using var container = Configure("openai", (request, _) =>
        {
            var key = Key(request, "openai");
            observed.Add(key);
            return Task.FromResult(key == FirstKey ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Response(Responses.OpenAi));
        }, customize: (settings, _) => settings["LLMProxy:Providers:OpenAI:ApiKeys:2"] = thirdKey);
        var provider = Provider(container);
        await provider.ChatCompletionAsync(ProviderHarness.Request(), default);
        observed.Clear();
        for (var i = 0; i < 60; i++) await provider.ChatCompletionAsync(ProviderHarness.Request(), default);
        Assert.Equal(30, observed.Count(key => key == SecondKey));
        Assert.Equal(30, observed.Count(key => key == thirdKey));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Rejected_keys_fail_over_and_cool_down_without_leaking_secrets(HttpStatusCode failure)
    {
        var clock = new PoolClock();
        var observed = new List<string>();
        using var container = Configure("openai", (request, _) =>
        {
            var key = Key(request, "openai");
            observed.Add(key);
            return Task.FromResult(key == FirstKey ? new HttpResponseMessage(failure)
            { Content = new StringContent("private upstream error " + FirstKey) } : Response(Responses.OpenAi));
        }, clock: clock);
        var attempts = new AttemptContext(Guid.NewGuid(), new ModelTarget("openai", "test"), clock);
        using (attempts.Enter()) await Provider(container).ChatCompletionAsync(ProviderHarness.Request(), default);
        Assert.Equal(new[] { FirstKey, SecondKey }, observed);
        Assert.Equal(2, attempts.Attempts.Count);
        Assert.Equal(2, attempts.Attempts.Select(attempt => attempt.ProviderKeyId).Distinct().Count());
        Assert.All(attempts.Attempts, attempt => Assert.Matches("^key_[a-f0-9]{32}$", attempt.ProviderKeyId!));
        var provider = Provider(container);
        await provider.ChatCompletionAsync(ProviderHarness.Request(), default);
        await provider.ChatCompletionAsync(ProviderHarness.Request(), default);
        Assert.Equal(1, observed.Count(key => key == FirstKey));
        clock.Advance(TimeSpan.FromSeconds(31));
        await provider.ChatCompletionAsync(ProviderHarness.Request(), default);
        await provider.ChatCompletionAsync(ProviderHarness.Request(), default);
        Assert.Equal(2, observed.Count(key => key == FirstKey));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 1)]
    [InlineData(HttpStatusCode.BadGateway, 3)]
    public async Task Rate_limited_keys_switch_immediately_while_transient_errors_keep_bounded_http_retries(HttpStatusCode failure, int expectedFailures)
    {
        var observed = new List<string>();
        using var container = Configure("openai", (request, _) =>
        {
            var key = Key(request, "openai");
            observed.Add(key);
            if (key == SecondKey) return Task.FromResult(Response(Responses.OpenAi));
            var response = new HttpResponseMessage(failure);
            if (failure == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        }, retries: 2);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var attempts = new AttemptContext(Guid.NewGuid(), new ModelTarget("openai", "test"), TimeProvider.System);
        using (attempts.Enter()) await Provider(container).ChatCompletionAsync(ProviderHarness.Request(), deadline.Token);
        Assert.Equal(expectedFailures, observed.Count(key => key == FirstKey));
        Assert.Equal(SecondKey, observed[^1]);
        Assert.Equal(expectedFailures + 1, attempts.Attempts.Count);
        Assert.Equal(2, attempts.Attempts.Select(attempt => attempt.ProviderKeyId).Distinct().Count());
        Assert.All(attempts.Attempts, attempt => Assert.NotNull(attempt.ProviderKeyId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_keys_cooling_down_fail_fast_and_respect_retry_after(bool dateHeader)
    {
        var clock = new PoolClock();
        var calls = 0;
        using var container = Configure("openai", (_, _) =>
        {
            calls++;
            if (calls > 2) return Task.FromResult(Response(Responses.OpenAi));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = dateHeader ? new RetryConditionHeaderValue(clock.GetUtcNow().AddMinutes(2))
                : new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        }, clock: clock);
        var provider = Provider(container);
        var exhausted = await Assert.ThrowsAsync<ProviderException>(() => provider.ChatCompletionAsync(ProviderHarness.Request(), default));
        Assert.Equal("provider_rate_limited", exhausted.Code);
        clock.Advance(TimeSpan.FromSeconds(60));
        var cooling = await Assert.ThrowsAsync<ProviderException>(() => provider.ChatCompletionAsync(ProviderHarness.Request(), default));
        Assert.Equal("provider_keys_unavailable", cooling.Code);
        Assert.Equal(60, cooling.RetryAfterSeconds);
        Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(61));
        await provider.ChatCompletionAsync(ProviderHarness.Request(), default);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Invalid_requests_are_not_replayed_across_keys(HttpStatusCode failure)
    {
        var calls = 0;
        using var container = Configure("openai", (_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage(failure)); });
        var error = await Assert.ThrowsAsync<ProviderException>(() => Provider(container).ChatCompletionAsync(ProviderHarness.Request(), default));
        Assert.Equal("provider_rejected_request", error.Code);
        Assert.False(error.IsTransient);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancellation_does_not_try_another_key()
    {
        var calls = 0;
        using var deadline = new CancellationTokenSource();
        using var container = Configure("openai", (_, ct) =>
        {
            calls++;
            deadline.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Response(Responses.OpenAi));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(container).ChatCompletionAsync(ProviderHarness.Request(), deadline.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_failing_keys_circuit_does_not_disable_healthy_keys()
    {
        var failingCalls = 0;
        var successfulCalls = 0;
        using var container = Configure("openai", (request, _) =>
        {
            if (Key(request, "openai") == FirstKey)
            { failingCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
            successfulCalls++;
            return Task.FromResult(Response(Responses.OpenAi));
        });
        var provider = Provider(container);
        for (var i = 0; i < 240; i++) await provider.ChatCompletionAsync(ProviderHarness.Request(), default);
        Assert.Equal(100, failingCalls);
        Assert.Equal(240, successfulCalls);
    }

    [Fact]
    public async Task Chat_embeddings_and_responses_share_the_same_key_pool()
    {
        var observed = new List<string>();
        using var container = Configure("openai", (request, _) =>
        {
            observed.Add(Key(request, "openai"));
            return Task.FromResult(Response(request.RequestUri!.AbsolutePath switch
            {
                "/embeddings" => """{"object":"list","data":[],"usage":{"prompt_tokens":3,"total_tokens":3}}""",
                "/responses" => """{"object":"response","status":"completed","output":[]}""",
                _ => Responses.OpenAi
            }));
        });
        var provider = Provider(container);
        await provider.ChatCompletionAsync(ProviderHarness.Request(), default);
        await ((IProtocolProvider)provider).CompleteProtocolAsync(GatewayOperation.Embeddings, new JsonObject { ["model"] = "test", ["input"] = "hi" }, default);
        await ((IProtocolProvider)provider).CompleteProtocolAsync(GatewayOperation.Responses, new JsonObject { ["model"] = "test", ["input"] = "hi" }, default);
        Assert.Equal(new[] { FirstKey, SecondKey, FirstKey }, observed);
    }

    [Fact]
    public async Task Foundry_Entra_authentication_does_not_use_configured_api_keys()
    {
        var observed = new List<string>();
        using var container = Configure("foundry", (request, _) =>
        {
            observed.Add(Key(request, "foundry"));
            return Task.FromResult(Response(Responses.OpenAi));
        }, customize: (settings, services) =>
        {
            settings["LLMProxy:Providers:Foundry:Authentication"] = "EntraId";
            services.AddSingleton<TokenCredential>(new FixtureCredential());
        });
        await Provider(container, "foundry").ChatCompletionAsync(ProviderHarness.Request(), default);
        Assert.Equal(new[] { "entra-fixture-token" }, observed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("secret\r\nInjected:yes")]
    [InlineData(FirstKey)]
    public void Invalid_or_duplicate_pool_keys_are_rejected_without_echoing_credentials(string invalid)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Configure("openai", (_, _) => Task.FromResult(Response(Responses.OpenAi)),
            customize: (settings, _) => settings["LLMProxy:Providers:OpenAI:ApiKeys:1"] = invalid));
        Assert.DoesNotContain(FirstKey, error.Message);
        Assert.DoesNotContain("Injected", error.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public void Invalid_key_cooldowns_fail_configuration_validation(int seconds)
        => Assert.Throws<InvalidOperationException>(() => Configure("openai", (_, _) => Task.FromResult(Response(Responses.OpenAi)),
            customize: (settings, _) => settings["LLMProxy:Providers:OpenAI:ApiKeyCooldownSeconds"] = seconds.ToString()));

    [Fact]
    public void Oversized_key_pools_fail_configuration_validation()
        => Assert.Throws<InvalidOperationException>(() => Configure("openai", (_, _) => Task.FromResult(Response(Responses.OpenAi)),
            customize: (settings, _) =>
            {
                for (var i = 0; i < 65; i++) settings["LLMProxy:Providers:OpenAI:ApiKeys:" + i] = "fixture-key-" + i;
            }));

    private static ServiceProvider Configure(string adapter, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback,
        bool named = false, TimeProvider? clock = null, int retries = 0,
        Action<Dictionary<string, string?>, IServiceCollection>? customize = null)
    {
        var section = adapter switch { "openai" => "OpenAI", "azure" => "AzureOpenAI", _ => adapter };
        var prefix = "LLMProxy:Providers:" + (named ? "Accounts:account" : section);
        var settings = new Dictionary<string, string?>
        {
            [prefix + ":BaseUrl"] = named ? "https://account.test" : "https://example.test",
            [prefix + ":ApiKey"] = "legacy-key-must-not-be-used",
            [prefix + ":ApiKeys:0"] = FirstKey,
            [prefix + ":ApiKeys:1"] = SecondKey,
            ["LLMProxy:Providers:Resilience:RetryCount"] = retries.ToString(),
            ["LLMProxy:Providers:Resilience:RetryDelaySeconds"] = "0"
        };
        if (named) settings[prefix + ":Adapter"] = adapter;
        var services = new ServiceCollection();
        services.AddLogging();
        if (clock is not null) services.AddSingleton(clock);
        customize?.Invoke(settings, services);
        services.AddLlmProxyProviders(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddHttpClient("llmproxy." + (named ? "account" : adapter))
            .ConfigurePrimaryHttpMessageHandler(() => new CallbackHandler(callback));
        return services.BuildServiceProvider();
    }

    private static ILlmProvider Provider(IServiceProvider services, string name = "openai")
        => services.GetServices<ILlmProvider>().Single(provider => provider.Name == name);
    private static string Key(HttpRequestMessage request, string adapter) => adapter switch
    {
        "anthropic" => request.Headers.GetValues("x-api-key").Single(),
        "gemini" => request.Headers.GetValues("x-goog-api-key").Single(),
        "azure" => request.Headers.GetValues("api-key").Single(),
        _ => request.Headers.Authorization!.Parameter!
    };
    private static HttpResponseMessage Response(string content, bool streaming = false) => new(HttpStatusCode.OK)
    { Content = new StringContent(content, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };

    private sealed class PoolClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class FixtureCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("entra-fixture-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
