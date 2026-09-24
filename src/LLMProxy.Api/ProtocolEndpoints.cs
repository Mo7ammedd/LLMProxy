using System.Text.Json.Nodes;
using LLMProxy.Application;
using LLMProxy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LLMProxy.Api;

public static class ProtocolEndpoints
{
    public static void MapProtocols(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/v1").RequireAuthorization();
        api.MapPost("/embeddings", (HttpContext http, ProtocolRequests validator, GatewayService gateway) =>
            HandleAsync(GatewayOperation.Embeddings, http, validator, gateway));
        api.MapPost("/responses", (HttpContext http, ProtocolRequests validator, GatewayService gateway) =>
            HandleAsync(GatewayOperation.Responses, http, validator, gateway));
    }
    private static async Task HandleAsync(GatewayOperation operation, HttpContext http, ProtocolRequests validator, GatewayService gateway)
    {
        var prepared = validator.Validate(operation, await GatewayEndpoints.ReadAsync<JsonObject>(http), http.ApiKey());
        var context = new GatewayRequestContext(http.RequestId(), http.ApiKey(), RequestDeadlineMiddleware.ClientCancellation(http));
        if (!prepared.Admission.Stream)
        {
            var response = await gateway.CompleteProtocolAsync(prepared, context, http.RequestAborted);
            await http.Response.WriteAsJsonAsync(response, LlmJson.Options, http.RequestAborted);
            return;
        }
        await using var stream = gateway.StreamProtocolAsync(prepared, context, http.RequestAborted).GetAsyncEnumerator(http.RequestAborted);
        var available = await stream.MoveNextAsync();
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        long sequence = 0;
        while (available)
        {
            var item = stream.Current;
            sequence = item.Data["sequence_number"] is JsonValue value && value.TryGetValue<long>(out var observed)
                && observed >= 0 && observed < long.MaxValue ? observed + 1 : sequence + 1;
            http.Items[RequestContext.ResponseSequenceItem] = sequence;
            if (item.Event is { } type) await http.Response.WriteAsync($"event: {type}\n", http.RequestAborted);
            await http.Response.WriteAsync($"data: {item.Data.ToJsonString()}\n\n", http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);
            available = await stream.MoveNextAsync();
        }
    }
}
