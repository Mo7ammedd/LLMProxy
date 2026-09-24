namespace LLMProxy.Domain;

public sealed class GatewayFile
{
    public string Id { get; set; } = "file-" + Guid.NewGuid().ToString("N");
    public Guid ApiKeyId { get; set; }
    public string Filename { get; set; } = "";
    public string Purpose { get; set; } = "batch";
    public string? Content { get; set; }
    public string? BatchId { get; set; }
    public bool ErrorsOnly { get; set; }
    public long Bytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BatchJob
{
    public string Id { get; set; } = "batch_" + Guid.NewGuid().ToString("N");
    public Guid ApiKeyId { get; set; }
    public string InputFileId { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Status { get; set; } = "in_progress";
    public string MetadataJson { get; set; } = "{}";
    public string OutputFileId { get; set; } = "file-" + Guid.NewGuid().ToString("N");
    public string ErrorFileId { get; set; } = "file-" + Guid.NewGuid().ToString("N");
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class BatchItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string BatchId { get; set; } = "";
    public int Index { get; set; }
    public string CustomId { get; set; } = "";
    public string Body { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int? HttpStatus { get; set; }
    public string? ResultJson { get; set; }
}

public sealed record BatchWork(BatchItem Item, BatchJob Job, ApiKey Key);

public interface IBatchStore
{
    Task CreateFileAsync(GatewayFile file, CancellationToken cancellationToken);
    Task<GatewayFile?> GetFileAsync(Guid keyId, string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<GatewayFile>> ListFilesAsync(Guid keyId, string? after, int limit, CancellationToken cancellationToken);
    Task<bool> DeleteFileAsync(Guid keyId, string id, CancellationToken cancellationToken);
    IAsyncEnumerable<string> ReadOutputAsync(string batchId, bool errorsOnly, CancellationToken cancellationToken);
    Task CreateBatchAsync(BatchJob job, IReadOnlyList<BatchItem> items, CancellationToken cancellationToken);
    Task<BatchJob?> GetBatchAsync(Guid keyId, string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<BatchJob>> ListBatchesAsync(Guid keyId, string? after, int limit, CancellationToken cancellationToken);
    Task<BatchJob?> CancelBatchAsync(Guid keyId, string id, CancellationToken cancellationToken);
    Task<BatchWork?> ClaimItemAsync(string worker, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);
    Task CompleteItemAsync(Guid itemId, string worker, int status, string resultJson, CancellationToken cancellationToken);
    Task MaintainBatchesAsync(DateTimeOffset now, DateTimeOffset retainAfter, CancellationToken cancellationToken);
}
