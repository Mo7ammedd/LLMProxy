using System.Globalization;
using System.Security.Claims;
using System.Text;
using LLMProxy.Application;
using LLMProxy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LLMProxy.Api;

public static class ManagementEndpoints
{
    public static void MapManagement(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/admin/auth/login", async (HttpContext http, OperatorService operators, IRateLimiter limiter) =>
        {
            var body = await GatewayEndpoints.ReadAsync<OperatorLogin>(http);
            var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var scopes = new[] { new RateLimitScope("operator-login:ip:" + ApiKeyHasher.Hash(address), 20),
                new RateLimitScope("operator-login:user:" + ApiKeyHasher.Hash(body.Username?.Trim().ToLowerInvariant() ?? ""), 5) };
            var decision = await limiter.AcquireAsync(scopes, http.RequestAborted);
            if (!decision.Allowed) throw new GatewayException("Too many login attempts.", "rate_limit_exceeded", 429)
            { RetryAfterSeconds = decision.RetryAfterSeconds };
            return Results.Json(await operators.LoginAsync(body, http.RequestAborted), LlmJson.Options);
        }).AllowAnonymous();
        var admin = endpoints.MapGroup("/admin").RequireAuthorization("Admin");
        admin.MapPost("/config/reload", async (HttpContext http, IRuntimeConfiguration configuration) =>
            Results.Json(await configuration.ReloadAsync(http.RequestAborted), LlmJson.Options)).RequireAuthorization("AdminOperators");
        admin.MapGet("/auth/me", (HttpContext http) => Results.Json(new
        {
            id = http.User.FindFirstValue(ClaimTypes.NameIdentifier),
            username = http.User.Identity?.Name,
            role = http.User.FindFirstValue(ClaimTypes.Role)
        }));
        admin.MapPost("/auth/logout", async (HttpContext http, IManagementStore store) =>
        {
            var raw = GatewayKeyAuthenticationHandler.ReadBearer(http.Request.Headers.Authorization);
            if (raw is not null) await store.DeleteSessionAsync(ApiKeyHasher.Hash(raw), http.RequestAborted);
            return Results.NoContent();
        });
        admin.MapGet("/operators", async (HttpContext http, IManagementStore store) => Results.Json(
            (await store.ListOperatorsAsync(http.RequestAborted)).Select(OperatorSummary.From), LlmJson.Options))
            .RequireAuthorization("AdminOperators");
        admin.MapPost("/operators", async (HttpContext http, OperatorService operators) => Results.Json(
            await operators.CreateAsync(await GatewayEndpoints.ReadAsync<CreateOperator>(http), http.RequestAborted),
            LlmJson.Options, statusCode: 201)).RequireAuthorization("AdminOperators");
        admin.MapPut("/operators/{id:guid}", async (Guid id, HttpContext http, OperatorService operators) => Results.Json(
            await operators.UpdateAsync(id, await GatewayEndpoints.ReadAsync<UpdateOperator>(http), http.RequestAborted), LlmJson.Options))
            .RequireAuthorization("AdminOperators");
        admin.MapPost("/keys/{id:guid}/rotate", async (Guid id, HttpContext http, ApiKeyService keys) =>
        {
            var body = await GatewayEndpoints.ReadAsync<RotateKey>(http);
            return Results.Json(await keys.RotateAsync(id, body.GraceSeconds, http.RequestAborted), LlmJson.Options);
        }).RequireAuthorization("AdminWrite");
        admin.MapGet("/keys/page", async (HttpContext http, IManagementStore store, string? owner, string? cursor, int? limit) =>
        {
            var page = await store.QueryKeysAsync(owner, cursor, limit ?? 100, http.RequestAborted);
            return Results.Json(new Page<ApiKeySummary>(page.Data.Select(ApiKeySummary.From).ToArray(), page.NextCursor), LlmJson.Options);
        });
        admin.MapGet("/keys/{id:guid}/quotas", async (Guid id, HttpContext http, IManagementStore store) =>
            Results.Json(await store.QuotaWindowsAsync(id, http.RequestAborted), LlmJson.Options));
        admin.MapGet("/usage/page", async ([AsParameters] ReportFilter filter, HttpContext http, IManagementStore store) =>
            Results.Json(await store.QueryUsageAsync(filter.Query(), http.RequestAborted), LlmJson.Options));
        admin.MapGet("/usage/summary", async ([AsParameters] ReportFilter filter, HttpContext http, IManagementStore store) =>
            Results.Json(await store.SummarizeUsageAsync(filter.Query(), http.RequestAborted), LlmJson.Options));
        admin.MapGet("/usage/{id:guid}/attempts", async (Guid id, HttpContext http, IManagementStore store) =>
            Results.Json(await store.ListAttemptsAsync(id, http.RequestAborted), LlmJson.Options));
        admin.MapGet("/usage/export", ExportAsync);
        admin.MapGet("/audit", async (HttpContext http, IManagementStore store, string? cursor, int? limit) =>
            Results.Json(await store.ListAuditAsync(cursor, limit ?? 100, http.RequestAborted), LlmJson.Options));
        admin.MapPost("/billing/reconcile", async (HttpContext http, IManagementStore store, TimeProvider time) =>
        {
            await store.ReconcileAsync(await GatewayEndpoints.ReadAsync<List<ReconciliationInput>>(http),
                http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, time.GetUtcNow(), http.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization("AdminOperators");
        admin.MapGet("/models", (RuntimeCatalog catalog) =>
        {
            var snapshot = catalog.Current;
            return Results.Json(snapshot.Registry.Models.Select(model => new
            {
                model.Name,
                model.Routing,
                model.MaxConcurrentRequests,
                targets = model.Targets.Select(target => new
                {
                    target.Provider,
                    target.Model,
                    capabilities = (snapshot.Providers.Single(p => p.Name == target.Provider).Capabilities.Features
                        & (target.Capabilities?.Features ?? (ModelCapability)(-1))).ToString()
                })
            }), LlmJson.Options);
        });
    }

    private static async Task ExportAsync([AsParameters] ReportFilter filter, HttpContext http, IManagementStore store)
    {
        var query = filter.Query() with { Limit = 1000, Cursor = null };
        var page = await store.QueryUsageAsync(query, http.RequestAborted);
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = "attachment; filename=\"llmproxy-usage.csv\"";
        await http.Response.WriteAsync("request_id,api_key_id,created_at,model,provider,status,input_tokens,output_tokens,cost_usd,latency_ms\n", http.RequestAborted);
        while (true)
        {
            foreach (var row in page.Data)
            {
                var values = new[] { row.RequestId.ToString(), row.ApiKeyId.ToString(), row.CreatedAt.ToString("O"), row.Model,
                    row.Provider, row.Status, row.InputTokens.ToString(CultureInfo.InvariantCulture),
                    row.OutputTokens.ToString(CultureInfo.InvariantCulture), row.EstimatedCost.ToString(CultureInfo.InvariantCulture),
                    row.LatencyMs.ToString(CultureInfo.InvariantCulture) };
                await http.Response.WriteAsync(string.Join(',', values.Select(Csv)) + "\n", http.RequestAborted);
            }
            if (page.NextCursor is null) break;
            page = await store.QueryUsageAsync(query with { Cursor = page.NextCursor }, http.RequestAborted);
        }
    }
    private static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
    private sealed record RotateKey(int GraceSeconds = 0);
}

public sealed class ReportFilter
{
    [FromQuery(Name = "api_key_id")] public Guid? ApiKeyId { get; set; }
    [FromQuery] public string? Owner { get; set; }
    [FromQuery] public DateTimeOffset? From { get; set; }
    [FromQuery] public DateTimeOffset? To { get; set; }
    [FromQuery] public string? Model { get; set; }
    [FromQuery] public string? Provider { get; set; }
    [FromQuery] public string? Status { get; set; }
    [FromQuery] public int? Limit { get; set; }
    [FromQuery] public string? Cursor { get; set; }
    public UsageQuery Query() => new(ApiKeyId, Owner, From, To, Model, Provider, Status, Limit ?? 100, Cursor);
}
