using System.Net.Http.Json;
using LLMProxy.Application;
using LLMProxy.Domain;
using OpenTelemetry;

namespace LLMProxy.Server;

public sealed class AlertWorker(AlertService evaluator, IAlertStore store, AlertOptions options, IHttpClientFactory clients,
    TimeProvider time, ILogger<AlertWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.EvaluationSeconds), time);
        try
        {
            do
            {
                try
                {
                    await evaluator.EvaluateAsync(stoppingToken);
                    if (options.WebhookUrl.Length > 0) await DeliverAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError("Operational alert evaluation failed: {FailureType}.", ex.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task DeliverAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled || options.WebhookUrl.Length == 0) return;
        // Bound each evaluation so an unreachable destination cannot starve incident detection.
        for (var i = 0; i < 10; i++)
        {
            var alert = await store.ClaimDeliveryAsync(time.GetUtcNow(), cancellationToken);
            if (alert is null) break;
            var success = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Post, options.WebhookUrl)
                {
                    Content = JsonContent.Create(new
                    {
                        id = alert.Id,
                        occurrence = alert.Occurrences,
                        kind = alert.Kind,
                        resource = alert.Resource,
                        severity = alert.Severity,
                        message = alert.Message,
                        started_at = alert.StartedAt
                    }, options: LlmJson.Options)
                };
                request.Headers.Add("Idempotency-Key", alert.Id.ToString("N") + ":" + alert.Occurrences);
                if (options.WebhookBearerToken.Length > 0) request.Headers.Authorization = new("Bearer", options.WebhookBearerToken);
                // Webhook URLs can themselves contain tokens. Do not include them in HTTP telemetry.
                using var suppression = SuppressInstrumentationScope.Begin();
                using var response = await clients.CreateClient("llmproxy-alerts").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                success = response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            { /* Only delivery status is persisted. Never log URLs, credentials or response bodies. */ }
            await store.CompleteDeliveryAsync(alert.Id, alert.DeliveryLeaseUntil!.Value, success, time.GetUtcNow(), cancellationToken);
        }
    }
}
