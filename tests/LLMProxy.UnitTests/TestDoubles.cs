using System.Runtime.CompilerServices;
using LLMProxy.Application;
using LLMProxy.Domain;

namespace LLMProxy.UnitTests;

internal sealed class TestProvider(string name, bool configured = true) : ILlmProvider
{
    public string Name => name;
    public bool IsConfigured => configured;
    public ModelCapabilities Capabilities { get; set; } = new();
    public int Calls { get; private set; }
    public Func<LlmRequest, CancellationToken, Task<LlmResponse>>? Complete { get; set; }
    public bool FailBeforeStream { get; set; }
    public bool FailAfterStream { get; set; }
    public string? SeenModel { get; private set; }
    public Task<LlmResponse> ChatCompletionAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        SeenModel = request.Model;
        return Complete?.Invoke(request, cancellationToken) ?? Task.FromResult(new LlmResponse("upstream", request.Model, 1,
            [new ChatChoice(0, ChatMessage.FromText("assistant", "Hello"), "stop")], TokenUsage.From(3, 2)));
    }
    public async IAsyncEnumerable<LlmStreamChunk> StreamChatCompletionAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls++;
        if (FailBeforeStream) throw new ProviderException("unavailable", true);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new LlmStreamChunk(new ChatDelta(Content: "Hello"));
        if (FailAfterStream) throw new ProviderException("interrupted", true);
        yield return new LlmStreamChunk(FinishReason: "stop", Usage: TokenUsage.From(3, 2));
    }
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}

internal sealed class TestStore : IGatewayStore
{
    public Dictionary<Guid, ApiKey> Keys { get; } = [];
    public Dictionary<Guid, QuotaReservation> Reservations { get; } = [];
    public List<UsageRecord> Usage { get; } = [];
    public Task<ApiKey?> FindKeyAsync(string hash, CancellationToken cancellationToken) => Task.FromResult(Keys.Values.FirstOrDefault(x => x.KeyHash == hash));
    public Task<ApiKey?> FindKeyByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Keys.GetValueOrDefault(id));
    public Task TouchKeyAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken) { Keys[id].LastUsedAt = now; return Task.CompletedTask; }
    public Task CreateKeyAsync(ApiKey key, CancellationToken cancellationToken) { Keys.Add(key.Id, key); return Task.CompletedTask; }
    public Task<IReadOnlyList<ApiKey>> ListKeysAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ApiKey>>(Keys.Values.Take(limit).ToArray());
    public Task<ApiKey?> UpdateKeyAsync(Guid id, ApiKeyPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var key = Keys.GetValueOrDefault(id);
        if (key is not null) { key.Enabled = policy.Enabled; key.AllowedModels = policy.AllowedModels; }
        return Task.FromResult(key);
    }
    public Task<bool> TryReserveAsync(QuotaReservation reservation, CancellationToken cancellationToken)
    { Reservations.Add(reservation.RequestId, reservation); return Task.FromResult(true); }
    public Task CompleteAsync(UsageRecord record, CancellationToken cancellationToken)
    { if (Reservations.Remove(record.RequestId)) Usage.Add(record); return Task.CompletedTask; }
    public Task<IReadOnlyList<UsageRecord>> ListUsageAsync(Guid? apiKeyId, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UsageRecord>>(Usage);
    public Task<int> RecoverExpiredReservationsAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(0);
}

internal static class TestData
{
    public static GatewayOptions Options(string routing = "priority", bool fallback = true) => new()
    {
        Models = new()
        {
            ["fast"] = new ModelOptions { Providers = ["first", "second"], ProviderModels = new() { ["first"] = "a", ["second"] = "b" }, Routing = routing, EnableFallback = fallback }
        },
        Pricing = new()
        {
            ["first/a"] = new PriceOptions { InputPerMillion = 1, OutputPerMillion = 2 },
            ["second/b"] = new PriceOptions { InputPerMillion = 3, OutputPerMillion = 4 }
        }
    };
    public static LlmRequest Request(bool stream = false) => new() { Model = "fast", Messages = [ChatMessage.FromText("user", "Hello")], Stream = stream };
    public static ApiKey Key() => new() { Owner = "test", AllowedModels = ["fast"], RequestsPerMinute = 60 };
}
