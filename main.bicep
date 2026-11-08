@description('Short lowercase prefix for resource names.')
param prefix string = 'weather'
param location string = resourceGroup().location

@description('Optional resource ID of an existing Container Apps environment in the deployment region.')
param existingEnvironmentId string = ''

@description('Container image for the function app. deploy.ps1 swaps in the real image after the first deploy.')
param image string = 'mcr.microsoft.com/azure-functions/dotnet8-quickstart-demo:1.0'

var suffix = uniqueString(resourceGroup().id)
var startQueueName = 'start-jobs'
var imageQueueName = 'image-jobs'
var imagesContainerName = 'images'
var jobsTableName = 'jobs' // names mirrored in src/Jobs.cs (Names)

// ---------- Storage: blobs + queues ----------
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('${prefix}${suffix}', 24)
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: true // images are served straight from blob
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource imagesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: imagesContainerName
  properties: { publicAccess: 'Blob' }
}

// Job status + per-station progress, read by GET /api/jobs/{id}.
resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource jobsTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: jobsTableName
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource startQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: startQueueName
}

resource imageQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: imageQueueName
}

var storageConnection = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

// ---------- Container registry ----------
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: '${prefix}acr${suffix}'
  location: location
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: true } // ponytail: admin creds, swap for managed identity pull if needed
}

// ---------- Container Apps environment ----------
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (empty(existingEnvironmentId)) {
  name: '${prefix}-logs-${suffix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = if (empty(existingEnvironmentId)) {
  name: '${prefix}-env-${suffix}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs!.properties.customerId
        sharedKey: logs!.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }
    ]
  }
}

// ---------- Function app hosted on Container Apps ----------
resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: '${prefix}-func-${suffix}'
  location: location
  kind: 'functionapp,linux,container,azurecontainerapps'
  properties: {
    managedEnvironmentId: empty(existingEnvironmentId) ? env!.id : existingEnvironmentId
    ...(empty(existingEnvironmentId) ? {
      workloadProfileName: 'Consumption'
      resourceConfig: {
        cpu: json('0.5')
        memory: '1Gi'
      }
    } : {})
    siteConfig: {
      linuxFxVersion: 'DOCKER|${image}'
      minimumElasticInstanceCount: 0
      functionAppScaleLimit: 10
      appSettings: [
        { name: 'AzureWebJobsStorage', value: storageConnection }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'PUBLIC_URL_SCHEME', value: 'https' }
        { name: 'DOCKER_REGISTRY_SERVER_URL', value: acr.properties.loginServer }
        { name: 'DOCKER_REGISTRY_SERVER_USERNAME', value: acr.listCredentials().username }
        { name: 'DOCKER_REGISTRY_SERVER_PASSWORD', value: acr.listCredentials().passwords[0].value }
      ]
    }
  }
  dependsOn: [startQueue, imageQueue, imagesContainer, jobsTable]
}

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output acrName string = acr.name
output acrLoginServer string = acr.properties.loginServer
output storageAccountName string = storage.name
