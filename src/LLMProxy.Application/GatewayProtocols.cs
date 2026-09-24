using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Application;

public sealed partial class GatewayService
{
    public async Task<JsonObject> CompleteProtocolAsync(PreparedProtocol prepared, GatewayRequestContext context, CancellationToken cancellationToken)
    {
        var request = prepared.Admission;
        var clientCancellation = context.ClientCancellation ?? cancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Requests.TimeoutSeconds));
        var state = await BeginAsync(request, context, timeout.Token);
        using var activity = StartActivity(state);
        try
        {
            var result = await ExecuteWithFallbackAsync(state, async (route, ct) =>
            {
                var payload = (JsonObject)prepared.Payload.DeepClone();
                payload["model"] = route.Target.Model;
                return await ((IProtocolProvider)route.Provider).CompleteProtocolAsync(request.Operation, payload, ct);
            }, timeout.Token);
            state.Usage = ReadProtocolUsage(result);
            state.OutputBytes = request.Operation == GatewayOperation.Embeddings ? 0 : Encoding.UTF8.GetByteCount(result["output"]?.ToJsonString() ?? "");
            state.Success = true;
            result["model"] = request.Model;
            if (request.Operation == GatewayOperation.Embeddings)
            {
                var usage = ActualOrEstimatedUsage(state);
                result["usage"] = new JsonObject { ["prompt_tokens"] = usage.InputTokens, ["total_tokens"] = usage.InputTokens };
            }
            return result;
        }
        catch (Exception ex)
        {
            state.ErrorCode = ErrorCode(ex, clientCancellation);
            if (ex is OperationCanceledException && !clientCancellation.IsCancellationRequested)
                throw new GatewayException("The request deadline was exceeded.", "request_timeout", 504);
            throw;
        }
        finally { await FinishAsync(state, clientCancellation); }
    }

    public async IAsyncEnumerable<ProtocolEvent> StreamProtocolAsync(PreparedProtocol prepared, GatewayRequestContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = prepared.Admission;
        var clientCancellation = context.ClientCancellation ?? cancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Requests.TimeoutSeconds));
        var state = await BeginAsync(request, context, timeout.Token);
        using var activity = StartActivity(state);
        IAsyncEnumerator<ProtocolEvent>? stream = null;
        try
        {
            try
            {
                stream = await ExecuteWithFallbackAsync(state, async (route, ct) =>
                {
                    var payload = (JsonObject)prepared.Payload.DeepClone();
                    payload["model"] = route.Target.Model;
                    var enumerator = ((IProtocolProvider)route.Provider).StreamProtocolAsync(request.Operation, payload, ct).GetAsyncEnumerator(ct);
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
                state.ErrorCode = ErrorCode(ex, clientCancellation);
                if (ex is OperationCanceledException && !clientCancellation.IsCancellationRequested)
                    throw new GatewayException("The request deadline was exceeded.", "request_timeout", 504);
                throw;
            }
            while (true)
            {
                var item = stream.Current;
                if (item.Data["response"] is JsonObject response)
                {
                    response["model"] = request.Model;
                    if (ReadProtocolUsage(response) is { } usage)
                    {
                        state.Usage = usage;
                        state.HasFinalUsage = item.Event is "response.completed" or "response.incomplete";
                    }
                }
                if (item.Event?.EndsWith(".delta", StringComparison.Ordinal) == true
                    && item.Data["delta"] is JsonValue value && value.TryGetValue<string>(out var delta))
                    state.OutputBytes += Encoding.UTF8.GetByteCount(delta);
                yield return item;
                bool next;
                try { next = await stream.MoveNextAsync(); }
                catch (Exception ex)
                {
                    state.ErrorCode = ErrorCode(ex, clientCancellation);
                    if (ex is OperationCanceledException && !clientCancellation.IsCancellationRequested)
                        throw new GatewayException("The request deadline was exceeded.", "request_timeout", 504);
                    throw;
                }
                if (!next) break;
            }
            state.Success = true;
        }
        finally
        {
            try { if (stream is not null) await stream.DisposeAsync(); }
            finally
            {
                if (!state.Success && timeout.IsCancellationRequested)
                    state.ErrorCode ??= clientCancellation.IsCancellationRequested ? "request_cancelled" : "request_timeout";
                await FinishAsync(state, clientCancellation);
            }
        }
    }

    private static TokenUsage? ReadProtocolUsage(JsonObject response)
    {
        if (response["usage"] is not JsonObject usage) return null;
        static long Read(JsonObject value, string name) => value[name] is JsonValue number
            && number.TryGetValue<long>(out var result) && result >= 0 ? result : 0;
        try
        {
            var input = checked(Read(usage, "input_tokens") + Read(usage, "prompt_tokens"));
            var output = Read(usage, "output_tokens");
            return TokenUsage.From(input, output) with
            {
                PromptTokensDetails = usage["input_tokens_details"] is JsonObject details
                ? new(Read(details, "cached_tokens")) : null
            };
        }
        catch (OverflowException) { throw new ProviderException("invalid_provider_usage", false); }
    }
}
