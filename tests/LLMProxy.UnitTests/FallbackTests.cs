using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Infrastructure.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMProxy.UnitTests;

public sealed class FallbackTests
{
    [Fact]
    public async Task Transient_failure_uses_next_provider_and_tracks_its_actual_price()
    {
        using var fixture = new Fixture();
        fixture.First.Complete = (_, _) => throw new ProviderException("provider_rate_limited", true, 429);
        var response = await fixture.Service.CompleteAsync(TestData.Request(), fixture.Context, default);
        Assert.Equal("fast", response.Model);
        Assert.Equal("b", fixture.Second.SeenModel);
        Assert.Equal(1, fixture.First.Calls);
        Assert.Equal(1, fixture.Second.Calls);
        var usage = Assert.Single(fixture.Store.Usage);
        Assert.Equal("second", usage.Provider);
        Assert.Equal("success", usage.Status);
        Assert.Equal(0.000017m, usage.EstimatedCost);
        Assert.Empty(fixture.Store.Reservations);
    }

    [Fact]
    public async Task Nontransient_failures_are_not_replayed()
    {
        using var fixture = new Fixture();
        fixture.First.Complete = (_, _) => throw new ProviderException("provider_rejected_request", false, 400);
        await Assert.ThrowsAsync<ProviderException>(() => fixture.Service.CompleteAsync(TestData.Request(), fixture.Context, default));
        Assert.Equal(0, fixture.Second.Calls);
        Assert.Equal("provider_rejected_request", Assert.Single(fixture.Store.Usage).ErrorCode);
    }

    [Fact]
    public async Task Missing_usage_accounts_for_reasoning_as_well_as_visible_output()
    {
        using var fixture = new Fixture();
        fixture.First.Complete = (request, _) => Task.FromResult(new LlmResponse("upstream", request.Model, 1,
            [new ChatChoice(0, ChatMessage.FromText("assistant", "OK") with { ReasoningContent = "why" }, "stop")], TokenUsage.Zero));
        var response = await fixture.Service.CompleteAsync(TestData.Request(), fixture.Context, default);
        Assert.Equal(5, response.Usage.OutputTokens);
        var record = Assert.Single(fixture.Store.Usage);
        Assert.True(record.UsageEstimated);
        Assert.Equal(5, record.OutputTokens);
        Assert.Equal(response.Usage.TotalTokens, record.TotalTokens);
    }

    [Fact]
    public async Task Streams_can_fall_back_before_the_first_chunk()
    {
        using var fixture = new Fixture();
        fixture.First.FailBeforeStream = true;
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in fixture.Service.StreamAsync(TestData.Request(true) with { StreamOptions = new(true) }, fixture.Context, default)) chunks.Add(chunk);
        Assert.Equal("Hello", chunks[0].Delta!.Content);
        Assert.Equal(5, chunks.Last().Usage!.TotalTokens);
        Assert.Equal("second", Assert.Single(fixture.Store.Usage).Provider);
    }

    [Fact]
    public async Task Streams_never_replay_after_a_chunk_is_emitted()
    {
        using var fixture = new Fixture();
        fixture.First.FailAfterStream = true;
        var chunks = new List<LlmStreamChunk>();
        await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var chunk in fixture.Service.StreamAsync(TestData.Request(true), fixture.Context, default)) chunks.Add(chunk);
        });
        Assert.Single(chunks);
        Assert.Equal(0, fixture.Second.Calls);
        Assert.Equal("error", Assert.Single(fixture.Store.Usage).Status);
        Assert.Empty(fixture.Store.Reservations);
    }

    [Fact]
    public async Task Client_cancellation_still_finalizes_usage()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.First.Complete = (_, ct) => { cancellation.Cancel(); return Task.FromCanceled<LlmResponse>(ct); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.CompleteAsync(TestData.Request(), fixture.Context, cancellation.Token));
        Assert.Equal("cancelled", Assert.Single(fixture.Store.Usage).Status);
        Assert.True(fixture.Store.Usage[0].TotalTokens > 0);
        Assert.Equal(0, fixture.Second.Calls);
        Assert.Empty(fixture.Store.Reservations);
    }

    [Fact]
    public async Task Disposing_a_stream_cleans_up_its_reservation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await using (var stream = fixture.Service.StreamAsync(TestData.Request(true), fixture.Context, cancellation.Token).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            cancellation.Cancel();
        }
        Assert.Empty(fixture.Store.Reservations);
        Assert.Equal("cancelled", Assert.Single(fixture.Store.Usage).Status);
        Assert.True(fixture.Store.Usage[0].UsageEstimated);
        Assert.Equal(1024, fixture.Store.Usage[0].OutputTokens);
    }

    private sealed class Fixture : IDisposable
    {
        public TestProvider First { get; } = new("first");
        public TestProvider Second { get; } = new("second");
        public TestStore Store { get; } = new();
        private readonly MemoryRateLimiter _limiter = new(TimeProvider.System);
        private readonly GatewayTelemetry _telemetry = new();
        public GatewayRequestContext Context { get; } = new(Guid.NewGuid(), TestData.Key());
        public GatewayService Service { get; }
        public Fixture()
        {
            var options = TestData.Options();
            var registry = new ModelRegistry(options);
            Service = new GatewayService(new RequestValidator(options, registry), registry,
                RoutingTests.Create("fallback", true, First, Second), _limiter, Store, new CostCalculator(new ConfiguredPricing(options)),
                options, _telemetry, TimeProvider.System, NullLogger<GatewayService>.Instance);
        }
        public void Dispose() { _limiter.Dispose(); _telemetry.Dispose(); }
    }
}
