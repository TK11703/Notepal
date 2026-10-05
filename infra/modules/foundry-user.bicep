// Grants Foundry User (formerly Azure AI User) on an existing Foundry project. Deployed at the account's resource group scope.
param accountName string
param projectName string

@description('Resource id of the identity; keeps the role assignment name stable.')
param identityId string
param principalId string

var foundryUserRole = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: accountName

  resource project 'projects' existing = {
    name: projectName
  }
}

resource foundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account::project.id, identityId, foundryUserRole)
  scope: account::project
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRole)
  }
}
