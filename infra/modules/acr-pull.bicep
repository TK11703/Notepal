// Grants AcrPull on an existing registry. Deployed at the registry's resource group scope.
type pullPrincipal = {
  @description('Resource id of the identity; keeps the role assignment name stable.')
  identityId: string
  principalId: string
}

param registryName string
param principals pullPrincipal[]

var acrPullRole = '7f951dda-4ed3-4ba8-8aa3-0b5a6f6e5ba5'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for p in principals: {
    name: guid(registry.id, p.identityId, acrPullRole)
    scope: registry
    properties: {
      principalId: p.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
    }
  }
]
