// Grants Foundry User (formerly Azure AI User) on an existing Foundry account. Deployed at the account's resource group scope.
param accountName string

@description('Resource id of the identity; keeps the role assignment name stable.')
param identityId string
param principalId string

var foundryUserRole = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: accountName
}

resource foundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, identityId, foundryUserRole)
  scope: account
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRole)
  }
}
