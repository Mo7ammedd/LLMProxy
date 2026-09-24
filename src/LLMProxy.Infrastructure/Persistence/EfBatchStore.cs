using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLMProxy.Domain;
using Microsoft.EntityFrameworkCore;

namespace LLMProxy.Infrastructure.Persistence;

public sealed partial class EfGatewayStore
{
    public async Task CreateFileAsync(GatewayFile file, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Files.Add(file);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<GatewayFile?> GetFileAsync(Guid keyId, string id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Files.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ApiKeyId == keyId, cancellationToken);
    }

    public async Task<IReadOnlyList<GatewayFile>> ListFilesAsync(Guid keyId, string? after, int limit, CancellationToken cancellationToken)
    {
        CheckLimit(limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Files.AsNoTracking().Where(x => x.ApiKeyId == keyId);
        if (after is not null)
        {
            var cursor = await query.SingleOrDefaultAsync(x => x.Id == after, cancellationToken)
                ?? throw new GatewayException("Invalid file cursor.", "invalid_cursor");
            query = query.Where(x => x.CreatedAt < cursor.CreatedAt || x.CreatedAt == cursor.CreatedAt && string.Compare(x.Id, cursor.Id) < 0);
        }
        return await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public Task<bool> DeleteFileAsync(Guid keyId, string id, CancellationToken cancellationToken) => TransactionAsync(async db =>
    {
        if (!await db.Files.AnyAsync(x => x.Id == id && x.ApiKeyId == keyId, cancellationToken)) return false;
        if (await db.Batches.AnyAsync(x => x.InputFileId == id && (x.Status == "in_progress" || x.Status == "cancelling"), cancellationToken))
            throw new GatewayException("An active batch uses this file.", "file_in_use", 409);
        return await db.Files.Where(x => x.Id == id && x.ApiKeyId == keyId).ExecuteDeleteAsync(cancellationToken) > 0;
    }, cancellationToken);

    public async IAsyncEnumerable<string> ReadOutputAsync(string batchId, bool errorsOnly, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var status = errorsOnly ? "failed" : "completed";
        await foreach (var row in db.BatchItems.AsNoTracking().Where(x => x.BatchId == batchId && x.Status == status && x.ResultJson != null)
            .OrderBy(x => x.Index).Select(x => x.ResultJson!).AsAsyncEnumerable().WithCancellation(cancellationToken))
            yield return row;
    }

    public Task CreateBatchAsync(BatchJob job, IReadOnlyList<BatchItem> items, CancellationToken cancellationToken) => TransactionAsync(async db =>
    {
        if (!await db.Files.AnyAsync(x => x.Id == job.InputFileId && x.ApiKeyId == job.ApiKeyId, cancellationToken))
            throw new GatewayException("Input file not found.", "file_not_found", 404);
        db.Batches.Add(job);
        db.BatchItems.AddRange(items);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }, cancellationToken);

    public async Task<BatchJob?> GetBatchAsync(Guid keyId, string id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Batches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ApiKeyId == keyId, cancellationToken);
    }

    public async Task<IReadOnlyList<BatchJob>> ListBatchesAsync(Guid keyId, string? after, int limit, CancellationToken cancellationToken)
    {
        CheckLimit(limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Batches.AsNoTracking().Where(x => x.ApiKeyId == keyId);
        if (after is not null)
        {
            var cursor = await query.SingleOrDefaultAsync(x => x.Id == after, cancellationToken)
                ?? throw new GatewayException("Invalid batch cursor.", "invalid_cursor");
            query = query.Where(x => x.CreatedAt < cursor.CreatedAt || x.CreatedAt == cursor.CreatedAt && string.Compare(x.Id, cursor.Id) < 0);
        }
        return await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public Task<BatchJob?> CancelBatchAsync(Guid keyId, string id, CancellationToken cancellationToken) => TransactionAsync(async db =>
    {
        await db.Batches.Where(x => x.Id == id && x.ApiKeyId == keyId && x.Status == "in_progress")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "cancelling"), cancellationToken);
        var job = await db.Batches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ApiKeyId == keyId, cancellationToken);
        if (job?.Status == "cancelling")
            await db.BatchItems.Where(x => x.BatchId == id && x.Status == "pending")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "cancelled"), cancellationToken);
        return job;
    }, cancellationToken);

    public async Task<BatchWork?> ClaimItemAsync(string worker, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var expires = now.Add(lease);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = await (from item in db.BatchItems
                                   join job in db.Batches on item.BatchId equals job.Id
                                   where item.Status == "pending" && job.Status == "in_progress" && job.ExpiresAt > now
                                   orderby job.CreatedAt, item.Index
                                   select item).AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            if (candidate is null) return null;
            var claimed = await db.BatchItems.Where(x => x.Id == candidate.Id && x.Status == "pending"
                && db.Batches.Any(job => job.Id == x.BatchId && job.Status == "in_progress" && job.ExpiresAt > now))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "running").SetProperty(x => x.LeaseOwner, worker)
                    .SetProperty(x => x.LeaseExpiresAt, expires), cancellationToken);
            if (claimed == 0) continue;
            var batch = await db.Batches.AsNoTracking().SingleAsync(x => x.Id == candidate.BatchId, cancellationToken);
            var key = await db.ApiKeys.AsNoTracking().SingleAsync(x => x.Id == batch.ApiKeyId, cancellationToken);
            return new(candidate, batch, key);
        }
        return null;
    }

    public async Task CompleteItemAsync(Guid itemId, string worker, int status, string resultJson, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.BatchItems.Where(x => x.Id == itemId && x.Status == "running" && x.LeaseOwner == worker)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status < 400 ? "completed" : "failed")
                .SetProperty(x => x.HttpStatus, status).SetProperty(x => x.ResultJson, resultJson)
                .SetProperty(x => x.LeaseOwner, (string?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);
    }

    public async Task MaintainBatchesAsync(DateTimeOffset now, DateTimeOffset retainAfter, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // A lost worker may already have called the provider. Report an unknown result rather than replaying it.
        var lost = await db.BatchItems.AsNoTracking().Where(x => x.Status == "running" && x.LeaseExpiresAt < now).Take(1000).ToListAsync(cancellationToken);
        foreach (var item in lost)
        {
            var result = JsonSerializer.Serialize(new
            {
                id = item.Id.ToString("N"),
                custom_id = item.CustomId,
                response = (object?)null,
                error = new { code = "batch_item_interrupted", message = "The worker stopped before recording a result. Usage may have been incurred." }
            });
            await db.BatchItems.Where(x => x.Id == item.Id && x.Status == "running" && x.LeaseExpiresAt < now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "failed").SetProperty(x => x.HttpStatus, 500)
                    .SetProperty(x => x.ResultJson, result), cancellationToken);
        }
        var active = await db.Batches.AsNoTracking().Where(x => x.Status == "in_progress" || x.Status == "cancelling")
            .OrderBy(x => x.CreatedAt).Take(1000).Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var id in active)
            await TransactionAsync(async transaction =>
            {
                await transaction.Batches.Where(x => x.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, x => x.Status), cancellationToken);
                var job = await transaction.Batches.SingleAsync(x => x.Id == id, cancellationToken);
                if (job.Status is not ("in_progress" or "cancelling")) return false;
                if (job.ExpiresAt <= now || job.Status == "cancelling")
                    await transaction.BatchItems.Where(x => x.BatchId == id && x.Status == "pending")
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "cancelled"), cancellationToken);
                var counts = await transaction.BatchItems.Where(x => x.BatchId == id).GroupBy(x => x.Status)
                    .Select(g => new { Status = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);
                job.Completed = counts.GetValueOrDefault("completed");
                job.Failed = counts.GetValueOrDefault("failed");
                if (counts.GetValueOrDefault("pending") == 0 && counts.GetValueOrDefault("running") == 0)
                {
                    job.Status = job.Status == "cancelling" ? "cancelled" : job.ExpiresAt <= now ? "expired" : "completed";
                    job.FinishedAt = now;
                    foreach (var errors in new[] { false, true })
                    {
                        var fileId = errors ? job.ErrorFileId : job.OutputFileId;
                        var status = errors ? "failed" : "completed";
                        // Length metadata is computed without materializing all output bodies together.
                        long bytes = 0;
                        await foreach (var row in transaction.BatchItems.Where(x => x.BatchId == id && x.Status == status && x.ResultJson != null)
                            .Select(x => x.ResultJson!).AsAsyncEnumerable().WithCancellation(cancellationToken))
                            bytes += Encoding.UTF8.GetByteCount(row) + 1;
                        transaction.Files.Add(new GatewayFile
                        {
                            Id = fileId,
                            ApiKeyId = job.ApiKeyId,
                            BatchId = id,
                            ErrorsOnly = errors,
                            Purpose = "batch_output",
                            Filename = errors ? "errors.jsonl" : "output.jsonl",
                            Bytes = bytes,
                            CreatedAt = now
                        });
                    }
                }
                await transaction.SaveChangesAsync(cancellationToken);
                return true;
            }, cancellationToken);
        await db.Files.Where(x => x.BatchId != null && db.Batches.Any(job => job.Id == x.BatchId && job.FinishedAt < retainAfter))
            .ExecuteDeleteAsync(cancellationToken);
        await db.Batches.Where(x => x.FinishedAt < retainAfter).ExecuteDeleteAsync(cancellationToken);
        await db.Files.Where(x => x.CreatedAt < retainAfter && x.BatchId == null
            && !db.Batches.Any(job => job.InputFileId == x.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}
