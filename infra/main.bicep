@description('Lowercase prefix used for Azure resource names.')
@minLength(3)
@maxLength(10)
param namePrefix string

@description('Object ID of the Microsoft Entra user or group that administers Azure SQL.')
param sqlAdminObjectId string

@description('Sign-in name of the Microsoft Entra SQL administrator.')
param sqlAdminLogin string

@description('Azure region for the resources.')
param location string = resourceGroup().location

var suffix = uniqueString(resourceGroup().id)
var sqlServerName = toLower(replace('${namePrefix}-${suffix}', '-', ''))
var storageAccountName = toLower(replace('${namePrefix}${suffix}', '-', ''))
var apiName = '${namePrefix}-api-${suffix}'
var staticWebAppName = '${namePrefix}-web-${suffix}'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource fileContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'files'
  properties: {
    publicAccess: 'None'
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${namePrefix}-plan-${suffix}'
  location: location
  kind: 'linux'
  sku: {
    name: 'F1'
    tier: 'Basic'
    size: 'F1'
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource api 'Microsoft.Web/sites@2024-04-01' = {
  name: apiName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: false
      ftpsState: 'Disabled'
      http20Enabled: true
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
    }
  }
}

resource sqlServer 'Microsoft.Sql/servers@2022-05-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    publicNetworkAccess: 'Enabled'
    minimalTlsVersion: '1.2'
  }
}

resource sqlEntraAdmin 'Microsoft.Sql/servers/administrators@2022-05-01-preview' = {
  parent: sqlServer
  name: 'ActiveDirectory'
  properties: {
    administratorType: 'ActiveDirectory'
    login: sqlAdminLogin
    sid: sqlAdminObjectId
    tenantId: tenant().tenantId
  }
}

resource database 'Microsoft.Sql/servers/databases@2022-05-01-preview' = {
  parent: sqlServer
  name: 'FileLibrary'
  location: location
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
}

resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, api.id, 'Storage Blob Data Contributor')
  scope: storage
  properties: {
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

resource apiSettings 'Microsoft.Web/sites/config@2022-03-01' = {
  parent: api
  name: 'appsettings'
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    ConnectionStrings__FileLibrary: 'Server=tcp:${sqlServer.name}.${environment().suffixes.sqlServerHostname},1433;Initial Catalog=${database.name};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;Authentication=Active Directory Managed Identity;'
    BlobServiceUri: 'https://${storage.name}.blob.${environment().suffixes.storage}'
    BlobContainerName: fileContainer.name
    AllowedOrigins__0: 'https://${staticWebApp.properties.defaultHostname}'
  }
}

resource staticWebApp 'Microsoft.Web/staticSites@2024-04-01' = {
  name: staticWebAppName
  location: location
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    stagingEnvironmentPolicy: 'Enabled'
    allowConfigFileUpdates: true
  }
}

output apiName string = api.name
output apiUrl string = 'https://${api.properties.defaultHostName}'
output sqlServerName string = sqlServer.name
output sqlDatabaseName string = database.name
output storageAccountName string = storage.name
output staticWebAppName string = staticWebApp.name
output staticWebAppUrl string = 'https://${staticWebApp.properties.defaultHostname}'
