using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace WeatherImages;

// Names must match main.bicep.
public static class Names
{
    public const string StartQueue = "start-jobs";
    public const string ImageQueue = "image-jobs";
    public const string ImagesContainer = "images"; // private: images/{jobId}/{stationId}.jpg, read via SAS links
    public const string JobsTable = "jobs";

    // Table layout: PartitionKey = jobId.
    //   RowKey "job"        -> Status, CreatedAt, StationCount
    //   RowKey "{stationId}" -> StationName, ImageUrl (one row per finished image)
    public const string JobRow = "job";
}

public class Jobs(TableServiceClient tables, QueueServiceClient queues, BlobServiceClient blobs)
{
    // Callers send the key in the x-api-key header. No key configured = every call is rejected.
    static readonly byte[]? ApiKey = Environment.GetEnvironmentVariable("API_KEY") is { Length: > 0 } k ? Encoding.UTF8.GetBytes(k) : null;
    static bool Authorized(HttpRequest req) =>
        ApiKey is not null && CryptographicOperations.FixedTimeEquals(ApiKey, Encoding.UTF8.GetBytes(req.Headers["x-api-key"].ToString()));

    // Images container is private; links are read-only SAS URLs, re-issued on every status call.
    static readonly TimeSpan SasLifetime = TimeSpan.FromHours(24);

    [Function("StartJob")]
    public async Task<IActionResult> Start([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "jobs")] HttpRequest req)
    {
        if (!Authorized(req)) return new UnauthorizedResult();
        var jobId = Guid.NewGuid().ToString("N");

        // Status row first so GET can tell "queued" apart from "unknown id".
        await tables.GetTableClient(Names.JobsTable).AddEntityAsync(new TableEntity(jobId, Names.JobRow)
        {
            ["Status"] = "queued",
            ["CreatedAt"] = DateTimeOffset.UtcNow,
        });
        await queues.GetQueueClient(Names.StartQueue)
            .SendMessageAsync(JsonSerializer.Serialize(new StartMessage(jobId)));

        // Container Apps can pass the request to the worker over HTTP after terminating TLS.
        var scheme = Environment.GetEnvironmentVariable("PUBLIC_URL_SCHEME") ?? req.Scheme;
        var statusUrl = $"{scheme}://{req.Host}/api/jobs/{jobId}";
        return new AcceptedResult(statusUrl, new { jobId, statusUrl });
    }

    [Function("GetJob")]
    public async Task<IActionResult> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs/{id}")] HttpRequest req, string id)
    {
        if (!Authorized(req)) return new UnauthorizedResult();
        if (!Guid.TryParseExact(id, "N", out _)) return new NotFoundResult(); // also keeps the filter below injection-free

        var table = tables.GetTableClient(Names.JobsTable);
        TableEntity? job = null;
        var images = new List<TableEntity>();
        await foreach (var row in table.QueryAsync<TableEntity>(e => e.PartitionKey == id))
        {
            if (row.RowKey == Names.JobRow) job = row; else images.Add(row);
        }
        if (job is null) return new NotFoundResult();

        var total = job.GetInt32("StationCount");
        var done = total is int t && images.Count >= t;
        var status = done ? "completed" : job.GetString("Status");

        // Persist the final state once, so the table itself shows completed jobs.
        if (done && job.GetString("Status") != "completed")
            await table.UpdateEntityAsync(new TableEntity(id, Names.JobRow) { ["Status"] = "completed", ["CompletedAt"] = DateTimeOffset.UtcNow }, Azure.ETag.All, TableUpdateMode.Merge);

        return new OkObjectResult(new
        {
            jobId = id,
            status,
            createdAt = job.GetDateTimeOffset("CreatedAt"),
            stationCount = total,
            imagesCompleted = images.Count,
            images = done ? images.Select(r => SignedUrl($"{id}/{r.RowKey}.jpg")) : null,
        });
    }

    string SignedUrl(string blobName) =>
        blobs.GetBlobContainerClient(Names.ImagesContainer).GetBlobClient(blobName)
            .GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(SasLifetime)).ToString();
}
