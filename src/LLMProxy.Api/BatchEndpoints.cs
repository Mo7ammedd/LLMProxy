using System.Text;
using System.Text.Json.Nodes;
using LLMProxy.Application;
using LLMProxy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LLMProxy.Api;

public static class BatchEndpoints
{
    public static void MapBatches(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/v1").RequireAuthorization();
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            await http.RequestServices.GetRequiredService<GatewayService>().CheckRateLimitAsync(http.ApiKey(), null, http.RequestAborted);
            return await next(context);
        });
        api.MapPost("/files", async (HttpContext http, BatchService batches, GatewayService gateway, GatewayOptions options) =>
        {
            if (!http.Request.HasFormContentType) throw new GatewayException("Use multipart/form-data.", "invalid_content_type", 415);
            var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = options.Batches.MaxFileBytes + 65536L;
            http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions { MultipartBodyLengthLimit = options.Batches.MaxFileBytes }));
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (form["purpose"] != "batch" || form.Files.Count != 1) throw new GatewayException("Upload one file with purpose=batch.", "invalid_file");
            var upload = form.Files[0];
            if (upload.Length > options.Batches.MaxFileBytes) throw new GatewayException("File too large.", "file_too_large", 413);
            using var reader = new StreamReader(upload.OpenReadStream(), new UTF8Encoding(false, true));
            string content;
            try { content = await reader.ReadToEndAsync(http.RequestAborted); }
            catch (DecoderFallbackException) { throw new GatewayException("Files must contain UTF-8 JSONL.", "invalid_encoding"); }
            return Results.Json(FileObject(await batches.UploadAsync(http.ApiKey(), upload.FileName, content, http.RequestAborted)), statusCode: 201);
        });
        api.MapGet("/files", async (HttpContext http, IBatchStore store, GatewayService gateway, string? after, int? limit) =>
        {
            var count = Limit(limit);
            var files = await store.ListFilesAsync(http.ApiKey().Id, after, count + 1, http.RequestAborted);
            return Results.Json(new
            {
                @object = "list",
                data = files.Take(count).Select(FileObject),
                has_more = files.Count > count,
                first_id = files.FirstOrDefault()?.Id,
                last_id = files.Take(count).LastOrDefault()?.Id
            });
        });
        api.MapGet("/files/{id}", async (string id, HttpContext http, IBatchStore store) =>
            Results.Json(FileObject(await FileAsync(id, http, store))));
        api.MapGet("/files/{id}/content", async (string id, HttpContext http, IBatchStore store) =>
        {
            var file = await FileAsync(id, http, store);
            http.Response.ContentType = "application/jsonl; charset=utf-8";
            http.Response.Headers.ContentDisposition = "attachment; filename=\"batch.jsonl\"";
            if (file.Content is { } content) await http.Response.WriteAsync(content, http.RequestAborted);
            else if (file.BatchId is { } batch)
                await foreach (var line in store.ReadOutputAsync(batch, file.ErrorsOnly, http.RequestAborted))
                    await http.Response.WriteAsync(line + "\n", http.RequestAborted);
        });
        api.MapDelete("/files/{id}", async (string id, HttpContext http, IBatchStore store) =>
        {
            if (!await store.DeleteFileAsync(http.ApiKey().Id, id, http.RequestAborted)) throw new GatewayException("File not found.", "file_not_found", 404);
            return Results.Json(new { id, @object = "file", deleted = true });
        });
        api.MapPost("/batches", async (HttpContext http, BatchService batches, GatewayService gateway) =>
        {
            return Results.Json(BatchObject(await batches.CreateAsync(http.ApiKey(), await GatewayEndpoints.ReadAsync<CreateBatch>(http), http.RequestAborted)));
        });
        api.MapGet("/batches", async (HttpContext http, IBatchStore store, GatewayService gateway, string? after, int? limit) =>
        {
            var count = Limit(limit);
            var batches = await store.ListBatchesAsync(http.ApiKey().Id, after, count + 1, http.RequestAborted);
            return Results.Json(new
            {
                @object = "list",
                data = batches.Take(count).Select(BatchObject),
                has_more = batches.Count > count,
                first_id = batches.FirstOrDefault()?.Id,
                last_id = batches.Take(count).LastOrDefault()?.Id
            });
        });
        api.MapGet("/batches/{id}", async (string id, HttpContext http, IBatchStore store) => Results.Json(BatchObject(
            await store.GetBatchAsync(http.ApiKey().Id, id, http.RequestAborted) ?? throw new GatewayException("Batch not found.", "batch_not_found", 404))));
        api.MapPost("/batches/{id}/cancel", async (string id, HttpContext http, IBatchStore store) => Results.Json(BatchObject(
            await store.CancelBatchAsync(http.ApiKey().Id, id, http.RequestAborted) ?? throw new GatewayException("Batch not found.", "batch_not_found", 404))));
    }

    private static async Task<GatewayFile> FileAsync(string id, HttpContext http, IBatchStore store) =>
        await store.GetFileAsync(http.ApiKey().Id, id, http.RequestAborted) ?? throw new GatewayException("File not found.", "file_not_found", 404);
    private static int Limit(int? limit) => limit is null ? 20 : limit is >= 1 and <= 100 ? limit.Value
        : throw new GatewayException("limit must be 1–100.", "invalid_limit");
    private static object FileObject(GatewayFile file) => new
    {
        file.Id,
        @object = "file",
        file.Bytes,
        created_at = file.CreatedAt.ToUnixTimeSeconds(),
        file.Filename,
        file.Purpose,
        status = "processed"
    };
    private static object BatchObject(BatchJob job) => new
    {
        job.Id,
        @object = "batch",
        job.Endpoint,
        input_file_id = job.InputFileId,
        completion_window = "24h",
        job.Status,
        created_at = job.CreatedAt.ToUnixTimeSeconds(),
        in_progress_at = job.CreatedAt.ToUnixTimeSeconds(),
        expires_at = job.ExpiresAt.ToUnixTimeSeconds(),
        completed_at = job.Status == "completed" ? job.FinishedAt?.ToUnixTimeSeconds() : null,
        cancelled_at = job.Status == "cancelled" ? job.FinishedAt?.ToUnixTimeSeconds() : null,
        expired_at = job.Status == "expired" ? job.FinishedAt?.ToUnixTimeSeconds() : null,
        output_file_id = job.FinishedAt is not null && job.Completed > 0 ? job.OutputFileId : null,
        error_file_id = job.FinishedAt is not null && job.Failed > 0 ? job.ErrorFileId : null,
        request_counts = new { total = job.Total, completed = job.Completed, failed = job.Failed },
        metadata = JsonNode.Parse(job.MetadataJson)
    };
}
