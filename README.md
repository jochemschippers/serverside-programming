# Weather Images API

This Azure Functions app runs on Azure Container Apps. It fetches current weather observations from Buienradar, uses two Azure Storage Queues to generate one image per station in the background, and serves the finished images from Blob Storage.

## Use the API

Open [api.http](api.http) in VS Code with the REST Client extension. The file is configured for the live deployment.

1. Run `GET /api/Ping` to check that the API responds with `status: ok`.
2. Run `POST /api/jobs` to start a job. The API returns `202 Accepted` with a `jobId` and a `statusUrl`.
3. Run `GET /api/jobs/{id}` or open the returned `statusUrl`. Repeat until the status is `completed`.
4. Open any link in the `images` list to view the generated image.

Job status and progress are saved in the `jobs` table in Azure Table Storage: one row per job (status, station count, timestamps) and one row per finished station image (image URL). Each job processes up to 50 unique stations. The live feed returned 40 stations during verification, so that job produced 40 images.

## Deploy to Azure

Install the .NET 8 SDK and Azure CLI, start Docker Desktop, and sign in with `az login`. Run [deploy.ps1](deploy.ps1) from the repository root in PowerShell. It publishes the app, provisions resources from [main.bicep](main.bicep), and deploys the container image.

Run these commands to redeploy to the existing Container Apps environment in Italy North.

```powershell
$envId = az containerapp env show -g inh-ccd-lab2-js260904 -n inh-ccd-lab2-env-js260904 --query id -o tsv
.\deploy.ps1 -Location italynorth -ExistingEnvironmentId $envId
```

For a new environment, run `.\deploy.ps1` and pass `-Location` if your subscription requires a different region. The script prints the health check URL when it finishes. If the hostname changed, update `@baseUrl` in `api.http`.
