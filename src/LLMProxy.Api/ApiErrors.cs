using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LLMProxy.Api;

public static class ApiErrors
{
    public static JsonObject OpenAi(GatewayException error) => new()
    {
        ["error"] = new JsonObject
        {
            ["message"] = error.Message,
            ["type"] = error.ErrorType,
            ["param"] = error.Param,
            ["code"] = error.Code
        }
    };

    public static async Task WriteAsync(HttpContext context, GatewayException error)
    {
        if (context.RequestAborted.IsCancellationRequested) return;
        if (context.Response.HasStarted)
        {
            var native = context.Items.TryGetValue(RequestContext.ResponseSequenceItem, out var sequence);
            var payload = native ? new JsonObject
            {
                ["type"] = "error",
                ["code"] = error.Code,
                ["message"] = error.Message,
                ["param"] = error.Param,
                ["sequence_number"] = (long)sequence!
            } : OpenAi(error);
            await context.Response.WriteAsync($"{(native ? "event: error\n" : "")}data: {payload.ToJsonString()}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            return;
        }
        context.Response.StatusCode = error.StatusCode;
        if (error.RetryAfterSeconds is { } retry) context.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (context.Request.Path.StartsWithSegments("/v1"))
            await context.Response.WriteAsJsonAsync(OpenAi(error), cancellationToken: context.RequestAborted);
        else
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = error.StatusCode,
                Title = error.Message,
                Type = "about:blank",
                Extensions = { ["code"] = error.Code, ["request_id"] = context.Items[RequestContext.RequestIdItem]?.ToString() }
            }, options: (JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}

public static class RequestContext
{
    public static object RequestIdItem { get; } = new();
    public static object ApiKeyItem { get; } = new();
    public static object ResponseSequenceItem { get; } = new();
    public static Guid RequestId(this HttpContext context) => (Guid)context.Items[RequestIdItem]!;
    public static ApiKey ApiKey(this HttpContext context) => (ApiKey)context.Items[ApiKeyItem]!;
}

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid();
        context.Items[RequestContext.RequestIdItem] = requestId;
        context.Response.Headers["X-Request-Id"] = requestId.ToString("N");
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.CacheControl = "no-store";
        var correlation = context.Request.Headers["X-Correlation-Id"].ToString();
        if (correlation.Length is 0 or > 64 || !correlation.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            correlation = requestId.ToString("N");
        context.Response.Headers["X-Correlation-Id"] = correlation;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId, ["CorrelationId"] = correlation });
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var error = exception switch
            {
                GatewayException gateway => gateway,
                JsonException => new GatewayException("The request body is not valid JSON.", "invalid_json"),
                BadHttpRequestException bad => new GatewayException("The HTTP request is invalid or too large.", "invalid_request", bad.StatusCode),
                OperationCanceledException => new GatewayException("The request deadline was exceeded.", "request_timeout", 504),
                _ => new GatewayException("The gateway could not complete the request.", "internal_error", 500)
            };
            // Exception messages and stack traces may contain upstream data. Log only the type and safe local code.
            if (error.StatusCode >= 500) logger.LogError("Request failed with {ErrorCode}; failure type {FailureType}", error.Code, exception.GetType().Name);
            try { await ApiErrors.WriteAsync(context, error); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (IOException) { context.Abort(); }
        }
    }
}
