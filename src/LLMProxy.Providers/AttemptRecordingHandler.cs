using LLMProxy.Domain;

namespace LLMProxy.Providers;

public sealed class AttemptRecordingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = AttemptContext.Current;
        if (context is null) return await base.SendAsync(request, cancellationToken);
        var attempt = context.Start();
        if (request.Options.TryGetValue(ProviderKeyPool.KeyIdOption, out var keyId)) attempt.ProviderKeyId = keyId;
        var started = context.Time.GetTimestamp();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            attempt.HttpStatus = (int)response.StatusCode;
            attempt.Status = response.IsSuccessStatusCode ? "response_received" : "http_error";
            attempt.UsageEstimated = response.IsSuccessStatusCode || (int)response.StatusCode >= 500 || (int)response.StatusCode == 408;
            if (response.Headers.TryGetValues("x-request-id", out var values))
            {
                var id = values.FirstOrDefault();
                if (id is { Length: <= 256 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) attempt.ProviderRequestId = id;
            }
            return response;
        }
        catch (OperationCanceledException) { attempt.Status = "interrupted"; throw; }
        catch { attempt.Status = "connection_error"; throw; }
        finally { attempt.LatencyMs = (long)context.Time.GetElapsedTime(started).TotalMilliseconds; }
    }
}
