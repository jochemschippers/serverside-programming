using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

var storage = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
builder.Services.AddSingleton(new BlobServiceClient(storage));
// Base64 matches what the queue trigger expects by default.
builder.Services.AddSingleton(new QueueServiceClient(storage, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
builder.Services.AddSingleton(new TableServiceClient(storage));
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

builder.Build().Run();
