using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Application;

public sealed record CreateBatch(string InputFileId, string Endpoint, string CompletionWindow, JsonObject? Metadata = null);

public sealed class BatchService(IBatchStore store, GatewayOptions options, RequestValidator chat,
    ProtocolRequests protocols, TimeProvider time)
{
    public async Task<GatewayFile> UploadAsync(ApiKey key, string filename, string content, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes is < 1 || bytes > options.Batches.MaxFileBytes) throw new GatewayException("Batch file exceeds the configured size limit.", "invalid_file_size", 413);
        filename = Path.GetFileName(filename.Replace('\\', '/'));
        if (filename.Length is < 1 or > 128 || filename.Any(char.IsControl)) throw new GatewayException("Invalid filename.", "invalid_filename");
        var file = new GatewayFile { ApiKeyId = key.Id, Filename = filename, Content = content, Bytes = bytes, CreatedAt = time.GetUtcNow() };
        await store.CreateFileAsync(file, cancellationToken);
        return file;
    }

    public async Task<BatchJob> CreateAsync(ApiKey key, CreateBatch command, CancellationToken cancellationToken)
    {
        if (command.CompletionWindow != "24h") throw new GatewayException("completion_window must be 24h.", "invalid_completion_window");
        if (command.Endpoint is not ("/v1/chat/completions" or "/v1/embeddings" or "/v1/responses"))
            throw new GatewayException("Unsupported batch endpoint.", "invalid_endpoint");
        if (command.Metadata is { } metadata && (metadata.Count > 16 || metadata.Any(pair => pair.Key.Length > 64
            || pair.Value is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length > 512)))
            throw new GatewayException("Metadata allows up to 16 string pairs.", "invalid_metadata");
        var file = await store.GetFileAsync(key.Id, command.InputFileId, cancellationToken)
            ?? throw new GatewayException("Input file not found.", "file_not_found", 404);
        if (file.Purpose != "batch" || file.Content is null) throw new GatewayException("Use an uploaded batch input file.", "invalid_input_file");
        var now = time.GetUtcNow();
        var job = new BatchJob
        {
            ApiKeyId = key.Id,
            InputFileId = file.Id,
            Endpoint = command.Endpoint,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
            MetadataJson = command.Metadata?.ToJsonString() ?? "{}"
        };
        var items = new List<BatchItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(file.Content);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (items.Count >= options.Batches.MaxRequests) throw new GatewayException("Too many requests in this batch.", "batch_too_large");
            var row = JsonNode.Parse(line) as JsonObject ?? throw new GatewayException("Every line must be a JSON object.", "invalid_batch");
            var customId = ProtocolRequests.String(row, "custom_id");
            if (customId is not { Length: > 0 and <= 128 } || !ids.Add(customId)
                || ProtocolRequests.String(row, "method") != "POST" || ProtocolRequests.String(row, "url") != command.Endpoint
                || row["body"] is not JsonObject body) throw new GatewayException("Batch lines require unique custom_id, POST, matching url and a body.", "invalid_batch");
            if (Encoding.UTF8.GetByteCount(body.ToJsonString()) > options.Requests.MaxBodyBytes)
                throw new GatewayException("A batch request exceeds the request body limit.", "request_too_large", 413);
            if (ProtocolRequests.Boolean(body, "stream") == true) throw new GatewayException("Batch requests cannot stream.", "invalid_batch_stream");
            if (command.Endpoint == "/v1/chat/completions")
                chat.Validate(body.Deserialize<LlmRequest>(LlmJson.Options) ?? throw new GatewayException("Invalid chat request.", "invalid_batch"), key);
            else protocols.Validate(command.Endpoint == "/v1/embeddings" ? GatewayOperation.Embeddings : GatewayOperation.Responses, body, key);
            items.Add(new BatchItem { BatchId = job.Id, Index = items.Count, CustomId = customId, Body = body.ToJsonString() });
        }
        if (items.Count == 0) throw new GatewayException("The input file contains no requests.", "empty_batch");
        job.Total = items.Count;
        await store.CreateBatchAsync(job, items, cancellationToken);
        return job;
    }
}
