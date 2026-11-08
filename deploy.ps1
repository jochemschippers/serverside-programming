# Publishes the function with the dotnet CLI, provisions Azure resources with Bicep,
# and deploys the container image to the Function App on Azure Container Apps.
# Prereqs: Azure CLI (az login done), .NET 8 SDK, Docker Desktop running.
param(
    [string]$ResourceGroup = 'rg-weatherimages',
    [string]$Location = 'swedencentral', # Azure for Students policy only allows a few EU regions
    [string]$Prefix = 'weather',
    [string]$ExistingEnvironmentId = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Invoke-Checked {
    & $args[0] $args[1..($args.Count - 1)]
    if ($LASTEXITCODE -ne 0) { throw "Command failed: $args" }
}

Write-Host '==> dotnet publish'
$publishDir = Join-Path $root 'src\publish'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
Invoke-Checked dotnet publish (Join-Path $root 'src') -c Release -o $publishDir

Write-Host '==> Register resource providers'
foreach ($ns in 'Microsoft.App', 'Microsoft.Web', 'Microsoft.ContainerRegistry', 'Microsoft.OperationalInsights', 'Microsoft.Storage') {
    Invoke-Checked az provider register --namespace $ns --wait --output none
}

Write-Host '==> Resource group + Bicep'
$groupExists = Invoke-Checked az group exists --name $ResourceGroup --output tsv
if ($groupExists -eq 'false') {
    Invoke-Checked az group create --name $ResourceGroup --location $Location --output none
}
$outputs = az deployment group create `
    --resource-group $ResourceGroup `
    --template-file (Join-Path $root 'main.bicep') `
    --parameters prefix=$Prefix location=$Location existingEnvironmentId=$ExistingEnvironmentId `
    --query properties.outputs --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Bicep deployment failed' }

$app = $outputs.functionAppName.value
$registry = $outputs.acrLoginServer.value
$image = "$registry/weatherimages:$(Get-Date -Format yyyyMMddHHmmss)"

Write-Host "==> Build + push $image"
Invoke-Checked az acr login --name $outputs.acrName.value
Invoke-Checked docker build -t $image (Join-Path $root 'src')
Invoke-Checked docker push $image

Write-Host '==> Point function app at new image'
# ponytail: Bicep re-run resets the image to the placeholder for a moment; this sets it right back.
Invoke-Checked az functionapp config container set --resource-group $ResourceGroup --name $app --image $image --output none

Write-Host "`nDeployed: $($outputs.functionAppUrl.value)/api/Ping"
