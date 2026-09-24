using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Application;
using LLMProxy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LLMProxy.Api;

public static class GatewayEndpoints
{
    public static IEndpointRouteBuilder MapLlmProxy(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/v1").RequireAuthorization();
        api.MapGet("/models", async (HttpContext http, IModelRegistry registry, GatewayService gateway, IProviderCatalog catalog) =>
        {
            await gateway.CheckRateLimitAsync(http.ApiKey(), null, http.RequestAborted);
            var available = catalog.Providers.Where(provider => provider.IsConfigured).Select(provider => provider.Name).ToHashSet(StringComparer.Ordinal);
            return Results.Json(new
            {
                @object = "list",
                data = registry.Models.Where(model => http.ApiKey().Allows(model.Name) && model.Targets.Any(target => available.Contains(target.Provider))).Select(model => new
                { id = model.Name, @object = "model", created = 0L, owned_by = "llmproxy" })
            });
        });
        api.MapPost("/chat/completions", async (HttpContext http, GatewayService gateway) =>
        {
            var request = await ReadAsync<LlmRequest>(http);
            var context = new GatewayRequestContext(http.RequestId(), http.ApiKey(), RequestDeadlineMiddleware.ClientCancellation(http));
            if (!request.Stream)
            {
                var response = await gateway.CompleteAsync(request, context, http.RequestAborted);
                await http.Response.WriteAsJsonAsync(response, LlmJson.Options, cancellationToken: http.RequestAborted);
                return;
            }
            await using var stream = gateway.StreamAsync(request, context, http.RequestAborted).GetAsyncEnumerator(http.RequestAborted);
            // Resolve auth, quotas, provider errors and fallback before sending HTTP 200 headers.
            var available = await stream.MoveNextAsync();
            http.Response.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            while (available)
            {
                var payload = StreamChunk(stream.Current, request, context.RequestId, created);
                await http.Response.WriteAsync($"data: {payload.ToJsonString()}\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
                available = await stream.MoveNextAsync();
            }
            await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);
        });
        // Keep unknown /v1 routes in the same error format (including unauthenticated callers).
        endpoints.MapFallback("/v1/{**path}", (HttpContext http) => ApiErrors.WriteAsync(http,
            new GatewayException("This endpoint is not supported.", "not_found", 404)));

        var admin = endpoints.MapGroup("/admin").RequireAuthorization("Admin");
        admin.MapPost("/keys", async (HttpContext http, ApiKeyService keys) =>
        {
            var result = await keys.CreateAsync(await ReadAsync<CreateApiKey>(http), http.RequestAborted);
            return Results.Json(result, LlmJson.Options, statusCode: 201);
        }).RequireAuthorization("AdminWrite");
        admin.MapGet("/keys", async (HttpContext http, IGatewayStore store, int? limit) => Results.Json(
            (await store.ListKeysAsync(limit ?? 100, http.RequestAborted)).Select(ApiKeySummary.From), LlmJson.Options));
        admin.MapPut("/keys/{id:guid}", async (Guid id, HttpContext http, ApiKeyService keys) => Results.Json(
            await keys.UpdateAsync(id, await ReadAsync<UpdateApiKey>(http), http.RequestAborted), LlmJson.Options)).RequireAuthorization("AdminWrite");
        admin.MapGet("/usage", async (HttpContext http, IGatewayStore store, Guid? api_key_id, int? limit) => Results.Json(
            await store.ListUsageAsync(api_key_id, limit ?? 100, http.RequestAborted), LlmJson.Options));

        endpoints.MapHealthChecks("/health", HealthOptions("ready"));
        endpoints.MapHealthChecks("/health/ready", HealthOptions("ready"));
        endpoints.MapHealthChecks("/health/live", HealthOptions("live"));
        endpoints.MapManagement();
        endpoints.MapProtocols();
        endpoints.MapBatches();
        endpoints.MapDashboard();
        return endpoints;
    }

    private static JsonObject StreamChunk(LlmStreamChunk chunk, LlmRequest request, Guid id, long created)
    {
        var choices = new JsonArray();
        if (chunk.Delta is not null || chunk.FinishReason is not null) choices.Add(new JsonObject
        {
            ["index"] = 0,
            ["delta"] = chunk.Delta is null ? new JsonObject() : JsonSerializer.SerializeToNode(chunk.Delta, LlmJson.Options),
            ["finish_reason"] = chunk.FinishReason
        });
        var result = new JsonObject
        {
            ["id"] = $"chatcmpl-{id:N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = request.Model,
            ["choices"] = choices
        };
        if (request.StreamOptions?.IncludeUsage == true) result["usage"] = JsonSerializer.SerializeToNode(chunk.Usage, LlmJson.Options);
        return result;
    }

    internal static async Task<T> ReadAsync<T>(HttpContext http)
    {
        if (!http.Request.HasJsonContentType()) throw new GatewayException("Content-Type must be application/json.", "invalid_content_type", 415);
        return await JsonSerializer.DeserializeAsync<T>(http.Request.Body, LlmJson.Options, http.RequestAborted)
            ?? throw new GatewayException("A JSON request body is required.", "invalid_request");
    }

    private static HealthCheckOptions HealthOptions(string tag) => new()
    {
        Predicate = registration => registration.Tags.Contains(tag),
        ResponseWriter = (http, report) => http.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(entry => entry.Key, entry => new
            { status = entry.Value.Status.ToString(), description = entry.Value.Description })
        }, cancellationToken: http.RequestAborted)
    };
}
