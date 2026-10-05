// Notepal – lowest-cost Azure footprint:
//  * Azure Container Apps (Consumption, scale to zero) for the Blazor web app and the API
//  * Azure Database for PostgreSQL Flexible Server, Burstable B1ms, 32 GB
//  * Azure Container Registry, Basic
//  * Azure AI Foundry (AIServices account + project + pay-per-token GlobalStandard model deployment) for the OCR agent
//  * Log Analytics (PerGB, 30 day retention, daily cap)
//  * Aspire dashboard (Container Apps .NET component) for live traces, metrics and logs
targetScope = 'resourceGroup'

@description('Prefix used to name all resources.')
@minLength(3)
@maxLength(12)
param namePrefix string = 'notepal'

@description('Region for all resources except Foundry.')
param location string = resourceGroup().location

@description('Region for the Foundry account. Must offer the chosen model as GlobalStandard.')
param foundryLocation string = location

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

@description('Model used by the OCR agent. Must support image input.')
param ocrModelName string = 'gpt-4.1-mini'

@description('Model version for the OCR deployment.')
param ocrModelVersion string = '2025-04-14'

@description('Tokens-per-minute capacity (in thousands) of the OCR deployment. Pay-per-token, so this is only a rate limit.')
param ocrModelCapacity int = 30

@description('Deploy the Aspire dashboard in the Container Apps environment.')
param enableAspireDashboard bool = true

var suffix = uniqueString(resourceGroup().id)
var deployApps = !empty(apiImage) && !empty(webImage)
var databaseName = 'notepal'
var foundryProjectName = '${namePrefix}-project'

// Built-in role definition ids.
var acrPullRole = '7f951dda-4ed3-4ba8-8aa3-0b5a6f6e5ba5'
var azureAiUserRole = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

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

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: take('acr${replace(namePrefix, '-', '')}${suffix}', 50)
  location: location
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

resource apiAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, apiIdentity.id, acrPullRole)
  scope: registry
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
  }
}

resource webAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, webIdentity.id, acrPullRole)
  scope: registry
  properties: {
    principalId: webIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
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

resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: 'ai-${namePrefix}-${suffix}'
  location: foundryLocation
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: 'ai-${namePrefix}-${suffix}'
    allowProjectManagement: true
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
}

resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: foundry
  name: foundryProjectName
  location: foundryLocation
  identity: { type: 'SystemAssigned' }
  properties: {
    displayName: 'Notepal'
    description: 'OCR agent for Notepal'
  }
}

resource ocrDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: foundry
  name: ocrModelName
  sku: {
    name: 'GlobalStandard'
    capacity: ocrModelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: ocrModelName
      version: ocrModelVersion
    }
  }
  // Operations on the same account must not run concurrently.
  dependsOn: [
    foundryProject
  ]
}

resource apiAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, apiIdentity.id, azureAiUserRole)
  scope: foundry
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAiUserRole)
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
            { name: 'Ocr__ModelDeploymentName', value: ocrDeployment.name }
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
    apiAcrPull
    apiAiUser
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
    webAcrPull
  ]
}

output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output foundryProjectEndpoint string = foundryProjectEndpoint
output postgresServer string = postgres.properties.fullyQualifiedDomainName
output webIdentityPrincipalId string = webIdentity.properties.principalId
output webUrl string = deployApps ? 'https://${web!.properties.configuration.ingress.fqdn}' : ''
