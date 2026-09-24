using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Infrastructure.RateLimiting;

namespace LLMProxy.UnitTests;

public sealed class ExpansionTests
{
    [Fact]
    public async Task Incompatible_targets_are_removed_before_strategy_and_fallback_selection()
    {
        var first = new TestProvider("first") { Capabilities = new() { Features = ModelCapability.Chat } };
        var second = new TestProvider("second");
        var options = TestData.Options(fallback: false);
        var router = new ModelRouter(new ModelRegistry(options), [first, second], [new PriorityRouting()]);
        var request = TestData.Request() with { ResponseFormat = JsonSerializer.SerializeToElement(new { type = "json_schema" }) };
        Assert.Equal("second", Assert.Single(await router.PlanAsync(request, default)).Provider.Name);
        Assert.Equal(0, first.Calls);
    }

    [Fact]
    public async Task No_compatible_target_is_a_client_error()
    {
        var first = new TestProvider("first") { Capabilities = new() { Features = ModelCapability.Chat } };
        var second = new TestProvider("second") { Capabilities = first.Capabilities };
        var router = new ModelRouter(new ModelRegistry(TestData.Options()), [first, second], [new PriorityRouting()]);
        var request = TestData.Request() with { ReasoningEffort = "high" };
        var error = await Assert.ThrowsAsync<GatewayException>(() => router.PlanAsync(request, default));
        Assert.Equal("unsupported_model_capability", error.Code);
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task Model_capabilities_can_restrict_adapter_capabilities_and_context()
    {
        var options = TestData.Options();
        options.Models["fast"].ProviderCapabilities["first"] = new() { ContextWindowTokens = 500 };
        options.Models["fast"].ProviderCapabilities["second"] = new() { Features = ModelCapability.Chat };
        var router = new ModelRouter(new ModelRegistry(options), [new TestProvider("first"), new TestProvider("second")], [new PriorityRouting()]);
        var request = TestData.Request() with { InputTokenEstimate = 501, MaxTokens = 1 };
        Assert.Equal("second", Assert.Single(await router.PlanAsync(request, default)).Provider.Name);
    }

    [Fact]
    public void Feature_combinations_are_checked()
    {
        var capability = new ModelCapabilities { JsonWithTools = false, MixedToolStrictness = false, MaxTemperature = 1 };
        var tools = new List<ToolDefinition> { new("function", new("lookup", null, null, true)) };
        Assert.False(capability.Supports(TestData.Request() with { Tools = tools, ResponseFormat = JsonSerializer.SerializeToElement(new { type = "json_object" }) }));
        Assert.False(capability.Supports(TestData.Request() with { Temperature = 1.5 }));
        tools.Add(new("function", new("other", null, null, false)));
        Assert.False(capability.Supports(TestData.Request() with { Tools = tools }));
    }

    [Fact]
    public async Task Cost_routing_uses_the_requested_output_ceiling()
    {
        var options = TestData.Options("cost");
        options.Pricing["first/a"] = new() { InputPerMillion = 1, OutputPerMillion = 100 };
        options.Pricing["second/b"] = new() { InputPerMillion = 10, OutputPerMillion = 1 };
        var costs = new CostCalculator(new ConfiguredPricing(options));
        var router = new ModelRouter(new ModelRegistry(options), [new TestProvider("first"), new TestProvider("second")], [new CostRouting(costs)]);
        var routes = await router.PlanAsync(TestData.Request() with { InputTokenEstimate = 10, MaxTokens = 1000 }, default);
        Assert.Equal("second", routes[0].Provider.Name);
    }

    [Fact]
    public async Task Latency_routing_samples_unknown_targets_then_prefers_observed_latency()
    {
        var latency = new ProviderLatency();
        var targets = new[] { new ModelTarget("a", "one"), new ModelTarget("b", "two") };
        latency.Observe(targets[0], 2);
        var strategy = new LatencyRouting(latency);
        Assert.Equal("b", (await strategy.OrderAsync("fast", targets, default))[0].Provider);
        latency.Observe(targets[1], 5);
        Assert.Equal("a", (await strategy.OrderAsync("fast", targets, default))[0].Provider);
    }

    [Fact]
    public async Task Cost_routing_uses_captured_prices_when_the_live_catalog_has_changed()
    {
        var captured = new ConfiguredPricing(TestData.Options());
        var updated = TestData.Options();
        updated.Pricing["first/a"] = new() { InputPerMillion = 100, OutputPerMillion = 100 };
        var strategy = new CostRouting(new CostCalculator(new ConfiguredPricing(updated)));
        ModelTarget[] targets = [new("first", "a"), new("second", "b")];
        Assert.Equal("first", (await strategy.OrderAsync(TestData.Request(), targets, captured, default))[0].Provider);
        Assert.Equal("second", (await strategy.OrderAsync(TestData.Request(), targets, default))[0].Provider);
    }

    [Fact]
    public async Task Concurrency_acquisition_is_atomic_and_release_is_idempotent()
    {
        var limiter = new MemoryConcurrencyLimiter(new TestClock());
        var first = await limiter.AcquireAsync([new("global", 2), new("a", 1)], TimeSpan.FromMinutes(1), default);
        await Assert.ThrowsAsync<GatewayException>(() => limiter.AcquireAsync([new("global", 2), new("a", 1)], TimeSpan.FromMinutes(1), default));
        await using var second = await limiter.AcquireAsync([new("global", 2), new("b", 1)], TimeSpan.FromMinutes(1), default);
        await first.DisposeAsync();
        await first.DisposeAsync();
        await using var third = await limiter.AcquireAsync([new("global", 2)], TimeSpan.FromMinutes(1), default);
        await Assert.ThrowsAsync<GatewayException>(() => limiter.AcquireAsync([new("global", 2)], TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task Expired_concurrency_leases_do_not_hold_capacity_forever()
    {
        var clock = new TestClock();
        var limiter = new MemoryConcurrencyLimiter(clock);
        var expired = await limiter.AcquireAsync([new("a", 1)], TimeSpan.FromSeconds(10), default);
        clock.Advance(TimeSpan.FromSeconds(11));
        await using var current = await limiter.AcquireAsync([new("a", 1)], TimeSpan.FromSeconds(10), default);
        await expired.DisposeAsync();
        await Assert.ThrowsAsync<GatewayException>(() => limiter.AcquireAsync([new("a", 1)], TimeSpan.FromSeconds(10), default));
    }

    [Fact]
    public void Cached_tokens_and_tiers_have_distinct_prices_and_safe_reservations()
    {
        var options = TestData.Options();
        options.Pricing["first/a"] = new()
        {
            InputPerMillion = 2,
            CachedInputPerMillion = .5m,
            OutputPerMillion = 10,
            Tiers = [new() { FromInputTokens = 2000, InputPerMillion = 1, OutputPerMillion = 5 }]
        };
        var pricing = new ConfiguredPricing(options);
        Assert.Equal(.0018m, pricing.GetPrice("first/a", 1000).Calculate(TokenUsage.From(1000, 100) with { PromptTokensDetails = new(800) }));
        Assert.Equal(.000001m, pricing.GetPrice("first/a", 2000).Input);
        Assert.Equal(.000002m, pricing.GetReservationPrice("first/a", 2000).Input);
    }

    [Theory]
    [InlineData("""{"model":"fast","input":"","encoding_format":"float"}""")]
    [InlineData("""{"model":"fast","input":["ok",3]}""")]
    [InlineData("""{"model":"fast","input":"ok","dimensions":0}""")]
    [InlineData("""{"model":"fast","input":"ok","encoding_format":"invalid"}""")]
    public void Embeddings_validate_their_protocol_before_admission(string body)
    {
        var options = TestData.Options();
        var validator = new ProtocolRequests(options, new ModelRegistry(options));
        Assert.Throws<GatewayException>(() => validator.Validate(GatewayOperation.Embeddings, JsonNode.Parse(body)!.AsObject(), TestData.Key()));
    }

    [Theory]
    [InlineData("store", "true")]
    [InlineData("background", "true")]
    [InlineData("previous_response_id", "\"resp_other\"")]
    public void Responses_do_not_allow_unowned_state_or_unaccounted_background_work(string field, string value)
    {
        var options = TestData.Options();
        var body = JsonNode.Parse("""{"model":"fast","input":"hello"}""")!.AsObject();
        body[field] = JsonNode.Parse(value);
        Assert.Throws<GatewayException>(() => new ProtocolRequests(options, new ModelRegistry(options)).Validate(GatewayOperation.Responses, body, TestData.Key()));
    }

    [Theory]
    [InlineData("{\"type\":\"input_file\",\"file_id\":\"file-other-key\"}")]
    [InlineData("{\"type\":\"input_image\",\"file_id\":\"file-other-key\"}")]
    [InlineData("{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,not-valid!\"}")]
    public void Responses_function_outputs_cannot_bypass_media_and_file_ownership_checks(string part)
    {
        var options = TestData.Options();
        var body = new JsonObject
        {
            ["model"] = "fast",
            ["input"] = new JsonArray(new JsonObject
            { ["type"] = "function_call_output", ["call_id"] = "call-test", ["output"] = new JsonArray(JsonNode.Parse(part)) })
        };
        Assert.Throws<GatewayException>(() => new ProtocolRequests(options, new ModelRegistry(options))
            .Validate(GatewayOperation.Responses, body, TestData.Key()));
    }

    [Fact]
    public void Responses_function_outputs_contribute_their_media_capability_requirements()
    {
        var options = TestData.Options();
        var body = JsonNode.Parse("{\"model\":\"fast\",\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"call-test\",\"output\":[{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,AQID\"}]}]}")!.AsObject();
        var prepared = new ProtocolRequests(options, new ModelRegistry(options)).Validate(GatewayOperation.Responses, body, TestData.Key());
        Assert.True(ModelCapabilities.Required(prepared.Admission).HasFlag(ModelCapability.ImageInput));
    }
}
