using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Infrastructure.RateLimiting;

namespace LLMProxy.UnitTests;

public sealed class RoutingTests
{
    [Theory]
    [InlineData("priority")]
    [InlineData("fallback")]
    public async Task Priority_and_fallback_preserve_provider_order(string strategy)
    {
        var router = Create(strategy);
        Assert.Equal(["first", "second"], (await router.PlanAsync("fast", default)).Select(x => x.Provider.Name));
    }

    [Fact]
    public async Task Round_robin_rotates_with_concurrent_requests()
    {
        var router = Create("round-robin");
        var requests = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => router.SelectProviderAsync("fast", default)));
        Assert.Equal(50, requests.Count(x => x.Name == "first"));
        Assert.Equal(50, requests.Count(x => x.Name == "second"));
    }

    [Fact]
    public async Task Random_preserves_each_candidate_once()
    {
        var router = Create("random");
        for (var index = 0; index < 30; index++)
            Assert.Equal(["first", "second"], (await router.PlanAsync("fast", default)).Select(x => x.Provider.Name).Order());
    }

    [Fact]
    public async Task Missing_credentials_are_skipped_and_fallback_can_be_disabled()
    {
        var router = Create("priority", false, new TestProvider("first", configured: false), new TestProvider("second"));
        Assert.Equal("second", Assert.Single(await router.PlanAsync("fast", default)).Provider.Name);
    }

    [Fact]
    public async Task Unknown_models_and_unconfigured_models_have_distinct_errors()
    {
        var router = Create("priority", true, new TestProvider("first", false), new TestProvider("second", false));
        Assert.Equal("model_not_found", (await Assert.ThrowsAsync<GatewayException>(() => router.PlanAsync("missing", default))).Code);
        Assert.Equal("provider_unavailable", (await Assert.ThrowsAsync<GatewayException>(() => router.PlanAsync("fast", default))).Code);
    }

    internal static ModelRouter Create(string strategy = "priority", bool fallback = true, params ILlmProvider[] providers)
        => new(new ModelRegistry(TestData.Options(strategy, fallback)), providers.Length == 0 ? [new TestProvider("first"), new TestProvider("second")] : providers,
            [new PriorityRouting(), new FallbackRouting(), new RoundRobinRouting(new MemoryRoutingState()), new RandomRouting()]);
}
