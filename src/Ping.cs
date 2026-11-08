using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace WeatherImages;

public class Ping
{
    [Function("Ping")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequest req)
        => new OkObjectResult(new { status = "ok", time = DateTimeOffset.UtcNow });
}
