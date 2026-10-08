// Notepal on shared infrastructure. Deployed into the apps resource group (rg-apps), which holds the shared
// Container Apps environment (with its Log Analytics workspace and Aspire dashboard). Created here:
//  * user-assigned identities for the API and web app (in this resource group, so they survive app replacement)
//  * the two container apps (Consumption, scale to zero) and their role assignments
// Existing, shared resources in the platform resource group (not created here):
//  * PostgreSQL flexible server: the API signs in with its managed identity (Entra ID), which owns the database
//  * Container registry, pulled from with the shared, AcrPull-only identity
//  * Foundry account and vision model deployment used for OCR
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

@description('Full image reference for the API (e.g. myacr.azurecr.io/notepal-api:sha). Leave empty to deploy only the shared infrastructure.')
param apiImage string = ''

@description('Full image reference for the web app. Leave empty to deploy only the shared infrastructure.')
param webImage string = ''

@description('Existing (shared) Container Apps environment in this resource group.')
param environmentName string = 'cae-shared'

@description('Resource group of the shared platform resources (registry, PostgreSQL, Foundry, AcrPull identity).')
param platformResourceGroup string = 'rg-platform'

@description('Existing (shared) container registry that the images are built in and pulled from.')
param registryName string = 'acccrshared'

@description('Existing user-assigned identity that only has AcrPull on the registry; attached to every container app.')
param acrPullIdentityName string = 'id-shared-acrpull'

@description('Existing (shared) PostgreSQL flexible server with Entra ID authentication.')
param postgresServerName string = 'accpsqlshared'

@description('Database on the PostgreSQL server, owned by the API identity (see README).')
param databaseName string = 'notepal'

@description('Existing (shared) Foundry account that hosts the OCR model deployment.')
param foundryAccountName string = 'aif-shared-acc'

@description('Existing model deployment used for OCR. Must support image input.')
param ocrModelDeploymentName string = 'gpt-4.1-mini'

@description('Comma-separated Entra user object ids that get Contributor on the Container Apps environment to open the Aspire dashboard.')
param dashboardUserIds string = ''

var deployApps = !empty(apiImage) && !empty(webImage)
var contributorRole = 'b24988ac-6180-42a0-ab88-20f7382dd24c'
var dashboardUsers = filter(map(split(dashboardUserIds, ','), id => trim(id)), id => !empty(id))

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${namePrefix}-api'
  location: location
}

resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${namePrefix}-web'
  location: location
}

resource acrPullIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: acrPullIdentityName
  scope: resourceGroup(platformResourceGroup)
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
  scope: resourceGroup(platformResourceGroup)
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' existing = {
  name: postgresServerName
  scope: resourceGroup(platformResourceGroup)
}

resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName
  scope: resourceGroup(platformResourceGroup)

  resource ocrDeployment 'deployments' existing = {
    name: ocrModelDeploymentName
  }
}

module apiFoundryUser 'modules/foundry-user.bicep' = {
  name: 'foundry-user-${namePrefix}-api'
  scope: resourceGroup(platformResourceGroup)
  params: {
    accountName: foundryAccountName
    identityId: apiIdentity.id
    principalId: apiIdentity.properties.principalId
  }
}

// Shared environment; its Aspire dashboard component is managed outside this template.
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

// The dashboard ignores roles inherited from the resource group or subscription.
resource dashboardAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for userId in dashboardUsers: {
    name: guid(environment.id, userId, contributorRole)
    scope: environment
    properties: {
      principalId: userId
      principalType: 'User'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRole)
    }
  }
]

var ocrEndpoint = 'https://${foundry.properties.customSubDomainName}.openai.azure.com/'
// No password: the API gets an Entra token for its identity, whose PostgreSQL role has the identity's name.
var postgresConnectionString = 'Host=${postgres.properties.fullyQualifiedDomainName};Database=${databaseName};Username=${apiIdentity.name};SSL Mode=Require'

resource api 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: 'ca-${namePrefix}-api'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentity.id}': {}
      '${acrPullIdentity.id}': {}
    }
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
          identity: acrPullIdentity.id
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
            { name: 'ConnectionStrings__Notepal', value: postgresConnectionString }
            { name: 'Database__ManagedIdentityClientId', value: apiIdentity.properties.clientId }
            { name: 'AzureAd__TenantId', value: tenantId }
            { name: 'AzureAd__ClientId', value: apiClientId }
            { name: 'Ocr__Endpoint', value: ocrEndpoint }
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
    apiFoundryUser
  ]
}

resource web 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: 'ca-${namePrefix}-web'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${webIdentity.id}': {}
      '${acrPullIdentity.id}': {}
    }
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
          identity: acrPullIdentity.id
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
}

output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output ocrEndpoint string = ocrEndpoint
// Reading the model deployment fails the deployment if it doesn't exist.
output ocrModel string = '${foundry::ocrDeployment.properties.model.name} (${foundry::ocrDeployment.properties.model.version})'
output postgresServer string = postgres.properties.fullyQualifiedDomainName
output webIdentityPrincipalId string = webIdentity.properties.principalId
output webUrl string = deployApps ? 'https://${web!.properties.configuration.ingress.fqdn}' : ''
