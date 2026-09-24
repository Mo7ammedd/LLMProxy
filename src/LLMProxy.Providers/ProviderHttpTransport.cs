using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace LLMProxy.Providers;

public sealed class ProviderHttpTransport(IHttpClientFactory clients)
{
    public async Task<HttpResponseMessage> SendAsync(string provider, Uri endpoint, JsonObject payload,
        IReadOnlyDictionary<string, string> headers, bool streaming, CancellationToken cancellationToken)
    {
        // Buffer the bounded JSON payload once, providing Content-Length and a replayable body for retries.
        using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Headers.Accept.ParseAdd(streaming ? "text/event-stream" : "application/json");
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient("llmproxy." + provider).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TimeoutRejectedException) { throw new ProviderException("provider_timeout", true, 504); }
        catch (BrokenCircuitException) { throw new ProviderException("provider_circuit_open", true, 503); }
        catch (HttpRequestException) { throw new ProviderException("provider_connection_error", true); }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose(); // Do not surface or log upstream response bodies.
        throw status switch
        {
            HttpStatusCode.TooManyRequests => new ProviderException("provider_rate_limited", true, 429),
            HttpStatusCode.RequestTimeout => new ProviderException("provider_timeout", true, 504),
            >= HttpStatusCode.InternalServerError => new ProviderException("provider_unavailable", true),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new ProviderException("provider_rejected_request", false, 400),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProviderException("provider_authentication_failed", false),
            _ => new ProviderException("provider_error", false)
        };
    }
}
