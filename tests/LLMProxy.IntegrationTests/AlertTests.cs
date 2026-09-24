using System.Net;
using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMProxy.IntegrationTests;

public sealed class AlertTests
{
    [Fact]
    public async Task Alerts_cover_budgets_errors_latency_and_pool_exhaustion_and_resolve() => await Evaluate(false);
    [PostgresFact]
    public async Task Postgres_alerts_cover_budgets_errors_latency_and_pool_exhaustion_and_resolve() => await Evaluate(true);

    private static async Task Evaluate(bool postgres)
    {
        await using var fixture = await StoreFixture.CreateAsync(postgres);
        var clock = new MutableClock();
        var now = clock.GetUtcNow();
        var key = await fixture.KeyAsync(budgetUnits: 1000);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            await db.ApiKeys.Where(x => x.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SpentUnits, 810L)
                .SetProperty(x => x.MonthlyBudgetUnits, 1000L));
            db.QuotaWindows.Add(new QuotaWindow
            {
                Id = key.Id.ToString("N") + ":" + now.ToString("yyyy-MM"),
                ApiKeyId = key.Id,
                Period = now.ToString("yyyy-MM"),
                SpentUnits = 810
            });
            db.Attempts.AddRange(Enumerable.Range(0, 20).Select(i => new UpstreamAttempt
            {
                Provider = "openai",
                Model = "fixture",
                CreatedAt = now,
                HttpStatus = i < 5 ? 500 : 200,
                LatencyMs = 1000,
                RequestId = Guid.NewGuid(),
                Status = i < 5 ? "error" : "success"
            }));
            await db.SaveChangesAsync();
        }
        var store = (IAlertStore)fixture.Store;
        var providers = new FakeProviders { Exhausted = true, Clock = clock };
        var evaluator = new AlertService(store, providers, new AlertOptions { AverageLatencyMs = 500 }, clock);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => evaluator.EvaluateAsync(default)));
        var alerts = await store.ListAsync(false, 100, default);
        Assert.Equal(5, alerts.Count);
        Assert.Equal(2, alerts.Count(x => x.Kind == "budget"));
        Assert.Contains(alerts, x => x.Kind == "error_spike");
        Assert.Contains(alerts, x => x.Kind == "high_latency");
        Assert.Contains(alerts, x => x.Kind == "pool_exhausted");
        Assert.All(alerts, x => Assert.Equal(1, x.Occurrences));
        await store.AcknowledgeAsync(alerts[0].Id, "operator-fixture", now, default);
        Assert.Contains(await store.ListAsync(false, 100, default), x => x.AcknowledgedBy == "operator-fixture");
        clock.Advance(TimeSpan.FromMinutes(6));
        providers.Exhausted = false;
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            await db.ApiKeys.Where(x => x.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SpentUnits, 0L));
            await db.QuotaWindows.ExecuteUpdateAsync(s => s.SetProperty(x => x.SpentUnits, 0L));
        }
        await evaluator.EvaluateAsync(default);
        Assert.Empty(await store.ListAsync(false, 100, default));
        Assert.All(await store.ListAsync(true, 100, default), x => Assert.NotNull(x.ResolvedAt));
        providers.Exhausted = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        await evaluator.EvaluateAsync(default);
        var reopened = Assert.Single(await store.ListAsync(false, 100, default));
        Assert.Equal("pool_exhausted", reopened.Kind);
        Assert.Equal(2, reopened.Occurrences);
        Assert.Null(reopened.AcknowledgedAt);
        // An older evaluation cannot reopen or resolve a newer incident.
        await store.ApplyAsync([], now, default);
        Assert.Single(await store.ListAsync(false, 100, default));
    }

    [Fact]
    public async Task Delivery_is_leased_across_workers_retries_and_uses_a_stable_idempotency_key() => await Deliver(false);
    [PostgresFact]
    public async Task Postgres_delivery_is_leased_across_workers_retries_and_uses_a_stable_idempotency_key() => await Deliver(true);

    private static async Task Deliver(bool postgres)
    {
        await using var fixture = await StoreFixture.CreateAsync(postgres);
        var store = (IAlertStore)fixture.Store;
        var clock = new MutableClock();
        await store.ApplyAsync([new("pool:fixture", "pool_exhausted", "fixture", "critical", "Pool exhausted.")], clock.GetUtcNow(), default);
        var claims = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => store.ClaimDeliveryAsync(clock.GetUtcNow(), default)));
        var claimed = Assert.Single(claims, x => x is not null)!;
        await store.CompleteDeliveryAsync(claimed.Id, claimed.DeliveryLeaseUntil!.Value, false, clock.GetUtcNow(), default);
        Assert.Null(await store.ClaimDeliveryAsync(clock.GetUtcNow(), default));
        clock.Advance(TimeSpan.FromMinutes(6));
        var options = new AlertOptions { WebhookUrl = "https://alert-destination.test/hook", WebhookBearerToken = "webhook-fixture" };
        var handler = new WebhookHandler();
        using var client = new HttpClient(handler);
        var worker = new AlertWorker(new AlertService(store, new FakeProviders { Clock = clock }, options, clock), store, options,
            new FixedClients(client), clock, NullLogger<AlertWorker>.Instance);
        await worker.DeliverAsync(default);
        Assert.Single(handler.IdempotencyKeys);
        Assert.Null((await store.ListAsync(false, 100, default))[0].DeliveredAt);
        clock.Advance(TimeSpan.FromMinutes(6));
        handler.Success = true;
        await worker.DeliverAsync(default);
        Assert.Equal(2, handler.IdempotencyKeys.Count);
        Assert.Equal(handler.IdempotencyKeys[0], handler.IdempotencyKeys[1]);
        Assert.NotNull((await store.ListAsync(false, 100, default))[0].DeliveredAt);
        await worker.DeliverAsync(default);
        Assert.Equal(2, handler.IdempotencyKeys.Count);
    }

    [Theory]
    [InlineData("http://destination.test/hook")]
    [InlineData("https://user:password@destination.test/hook")]
    [InlineData("file:///tmp/hook")]
    public void Invalid_webhook_settings_are_rejected(string url) =>
        Assert.Throws<InvalidOperationException>(() => new AlertOptions { WebhookUrl = url }.Validate());

    private sealed class FakeProviders : IProviderOperations
    {
        public bool Exhausted { get; set; }
        public required TimeProvider Clock { get; init; }
        public Task<IReadOnlyList<ProviderSummary>> ListAsync(CancellationToken cancellationToken)
        {
            var until = Exhausted ? Clock.GetUtcNow().AddMinutes(1) : (DateTimeOffset?)null;
            return Task.FromResult<IReadOnlyList<ProviderSummary>>([new("openai", "openai", "https://fixture.test", true, true, false, 30,
                [new("key-a", "", "configuration", true, until, 0, 0, 0, 0, 0, 0), new("key-b", "", "configuration", true, until, 0, 0, 0, 0, 0, 0)])]);
        }
        public Task AddKeyAsync(string provider, AddProviderKey request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateKeyAsync(string provider, string keyId, UpdateProviderKey request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderCheckResult>> CheckAsync(string provider, ProviderCheckRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class FixedClients(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class WebhookHandler : HttpMessageHandler
    {
        public bool Success { get; set; }
        public List<string> IdempotencyKeys { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("webhook-fixture", request.Headers.Authorization?.Parameter);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain("webhook-fixture", body);
            Assert.Contains("pool_exhausted", body);
            IdempotencyKeys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            return new(Success ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable);
        }
    }
}
