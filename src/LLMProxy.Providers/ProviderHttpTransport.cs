using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace LLMProxy.Providers;

public sealed class ProviderHttpTransport(IHttpClientFactory clients, TimeProvider? time = null,
    IProviderPoolState? poolState = null, string? accountName = null)
{
    private readonly ConditionalWeakTable<ProviderConnectionOptions, ProviderKeyPool> _keyPools = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly IProviderPoolState _poolState = poolState ?? new MemoryProviderPoolState(time ?? TimeProvider.System);

    public async Task<HttpResponseMessage> SendAsync(string provider, Uri endpoint, JsonObject payload,
        IReadOnlyDictionary<string, string> headers, bool streaming, CancellationToken cancellationToken,
        ProviderConnectionOptions? connection = null, string apiKeyHeader = "Authorization", string apiKeyPrefix = "Bearer ")
    {
        // Buffer the bounded JSON payload once, providing Content-Length and a replayable body for retries.
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var pool = connection is null ? null : _keyPools.GetValue(connection, value => new(value, _poolState, accountName ?? provider));
        var keys = pool is null ? null : await pool.NextAsync(cancellationToken);
        if (keys is not { Count: > 0 })
            return await SendAttemptAsync(provider, endpoint, body, headers, streaming, null, false,
                apiKeyHeader, apiKeyPrefix, cancellationToken);

        ProviderException? lastError = null;
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await pool!.IsAvailableAsync(key, cancellationToken)) continue;
            try
            {
                // Only HTTP establishment is retried here. A successful response/stream stays on this key.
                return await SendAttemptAsync(provider, endpoint, body, headers, streaming, key, pool.HasMultipleKeys,
                    apiKeyHeader, apiKeyPrefix, cancellationToken);
            }
            catch (ProviderException ex) when (pool.HasMultipleKeys && ex.IsTransient)
            {
                if (ex.Code is "provider_authentication_failed" or "provider_rate_limited")
                    await pool.CoolDownAsync(key, ex.RetryAfterSeconds, cancellationToken);
                lastError = ex;
            }
        }
        throw lastError ?? await pool!.UnavailableAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAttemptAsync(string provider, Uri endpoint, byte[] body,
        IReadOnlyDictionary<string, string> headers, bool streaming, ProviderKeyPool.Credential? key, bool multipleKeys,
        string apiKeyHeader, string apiKeyPrefix, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (key is not null)
        {
            request.Headers.Remove(apiKeyHeader);
            request.Headers.TryAddWithoutValidation(apiKeyHeader, apiKeyPrefix + key.Value);
            request.Options.Set(ProviderKeyPool.KeyIdOption, key.Id);
        }
        request.Options.Set(ProviderKeyPool.MultipleKeysOption, multipleKeys);
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
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ?? (retryAfter?.Date - _time.GetUtcNow());
        var retryAfterSeconds = delay is { } duration ? (int?)Math.Clamp(Math.Ceiling(duration.TotalSeconds), 1, 86400) : null;
        response.Dispose(); // Do not surface or log upstream response bodies.
        throw status switch
        {
            HttpStatusCode.TooManyRequests => new ProviderException("provider_rate_limited", true, 429) { RetryAfterSeconds = retryAfterSeconds },
            HttpStatusCode.RequestTimeout => new ProviderException("provider_timeout", true, 504),
            >= HttpStatusCode.InternalServerError => new ProviderException("provider_unavailable", true),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new ProviderException("provider_rejected_request", false, 400),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProviderException("provider_authentication_failed", multipleKeys),
            _ => new ProviderException("provider_error", false)
        };
    }
}
