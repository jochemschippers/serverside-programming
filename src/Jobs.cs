using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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
    public const string ImagesContainer = "images"; // public: images/{jobId}/{stationId}.jpg
    public const string JobsContainer = "jobs";     // private: jobs/{jobId}.json
}

public record JobManifest(DateTimeOffset CreatedAt, int? StationCount);

public class Jobs(BlobServiceClient blobs, QueueServiceClient queues)
{
    [Function("StartJob")]
    public async Task<IActionResult> Start([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "jobs")] HttpRequest req)
    {
        var jobId = Guid.NewGuid().ToString("N");

        // Manifest first so GET can tell "queued" apart from "unknown id".
        await blobs.GetBlobContainerClient(Names.JobsContainer)
            .GetBlobClient($"{jobId}.json")
            .UploadAsync(BinaryData.FromObjectAsJson(new JobManifest(DateTimeOffset.UtcNow, null)));
        await queues.GetQueueClient(Names.StartQueue)
            .SendMessageAsync(JsonSerializer.Serialize(new StartMessage(jobId)));

        // Container Apps ingress terminates TLS, so trust X-Forwarded-Proto for the scheme.
        var scheme = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? req.Scheme;
        var statusUrl = $"{scheme}://{req.Host}/api/jobs/{jobId}";
        return new AcceptedResult(statusUrl, new { jobId, statusUrl });
    }

    [Function("GetJob")]
    public async Task<IActionResult> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs/{id}")] HttpRequest req, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return new NotFoundResult();

        var manifestBlob = blobs.GetBlobContainerClient(Names.JobsContainer).GetBlobClient($"{id}.json");
        if (!await manifestBlob.ExistsAsync()) return new NotFoundResult();
        var manifest = (await manifestBlob.DownloadContentAsync()).Value.Content.ToObjectFromJson<JobManifest>()!;

        if (manifest.StationCount is not int total)
            return new OkObjectResult(new { jobId = id, status = "queued", manifest.CreatedAt });

        var images = blobs.GetBlobContainerClient(Names.ImagesContainer);
        var urls = new List<string>();
        await foreach (var blob in images.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{id}/", default))
            urls.Add(images.GetBlobClient(blob.Name).Uri.ToString());

        var done = urls.Count >= total;
        return new OkObjectResult(new
        {
            jobId = id,
            status = done ? "completed" : "processing",
            manifest.CreatedAt,
            stationCount = total,
            imagesCompleted = urls.Count,
            images = done ? urls : null,
        });
    }
}
