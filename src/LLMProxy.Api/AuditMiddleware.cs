using System.Security.Claims;
using System.Text.Json;
using LLMProxy.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace LLMProxy.Api;

public sealed class AuditMiddleware(RequestDelegate next, ILogger<AuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IManagementStore store, TimeProvider time)
    {
        var management = context.Request.Path.StartsWithSegments("/admin");
        var mutation = management && context.Request.Method is not ("GET" or "HEAD" or "OPTIONS");
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        if (management)
        {
            var authentication = await context.AuthenticateAsync(AdminKeyAuthenticationHandler.SchemeName);
            actor = authentication.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        }
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? (management ? "/admin/*" : "/v1/*");
        var resource = route.Length > 256 ? route[..256] : route;
        var requestId = context.RequestId();
        if (mutation && actor != "anonymous")
        {
            try
            {
                await store.AppendAuditAsync(new AuditRecord
                {
                    Actor = actor,
                    Action = context.Request.Method + ".started",
                    Resource = resource,
                    CreatedAt = time.GetUtcNow(),
                    RequestId = requestId
                }, context.RequestAborted);
            }
            catch (Exception)
            { throw new GatewayException("Management audit storage is unavailable.", "audit_unavailable", 503); }
        }
        int? failure = null;
        try { await next(context); }
        catch (Exception ex)
        {
            failure = ex switch
            {
                GatewayException gateway => gateway.StatusCode,
                JsonException => 400,
                BadHttpRequestException bad => bad.StatusCode,
                OperationCanceledException when !RequestDeadlineMiddleware.ClientCancellation(context).IsCancellationRequested => 504,
                OperationCanceledException => 499,
                _ => 500
            };
            throw;
        }
        finally
        {
            var status = failure ?? context.Response.StatusCode;
            if (mutation || status >= 400 && (management || context.Request.Path.StartsWithSegments("/v1")))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await store.AppendAuditAsync(new AuditRecord
                    {
                        Actor = actor,
                        Action = context.Request.Method,
                        Resource = resource,
                        StatusCode = status,
                        CreatedAt = time.GetUtcNow(),
                        RequestId = requestId
                    }, cleanup.Token);
                }
                catch (Exception ex)
                { logger.LogError("Audit persistence failed for {RequestId}: {FailureType}", requestId, ex.GetType().Name); }
            }
        }
    }
}
