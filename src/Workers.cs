using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace WeatherImages;

public record StartMessage(string JobId);
public record ImageMessage(string JobId, Station Station);

// Subset of a Buienradar "stationmeasurements" entry. Some stations omit values, hence nullable.
public record Station(
    int StationId, string StationName, string? Regio, DateTime? Timestamp, string? WeatherDescription,
    double? Temperature, double? FeelTemperature, double? Humidity, double? WindSpeed, string? WindDirection,
    double? Precipitation);

public class Workers(BlobServiceClient blobs, QueueServiceClient queues, TableServiceClient tables, HttpClient http, ILogger<Workers> log)
{
    public const int MaxStations = 50;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Explicit settings: by default ImageSharp 3.1 copies the source's progressive-JPEG metadata
    // and writes a scrambled file (two half-width copies of the image).
    static readonly JpegEncoder Jpeg = new() { Quality = 85, Interleaved = true, ColorType = JpegEncodingColor.YCbCrRatio420 };

    // Overridable so the 50-station case can be tested with a local sample file.
    static readonly string FeedSource = Environment.GetEnvironmentVariable("BUIENRADAR_URL") ?? "https://data.buienradar.nl/2.0/feed/json";

    [Function("StartWorker")]
    public async Task Start([QueueTrigger(Names.StartQueue)] string message)
    {
        var jobId = JsonSerializer.Deserialize<StartMessage>(message, Json)!.JobId;

        var feed = File.Exists(FeedSource) ? await File.ReadAllTextAsync(FeedSource) : await http.GetStringAsync(FeedSource);
        var stations = SelectStations(JsonNode.Parse(feed)!["actual"]!["stationmeasurements"]!.Deserialize<List<Station>>(Json)!);
        log.LogInformation("Job {JobId}: {Count} stations", jobId, stations.Count);

        // Merge keeps CreatedAt from the POST.
        await tables.GetTableClient(Names.JobsTable).UpsertEntityAsync(new TableEntity(jobId, Names.JobRow)
        {
            ["Status"] = "processing",
            ["StationCount"] = stations.Count,
        }, TableUpdateMode.Merge);

        // Fan out: one message per station. A retry re-sends these; image paths are fixed, so no duplicate results.
        var imageQueue = queues.GetQueueClient(Names.ImageQueue);
        await Task.WhenAll(stations.Select(s => imageQueue.SendMessageAsync(JsonSerializer.Serialize(new ImageMessage(jobId, s), Json))));
    }

    public static List<Station> SelectStations(IEnumerable<Station> all) =>
        all.DistinctBy(s => s.StationId).Take(MaxStations).ToList();

    [Function("ImageWorker")]
    public async Task Image([QueueTrigger(Names.ImageQueue)] string message)
    {
        var (jobId, station) = JsonSerializer.Deserialize<ImageMessage>(message, Json)!;

        using var image = await LoadBackground($"{jobId}-{station.StationId}");
        DrawWeather(image, station);

        using var jpeg = new MemoryStream();
        await image.SaveAsJpegAsync(jpeg, Jpeg);
        jpeg.Position = 0;
        var blob = blobs.GetBlobContainerClient(Names.ImagesContainer).GetBlobClient($"{jobId}/{station.StationId}.jpg");
        await blob.UploadAsync(jpeg, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "image/jpeg" } });

        // Progress row per station. Retries overwrite the same blob and the same row, so nothing is counted twice.
        await tables.GetTableClient(Names.JobsTable).UpsertEntityAsync(new TableEntity(jobId, station.StationId.ToString())
        {
            ["StationName"] = station.StationName,
            ["ImageUrl"] = blob.Uri.ToString(),
        }, TableUpdateMode.Replace);
    }

    async Task<Image<Rgba32>> LoadBackground(string seed)
    {
        try
        {
            // Lorem Picsum: free public image API, no key. Seed keeps retries on the same picture.
            // JPEG decoder + SkipMetadata: keeps ImageSharp 3.1's vulnerable TIFF/ICC code out of reach (see csproj).
            using var body = await http.GetStreamAsync($"https://picsum.photos/seed/{seed}/1024/768");
            return JpegDecoder.Instance.Decode<Rgba32>(new DecoderOptions { SkipMetadata = true }, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ImageFormatException)
        {
            // ponytail: plain background so one flaky download doesn't stall the job at N-1 images.
            log.LogWarning(ex, "Background download failed for {Seed}, using plain background", seed);
            return new Image<Rgba32>(1024, 768, Color.SteelBlue);
        }
    }

    static readonly FontFamily Family =
        SystemFonts.TryGet("DejaVu Sans", out var dejavu) ? dejavu :   // Linux container (fonts-dejavu-core)
        SystemFonts.TryGet("Arial", out var arial) ? arial :           // local Windows
        SystemFonts.Families.First();

    static void DrawWeather(Image<Rgba32> image, Station s)
    {
        static string N(double? v, string unit) => v is double d ? $"{d:0.#}{unit}" : "-";
        var text = string.Join('\n',
            s.StationName,
            s.WeatherDescription ?? "",
            $"Temp {N(s.Temperature, " Â°C")}  (feels {N(s.FeelTemperature, " Â°C")})",
            $"Wind {s.WindDirection} {N(s.WindSpeed, " m/s")}   Humidity {N(s.Humidity, "%")}   Rain {N(s.Precipitation, " mm/h")}",
            s.Timestamp is DateTime t ? $"Measured {t:dd-MM-yyyy HH:mm}" : "");

        const int band = 230;
        image.Mutate(x => x
            .Fill(Color.Black.WithAlpha(0.55f), new RectangularPolygon(0, image.Height - band, image.Width, band))
            .DrawText(new RichTextOptions(Family.CreateFont(30))
            {
                Origin = new PointF(28, image.Height - band + 18),
                WrappingLength = image.Width - 56,
                LineSpacing = 1.15f,
            }, text, Color.White));
    }
}
