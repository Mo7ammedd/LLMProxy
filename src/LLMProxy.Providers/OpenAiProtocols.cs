using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public abstract partial class OpenAiCompatibleProvider : IProtocolProvider
{
    protected virtual Uri ProtocolEndpoint(GatewayOperation operation, JsonObject payload)
    {
        var endpoint = new UriBuilder(Endpoint(new LlmRequest { Model = payload["model"]!.GetValue<string>() }));
        const string chat = "chat/completions";
        if (!endpoint.Path.EndsWith(chat, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid provider endpoint.");
        endpoint.Path = endpoint.Path[..^chat.Length] + (operation == GatewayOperation.Embeddings ? "embeddings" : "responses");
        return endpoint.Uri;
    }

    public async Task<JsonObject> CompleteProtocolAsync(GatewayOperation operation, JsonObject payload, CancellationToken cancellationToken)
    {
        using var response = await transport.SendAsync(Name, ProtocolEndpoint(operation, payload), payload,
            await HeadersAsync(cancellationToken), false, cancellationToken, KeyConnection, ApiKeyHeader, ApiKeyPrefix);
        using var json = await ProviderJson.ReadAsync(response, cancellationToken);
        var root = JsonNode.Parse(json.RootElement.GetRawText()) as JsonObject
            ?? throw new ProviderException("invalid_provider_response", false);
        if (operation == GatewayOperation.Embeddings && root["data"] is not JsonArray)
            throw new ProviderException("invalid_provider_response", false);
        if (operation == GatewayOperation.Responses)
        {
            if (root["status"]?.GetValue<string>() == "failed") throw new ProviderException("provider_response_failed", false);
            if (root["object"]?.GetValue<string>() != "response" || root["output"] is not JsonArray
                || root["status"]?.GetValue<string>() is not ("completed" or "incomplete"))
                throw new ProviderException("invalid_provider_response", false);
        }
        return root;
    }

    public async IAsyncEnumerable<ProtocolEvent> StreamProtocolAsync(GatewayOperation operation, JsonObject payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await transport.SendAsync(Name, ProtocolEndpoint(operation, payload), payload,
            await HeadersAsync(cancellationToken), true, cancellationToken, KeyConnection, ApiKeyHeader, ApiKeyPrefix);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var terminal = false;
        await foreach (var item in SseReader.ReadAsync(body, cancellationToken))
        {
            using var json = ProviderJson.Parse(item.Data);
            var data = JsonNode.Parse(json.RootElement.GetRawText()) as JsonObject
                ?? throw new ProviderException("invalid_provider_response", false);
            var type = data["type"]?.GetValue<string>() ?? item.Event;
            if (type is null || type.Length > 128 || !type.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                throw new ProviderException("invalid_provider_event", false);
            if (type is "error" or "response.failed") throw new ProviderException("provider_stream_error", false);
            terminal |= type is "response.completed" or "response.incomplete";
            yield return new ProtocolEvent(type, data);
            if (terminal) yield break;
        }
        if (!terminal) throw new ProviderException("incomplete_provider_stream", false);
    }
}
