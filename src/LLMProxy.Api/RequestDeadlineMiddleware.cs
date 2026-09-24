using LLMProxy.Application;
using LLMProxy.Domain;
using Microsoft.AspNetCore.Http;

namespace LLMProxy.Api;

public sealed class RequestDeadlineMiddleware(RequestDelegate next)
{
    internal static readonly object DeadlineItem = new();
    public async Task InvokeAsync(HttpContext context, GatewayOptions options)
    {
        var path = context.Request.Path.Value?.TrimEnd('/');
        if (!HttpMethods.IsPost(context.Request.Method) || !new[] { "/v1/chat/completions", "/v1/responses", "/v1/embeddings" }
            .Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        var clientCancellation = context.RequestAborted;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(clientCancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Requests.TimeoutSeconds));
        context.Items[DeadlineItem] = clientCancellation;
        context.RequestAborted = deadline.Token;
        try { await next(context); }
        catch (OperationCanceledException) when (!clientCancellation.IsCancellationRequested)
        {
            throw new GatewayException("The request deadline was exceeded.", "request_timeout", 504);
        }
        finally
        {
            context.RequestAborted = clientCancellation;
            context.Items.Remove(DeadlineItem);
        }
    }

    internal static CancellationToken ClientCancellation(HttpContext context) =>
        context.Items.TryGetValue(DeadlineItem, out var value) && value is CancellationToken token ? token : context.RequestAborted;
}
