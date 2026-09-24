using System.Diagnostics;
using System.Diagnostics.Metrics;
using LLMProxy.Domain;

namespace LLMProxy.Application;

public sealed class GatewayTelemetry : IDisposable
{
    public const string SourceName = "LLMProxy";
    public ActivitySource ActivitySource { get; } = new(SourceName);
    private readonly Meter _meter = new(SourceName);
    private readonly Counter<long> _requests;
    private readonly Counter<long> _tokens;
    private readonly Counter<long> _errors;
    private readonly Counter<long> _fallbacks;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _providerDuration;
    private readonly Histogram<double> _firstTokenDuration;
    private readonly Counter<long> _providerRequests;
    private readonly Counter<long> _providerErrors;
    private readonly Counter<double> _cost;

    public GatewayTelemetry()
    {
        _requests = _meter.CreateCounter<long>("llmproxy.requests");
        _tokens = _meter.CreateCounter<long>("llmproxy.tokens", "{token}");
        _errors = _meter.CreateCounter<long>("llmproxy.errors");
        _fallbacks = _meter.CreateCounter<long>("llmproxy.fallbacks");
        _duration = _meter.CreateHistogram<double>("llmproxy.request.duration", "s");
        _providerDuration = _meter.CreateHistogram<double>("llmproxy.provider.duration", "s");
        _firstTokenDuration = _meter.CreateHistogram<double>("llmproxy.provider.first_token", "s");
        _providerRequests = _meter.CreateCounter<long>("llmproxy.provider.requests");
        _providerErrors = _meter.CreateCounter<long>("llmproxy.provider.errors");
        _cost = _meter.CreateCounter<double>("llmproxy.estimated_cost", "USD");
    }

    public void Record(UsageRecord usage)
    {
        var tags = new TagList { { "model", usage.Model }, { "provider", usage.Provider }, { "status", usage.Status } };
        _requests.Add(1, tags);
        _duration.Record(usage.LatencyMs / 1000d, tags);
        _cost.Add((double)usage.EstimatedCost, tags);
        if (usage.Status != "success") _errors.Add(1, tags);
        tags.Add("direction", "input");
        _tokens.Add(usage.InputTokens, tags);
        tags[tags.Count - 1] = new KeyValuePair<string, object?>("direction", "output");
        _tokens.Add(usage.OutputTokens, tags);
    }

    public void ProviderFinished(string provider, double seconds, bool success)
    {
        var tag = new KeyValuePair<string, object?>("provider", provider);
        _providerRequests.Add(1, tag);
        if (!success) _providerErrors.Add(1, tag);
        _providerDuration.Record(seconds, tag, new KeyValuePair<string, object?>("success", success));
    }
    public void ProviderFirstToken(string provider, double seconds) => _firstTokenDuration.Record(seconds,
        new KeyValuePair<string, object?>("provider", provider));
    public void Fallback(string model) => _fallbacks.Add(1, new KeyValuePair<string, object?>("model", model));
    public void Dispose() { ActivitySource.Dispose(); _meter.Dispose(); }
}
