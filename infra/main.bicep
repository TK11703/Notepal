// Notepal – lowest-cost Azure footprint:
//  * Azure Container Apps (Consumption, scale to zero) for the Blazor web app and the API
//  * Azure Database for PostgreSQL Flexible Server, Burstable B1ms, 32 GB
//  * Azure Container Registry: an existing shared registry (not created here)
//  * Azure AI Foundry: an existing shared project and model deployment for the OCR agent (not created here)
//  * Log Analytics (PerGB, 30 day retention, daily cap)
//  * Aspire dashboard (Container Apps .NET component) for live traces, metrics and logs
targetScope = 'resourceGroup'

@description('Prefix used to name all resources.')
@minLength(3)
@maxLength(12)
param namePrefix string = 'notepal'

@description('Region for all resources.')
param location string = resourceGroup().location

@description('Entra ID tenant that signs users in.')
param tenantId string = tenant().tenantId

@description('Application (client) id of the Notepal API app registration.')
param apiClientId string

@description('Application (client) id of the Notepal Web app registration.')
param webClientId string

@description('PostgreSQL administrator login.')
param postgresAdminLogin string = 'notepaladmin'

@description('PostgreSQL administrator password.')
@secure()
param postgresAdminPassword string

@description('Full image reference for the API (e.g. myacr.azurecr.io/notepal-api:sha). Leave empty to deploy only the shared infrastructure.')
param apiImage string = ''

@description('Full image reference for the web app. Leave empty to deploy only the shared infrastructure.')
param webImage string = ''

@description('Existing (shared) Foundry account that hosts the OCR project.')
param foundryAccountName string = 'aif-acc-common'

@description('Resource group of the Foundry account.')
param foundryResourceGroup string = 'rg-common'

@description('Existing Foundry project the OCR agent is created in.')
param foundryProjectName string = 'proj-default'

@description('Existing model deployment used by the OCR agent. Must support image input.')
param ocrModelDeploymentName string = 'gpt-4.1-mini'

@description('Deploy the Aspire dashboard in the Container Apps environment.')
param enableAspireDashboard bool = true

@description('Existing (shared) container registry that the images are built in and pulled from.')
param registryName string = 'acracccommon'

@description('Resource group of the shared container registry.')
param registryResourceGroup string = 'rg-common'

var suffix = uniqueString(resourceGroup().id)
var deployApps = !empty(apiImage) && !empty(webImage)
var databaseName = 'notepal'

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${namePrefix}-api'
  location: location
}

resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${namePrefix}-web'
  location: location
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${namePrefix}-${suffix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: 1 }
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
  scope: resourceGroup(registryResourceGroup)
}

module acrPull 'modules/acr-pull.bicep' = {
  name: 'acr-pull-${namePrefix}'
  scope: resourceGroup(registryResourceGroup)
  params: {
    registryName: registryName
    principals: [
      { identityId: apiIdentity.id, principalId: apiIdentity.properties.principalId }
      { identityId: webIdentity.id, principalId: webIdentity.properties.principalId }
    ]
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'psql-${namePrefix}-${suffix}'
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '16'
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    storage: {
      storageSizeGB: 32
      autoGrow: 'Disabled'
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
    network: { publicNetworkAccess: 'Enabled' }
    authConfig: {
      activeDirectoryAuth: 'Disabled'
      passwordAuth: 'Enabled'
    }
  }

  resource database 'databases' = {
    name: databaseName
    properties: {
      charset: 'UTF8'
      collation: 'en_US.utf8'
    }
  }

  // 0.0.0.0 - 0.0.0.0 allows connections from Azure services only (no VNet needed, which keeps cost down).
  resource allowAzure 'firewallRules' = {
    name: 'AllowAzureServices'
    properties: {
      startIpAddress: '0.0.0.0'
      endIpAddress: '0.0.0.0'
    }
  }
}

resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName
  scope: resourceGroup(foundryResourceGroup)

  resource ocrDeployment 'deployments' existing = {
    name: ocrModelDeploymentName
  }
}

module apiFoundryUser 'modules/foundry-user.bicep' = {
  name: 'foundry-user-${namePrefix}-api'
  scope: resourceGroup(foundryResourceGroup)
  params: {
    accountName: foundryAccountName
    projectName: foundryProjectName
    identityId: apiIdentity.id
    principalId: apiIdentity.properties.principalId
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${namePrefix}-${suffix}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

// Container Apps injects OTEL_EXPORTER_OTLP_ENDPOINT into the apps, which ServiceDefaults uses. Opening it needs Contributor/Owner on the environment.
resource aspireDashboard 'Microsoft.App/managedEnvironments/dotNetComponents@2025-10-02-preview' = if (enableAspireDashboard) {
  parent: environment
  name: 'aspire-dashboard'
  properties: {
    componentType: 'AspireDashboard'
  }
}

var foundryProjectEndpoint = 'https://${foundry.properties.customSubDomainName}.services.ai.azure.com/api/projects/${foundryProjectName}'
var postgresConnectionString = 'Host=${postgres.properties.fullyQualifiedDomainName};Database=${databaseName};Username=${postgresAdminLogin};Password=${postgresAdminPassword};SSL Mode=Require'

resource api 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: 'ca-${namePrefix}-api'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${apiIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      // Internal ingress: the API is only reachable from inside the Container Apps environment (i.e. by the web app).
      ingress: {
        external: false
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: apiIdentity.id
        }
      ]
      secrets: [
        {
          name: 'postgres-connection'
          value: postgresConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'ConnectionStrings__Notepal', secretRef: 'postgres-connection' }
            { name: 'AzureAd__TenantId', value: tenantId }
            { name: 'AzureAd__ClientId', value: apiClientId }
            { name: 'Ocr__ProjectEndpoint', value: foundryProjectEndpoint }
            { name: 'Ocr__ModelDeploymentName', value: foundry::ocrDeployment.name }
            { name: 'Ocr__ManagedIdentityClientId', value: apiIdentity.properties.clientId }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/healthz', port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [
          {
            name: 'http'
            http: { metadata: { concurrentRequests: '50' } }
          }
        ]
      }
    }
  }
  dependsOn: [
    acrPull
    apiFoundryUser
  ]
}

resource web 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: 'ca-${namePrefix}-web'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${webIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        // Blazor Server keeps a SignalR circuit per user, so keep each browser on the same replica.
        stickySessions: { affinity: 'sticky' }
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: webIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'AzureAd__TenantId', value: tenantId }
            { name: 'AzureAd__ClientId', value: webClientId }
            // Secret-less: the web app proves its identity with its managed identity (federated credential on the app registration).
            { name: 'AzureAd__ClientCredentials__0__SourceType', value: 'SignedAssertionFromManagedIdentity' }
            { name: 'AzureAd__ClientCredentials__0__ManagedIdentityClientId', value: webIdentity.properties.clientId }
            { name: 'NotepalApi__BaseUrl', value: 'https://${api!.properties.configuration.ingress.fqdn}/' }
            { name: 'NotepalApi__Scopes__0', value: 'api://${apiClientId}/access_as_user' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/healthz', port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [
          {
            name: 'http'
            http: { metadata: { concurrentRequests: '50' } }
          }
        ]
      }
    }
  }
  dependsOn: [
    acrPull
  ]
}

output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output foundryProjectEndpoint string = foundryProjectEndpoint
// Reading the model deployment fails the deployment if it doesn't exist.
output ocrModel string = '${foundry::ocrDeployment.properties.model.name} (${foundry::ocrDeployment.properties.model.version})'
output postgresServer string = postgres.properties.fullyQualifiedDomainName
output webIdentityPrincipalId string = webIdentity.properties.principalId
output webUrl string = deployApps ? 'https://${web!.properties.configuration.ingress.fqdn}' : ''
