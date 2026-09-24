using LLMProxy.Domain;

namespace LLMProxy.Application;

public sealed class AlertOptions
{
    public bool Enabled { get; set; } = true;
    public int EvaluationSeconds { get; set; } = 60;
    public int WindowMinutes { get; set; } = 5;
    public int MinimumAttempts { get; set; } = 20;
    public double BudgetPercent { get; set; } = 80;
    public double ErrorPercent { get; set; } = 20;
    public double AverageLatencyMs { get; set; } = 10000;
    public string WebhookUrl { get; set; } = "";
    public string WebhookBearerToken { get; set; } = "";
    public bool AllowInsecureWebhook { get; set; }

    public void Validate()
    {
        if (EvaluationSeconds is < 10 or > 3600 || WindowMinutes is < 1 or > 1440 || MinimumAttempts is < 1 or > 1000000
            || !double.IsFinite(BudgetPercent) || BudgetPercent is <= 0 or > 100
            || !double.IsFinite(ErrorPercent) || ErrorPercent is <= 0 or > 100
            || !double.IsFinite(AverageLatencyMs) || AverageLatencyMs is < 1 or > 3600000
            || WebhookBearerToken.Any(c => c is < '!' or > '~') || WebhookBearerToken.Length > 8192)
            throw new InvalidOperationException("Invalid operational alert settings.");
        if (WebhookUrl.Length > 0 && (!Uri.TryCreate(WebhookUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" && !(AllowInsecureWebhook && uri.Scheme == "http")
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0))
            throw new InvalidOperationException("The alert webhook must use HTTPS (or explicitly enabled HTTP) without user information or a fragment.");
    }
}

public sealed class AlertService(IAlertStore store, IProviderOperations providers, AlertOptions options, TimeProvider time)
{
    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return;
        var now = time.GetUtcNow();
        var inputs = await store.InputsAsync(now.AddMinutes(-options.WindowMinutes), now, options.BudgetPercent / 100, cancellationToken);
        var observations = inputs.Budgets.ToList();
        foreach (var sample in inputs.Providers.Where(x => x.Attempts >= options.MinimumAttempts))
        {
            if (sample.Failures * 100d / sample.Attempts >= options.ErrorPercent)
                observations.Add(new("errors:" + sample.Provider, "error_spike", sample.Provider, "critical",
                    "Upstream failure rate exceeded the configured threshold in the observation window."));
            if (sample.AverageLatencyMs >= options.AverageLatencyMs)
                observations.Add(new("latency:" + sample.Provider, "high_latency", sample.Provider, "warning",
                    "Average upstream attempt latency exceeded the configured threshold in the observation window."));
        }
        foreach (var provider in await providers.ListAsync(cancellationToken))
        {
            var enabled = provider.Keys.Where(x => x.Enabled).ToArray();
            if (provider.Keys.Count > 0 && (enabled.Length == 0 && !provider.Configured || enabled.Length > 1 && enabled.All(x => x.CooldownUntil is not null)))
                observations.Add(new("pool:" + provider.Name, "pool_exhausted", provider.Name, "critical",
                    "All provider keys are disabled or cooling down."));
        }
        await store.ApplyAsync(observations, now, cancellationToken);
    }
}
