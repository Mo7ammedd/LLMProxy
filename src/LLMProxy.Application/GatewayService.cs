using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using LLMProxy.Domain;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace LLMProxy.Application;

public sealed record GatewayRequestContext(Guid RequestId, ApiKey ApiKey);

public sealed class GatewayService(
    RequestValidator validator, IModelRegistry registry, IRoutePlanner router, IRateLimiter limiter,
    IGatewayStore store, CostCalculator costs, GatewayOptions options, GatewayTelemetry telemetry,
    TimeProvider time, ILogger<GatewayService> logger)
{
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, GatewayRequestContext context, CancellationToken cancellationToken)
    {
        request = validator.Validate(request, context.ApiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Requests.TimeoutSeconds));
        var state = await BeginAsync(request, context, timeout.Token);
        using var activity = StartActivity(state);
        try
        {
            var result = await ExecuteWithFallbackAsync(state, async (route, ct) =>
                await route.Provider.ChatCompletionAsync(request with { Model = route.Target.Model }, ct), timeout.Token);
            state.Usage = result.Usage;
            state.OutputBytes = result.Choices.Sum(c => Encoding.UTF8.GetByteCount(c.Message.Text())
                + Encoding.UTF8.GetByteCount(c.Message.ReasoningContent ?? "")
                + (c.Message.ToolCalls?.Sum(t => Encoding.UTF8.GetByteCount(t.Function.Arguments)) ?? 0));
            state.Success = true;
            return result with
            {
                Id = $"chatcmpl-{context.RequestId:N}",
                Model = request.Model,
                Created = state.StartedAt.ToUnixTimeSeconds(),
                Usage = ActualOrEstimatedUsage(state)
            };
        }
        catch (Exception ex)
        {
            state.ErrorCode = ErrorCode(ex, cancellationToken);
            activity?.SetStatus(ActivityStatusCode.Error, state.ErrorCode);
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new GatewayException("The request deadline was exceeded.", "request_timeout", 504);
            throw;
        }
        finally { await FinishAsync(state, cancellationToken); }
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(LlmRequest request, GatewayRequestContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request = validator.Validate(request, context.ApiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Requests.TimeoutSeconds));
        var state = await BeginAsync(request, context, timeout.Token);
        using var activity = StartActivity(state);
        IAsyncEnumerator<LlmStreamChunk>? stream = null;
        try
        {
            // Fetch one event inside the resilience pipeline. Once any event is emitted, never replay a stream.
            try
            {
                stream = await ExecuteWithFallbackAsync(state, async (route, ct) =>
                {
                    var enumerator = route.Provider.StreamChatCompletionAsync(request with { Model = route.Target.Model }, ct)
                        .GetAsyncEnumerator(ct);
                    try
                    {
                        if (!await enumerator.MoveNextAsync()) throw new ProviderException("empty_stream", true);
                        return enumerator;
                    }
                    catch { await enumerator.DisposeAsync(); throw; }
                }, timeout.Token, streaming: true);
                state.StreamOpened = true;
            }
            catch (Exception ex)
            {
                state.ErrorCode = ErrorCode(ex, cancellationToken);
                if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    throw new GatewayException("The request deadline was exceeded.", "request_timeout", 504);
                throw;
            }

            while (true)
            {
                var chunk = stream.Current;
                if (chunk.FinishReason is not null) state.SawFinish = true;
                if (chunk.Usage is { } usage)
                {
                    state.Usage = usage;
                    state.HasFinalUsage = state.SawFinish;
                }
                state.OutputBytes += Encoding.UTF8.GetByteCount(chunk.Delta?.Content ?? "")
                    + Encoding.UTF8.GetByteCount(chunk.Delta?.ReasoningContent ?? "")
                    + (chunk.Delta?.ToolCalls?.Sum(call => Encoding.UTF8.GetByteCount(call.Function?.Arguments ?? "")) ?? 0);
                if (chunk.Delta is not null || chunk.FinishReason is not null) yield return chunk with { Usage = null };
                bool hasNext;
                try { hasNext = await stream.MoveNextAsync(); }
                catch (Exception ex)
                {
                    state.ErrorCode = ErrorCode(ex, cancellationToken);
                    if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                        throw new GatewayException("The request deadline was exceeded.", "request_timeout", 504);
                    throw;
                }
                if (!hasNext) break;
            }
            state.Success = true;
            // Usage is normalized to exactly one final event for OpenAI SDK compatibility.
            if (request.StreamOptions?.IncludeUsage == true) yield return new LlmStreamChunk(Usage: ActualOrEstimatedUsage(state));
        }
        finally
        {
            try { if (stream is not null) await stream.DisposeAsync(); }
            finally
            {
                if (!state.Success) activity?.SetStatus(ActivityStatusCode.Error, state.ErrorCode ?? "stream_interrupted");
                await FinishAsync(state, cancellationToken);
            }
        }
    }

    public async Task CheckRateLimitAsync(ApiKey key, string? model, CancellationToken cancellationToken)
    {
        var owner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Owner)));
        var scopes = new List<RateLimitScope>
        {
            new($"key:{key.Id:N}", key.RequestsPerMinute),
            new($"user:{owner}", options.RateLimiting.Users.GetValueOrDefault(key.Owner, options.RateLimiting.PerUserRequestsPerMinute))
        };
        if (model is not null) scopes.Add(new RateLimitScope($"model:{model}", registry.Get(model).RequestsPerMinute));
        var decision = await limiter.AcquireAsync(scopes, cancellationToken);
        if (!decision.Allowed)
            throw new GatewayException("Rate limit exceeded. Retry after the indicated delay.", "rate_limit_exceeded", 429)
            { RetryAfterSeconds = decision.RetryAfterSeconds };
    }

    private async Task<Execution> BeginAsync(LlmRequest request, GatewayRequestContext context, CancellationToken cancellationToken)
    {
        await CheckRateLimitAsync(context.ApiKey, request.Model, cancellationToken);
        var routes = await router.PlanAsync(request.Model, cancellationToken);
        var input = RequestValidator.EstimateInputTokens(request);
        var reserveUsage = TokenUsage.From(input, request.OutputTokenLimit);
        var maxCost = routes.Max(route => costs.Calculate(route.Target, reserveUsage));
        var now = time.GetUtcNow();
        var reservation = new QuotaReservation
        {
            RequestId = context.RequestId,
            ApiKeyId = context.ApiKey.Id,
            Model = request.Model,
            Tokens = reserveUsage.TotalTokens,
            CostUnits = Money.ToUnits(maxCost),
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(options.Requests.ReservationTtlSeconds)
        };
        if (!await store.TryReserveAsync(reservation, cancellationToken))
            throw new GatewayException("The API key is disabled or its token/spending allowance is insufficient for this request.", "insufficient_quota", 429);
        return new Execution(context, request, routes, reservation, input, now, time.GetTimestamp());
    }

    private async Task<T> ExecuteWithFallbackAsync<T>(Execution state, Func<ProviderRoute, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken, bool streaming = false)
    {
        var builder = new ResiliencePipelineBuilder<T>();
        if (state.Routes.Count > 1)
        {
            builder.AddRetry(new RetryStrategyOptions<T>
            {
                MaxRetryAttempts = state.Routes.Count - 1,
                Delay = TimeSpan.Zero,
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is ProviderException { IsTransient: true }),
                OnRetry = _ => { telemetry.Fallback(state.Request.Model); return ValueTask.CompletedTask; }
            });
        }
        var index = 0;
        return await builder.Build().ExecuteAsync(async ct =>
        {
            var route = state.Routes[index++];
            state.Route = route;
            using var span = telemetry.ActivitySource.StartActivity("provider.chat", ActivityKind.Client);
            span?.SetTag("gen_ai.provider.name", route.Provider.Name);
            span?.SetTag("gen_ai.request.model", route.Target.Model);
            var started = time.GetTimestamp();
            state.ProviderStarted = started;
            var success = false;
            try { var result = await action(route, ct); success = true; return result; }
            catch { span?.SetStatus(ActivityStatusCode.Error); throw; }
            finally
            {
                if (!streaming || !success) telemetry.ProviderFinished(route.Provider.Name, time.GetElapsedTime(started).TotalSeconds, success);
                else telemetry.ProviderFirstToken(route.Provider.Name, time.GetElapsedTime(started).TotalSeconds);
            }
        }, cancellationToken);
    }

    private Activity? StartActivity(Execution state)
    {
        var activity = telemetry.ActivitySource.StartActivity("gateway.chat");
        activity?.SetTag("llmproxy.request.id", state.Context.RequestId);
        activity?.SetTag("gen_ai.request.model", state.Request.Model);
        return activity;
    }

    private static TokenUsage ActualOrEstimatedUsage(Execution state)
    {
        // A disconnected/unfinished stream can still be billed upstream. Charge its reservation ceiling
        // until final usage is known, so cancellation cannot be used to bypass lifetime allowances.
        var unknownStream = state.StreamOpened && !state.HasFinalUsage;
        var unknownCancellation = state.Route is not null && state.ErrorCode is "request_cancelled" or "request_timeout" or "provider_timeout";
        if (!state.Success && (unknownStream || unknownCancellation))
            return TokenUsage.From(state.InputEstimate, state.Request.OutputTokenLimit);
        if (state.Usage is { TotalTokens: > 0 } usage) return usage;
        if (!state.Success && state.OutputBytes == 0) return TokenUsage.Zero;
        return TokenUsage.From(state.InputEstimate, Math.Min(state.OutputBytes, state.Request.OutputTokenLimit));
    }

    private async Task FinishAsync(Execution state, CancellationToken requestCancellation)
    {
        if (state.StreamOpened && state.Route is { } completedRoute)
            telemetry.ProviderFinished(completedRoute.Provider.Name, time.GetElapsedTime(state.ProviderStarted).TotalSeconds, state.Success);
        var usage = ActualOrEstimatedUsage(state);
        var record = new UsageRecord
        {
            RequestId = state.Context.RequestId,
            ApiKeyId = state.Context.ApiKey.Id,
            Model = state.Request.Model,
            Provider = state.Route?.Provider.Name ?? "",
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            TotalTokens = usage.TotalTokens,
            LatencyMs = (long)time.GetElapsedTime(state.Started).TotalMilliseconds,
            Status = state.Success ? "success" : requestCancellation.IsCancellationRequested ? "cancelled" : "error",
            ErrorCode = state.Success ? null : state.ErrorCode ?? (requestCancellation.IsCancellationRequested ? "request_cancelled" : "stream_interrupted"),
            EstimatedCost = state.Route is { } route ? costs.Calculate(route.Target, usage) : 0,
            UsageEstimated = state.Usage is not { TotalTokens: > 0 } || state.StreamOpened && !state.Success && !state.HasFinalUsage,
            CreatedAt = state.StartedAt
        };
        // Client disconnects do not cancel bookkeeping. A durable reservation protects allowances if this fails.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await store.CompleteAsync(record, cleanup.Token); }
        catch (Exception ex)
        {
            logger.LogCritical("Usage persistence failed for {RequestId}; reservation retained. Failure type: {FailureType}", record.RequestId, ex.GetType().Name);
            if (!requestCancellation.IsCancellationRequested)
                throw new GatewayException("Usage storage is temporarily unavailable.", "storage_unavailable", 503);
        }
        telemetry.Record(record);
        logger.LogInformation("Request {RequestId} model {Model} provider {Provider} status {Status} tokens {Tokens} latency_ms {LatencyMs}",
            record.RequestId, record.Model, record.Provider, record.Status, record.TotalTokens, record.LatencyMs);
    }

    private static string ErrorCode(Exception exception, CancellationToken requestCancellation) => exception switch
    {
        GatewayException gateway => gateway.Code,
        OperationCanceledException => requestCancellation.IsCancellationRequested ? "request_cancelled" : "request_timeout",
        _ => "internal_error"
    };

    private sealed class Execution(GatewayRequestContext context, LlmRequest request, IReadOnlyList<ProviderRoute> routes,
        QuotaReservation reservation, long inputEstimate, DateTimeOffset startedAt, long started)
    {
        public GatewayRequestContext Context { get; } = context;
        public LlmRequest Request { get; } = request;
        public IReadOnlyList<ProviderRoute> Routes { get; } = routes;
        public QuotaReservation Reservation { get; } = reservation;
        public long InputEstimate { get; } = inputEstimate;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public long Started { get; } = started;
        public ProviderRoute? Route { get; set; }
        public long ProviderStarted { get; set; }
        public bool StreamOpened { get; set; }
        public bool SawFinish { get; set; }
        public bool HasFinalUsage { get; set; }
        public TokenUsage? Usage { get; set; }
        public long OutputBytes { get; set; }
        public bool Success { get; set; }
        public string? ErrorCode { get; set; }
    }
}
