using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

internal static class ProviderJson
{
    public static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var bounded = new BoundedReadStream(body, 8 * 1024 * 1024);
            return await JsonDocument.ParseAsync(bounded, new JsonDocumentOptions { MaxDepth = 48 }, cancellationToken);
        }
        catch (JsonException) { throw new ProviderException("invalid_provider_response", false); }
        catch (IOException) { throw new ProviderException("provider_connection_error", true); }
    }

    public static JsonDocument Parse(string data)
    {
        try { return JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 48 }); }
        catch (JsonException) { throw new ProviderException("invalid_provider_response", false); }
    }

    public static JsonNode? Node(JsonElement? value) => value is null ? null : JsonNode.Parse(value.Value.GetRawText());
    public static string? Text(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    public static long Number(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var number) && number >= 0 ? number : 0;
    public static JsonElement Object(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) ? property : default;
    public static IEnumerable<JsonElement> Array(this JsonElement value, string name) => value.Object(name) is { ValueKind: JsonValueKind.Array } array
        ? array.EnumerateArray() : [];

    public static TokenUsage OpenAiUsage(JsonElement root)
    {
        var usage = root.Object("usage");
        return TokenUsage.From(usage.Number("prompt_tokens"), usage.Number("completion_tokens"))
            with
        {
            PromptTokensDetails = usage.Object("prompt_tokens_details").ValueKind == JsonValueKind.Object
                ? new(usage.Object("prompt_tokens_details").Number("cached_tokens")) : null
        };
    }

    public static void RejectUnsupported(LlmRequest request, bool supportsJson)
    {
        if (request.Seed is not null) Unsupported("seed");
        if (request.ResponseFormat is { ValueKind: not JsonValueKind.Null } && !supportsJson) Unsupported("response_format");
        if (request.Tools?.Any(x => x.Function.Strict == true) == true) Unsupported("tools.function.strict");
    }

    public static void Unsupported(string param) => throw new GatewayException(
        "The selected provider does not support this parameter.", "unsupported_parameter", param: param);

    private sealed class BoundedReadStream(Stream inner, long maximum) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private int Count(int count)
        {
            _read += count;
            if (_read > maximum) throw new ProviderException("provider_response_too_large", false);
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken));
    }
}
