using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Application;
using LLMProxy.Domain;
using LLMProxy.Infrastructure;

namespace LLMProxy.Server;

public sealed class BatchWorker(IBatchStore store, GatewayService gateway, ProtocolRequests protocols,
    GatewayOptions options, StorageOptions storage, TimeProvider time, ILogger<BatchWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Enumerable.Range(0, options.Batches.Workers).Select(_ => WorkAsync(stoppingToken)).Append(MaintainAsync(stoppingToken)));

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        var worker = Guid.NewGuid().ToString("N");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var item = await store.ClaimItemAsync(worker, time.GetUtcNow(), TimeSpan.FromSeconds(options.Requests.TimeoutSeconds + 90), stoppingToken);
                if (item is null) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                else await ProcessAsync(item, worker, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Batch worker unavailable: {FailureType}", ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async Task ProcessAsync(BatchWork work, string worker, CancellationToken cancellationToken)
    {
        JsonNode result;
        var status = 200;
        try
        {
            if (!work.Key.Enabled || work.Key.ExpiresAt <= time.GetUtcNow())
                throw new GatewayException("The gateway key is disabled or expired.", "invalid_api_key", 401);
            var body = JsonNode.Parse(work.Item.Body)!.AsObject();
            var context = new GatewayRequestContext(work.Item.Id, work.Key);
            JsonNode response;
            if (work.Job.Endpoint == "/v1/chat/completions")
                response = JsonSerializer.SerializeToNode(await gateway.CompleteAsync(body.Deserialize<LlmRequest>(LlmJson.Options)!, context, cancellationToken), LlmJson.Options)!;
            else response = await gateway.CompleteProtocolAsync(protocols.Validate(
                work.Job.Endpoint == "/v1/embeddings" ? GatewayOperation.Embeddings : GatewayOperation.Responses, body, work.Key), context, cancellationToken);
            result = new JsonObject
            {
                ["id"] = "batch_req_" + work.Item.Id.ToString("N"),
                ["custom_id"] = work.Item.CustomId,
                ["response"] = new JsonObject { ["status_code"] = 200, ["request_id"] = work.Item.Id.ToString(), ["body"] = response },
                ["error"] = null
            };
        }
        catch (Exception ex)
        {
            status = ex is GatewayException error ? error.StatusCode : ex is OperationCanceledException ? 499 : 500;
            result = new JsonObject
            {
                ["id"] = "batch_req_" + work.Item.Id.ToString("N"),
                ["custom_id"] = work.Item.CustomId,
                ["response"] = null,
                ["error"] = new JsonObject
                {
                    ["code"] = ex is GatewayException failure ? failure.Code : "batch_request_failed",
                    ["message"] = ex is GatewayException known ? known.Message : "The batch request did not complete."
                }
            };
        }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await store.CompleteItemAsync(work.Item.Id, worker, status, result.ToJsonString(), cleanup.Token);
    }

    private async Task MaintainAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        try
        {
            do
            {
                try { await store.MaintainBatchesAsync(time.GetUtcNow(), time.GetUtcNow().AddDays(-storage.BatchRetentionDays), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError("Batch maintenance unavailable: {FailureType}", ex.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
