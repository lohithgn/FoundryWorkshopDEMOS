targetScope = 'resourceGroup'

param location string
param principalId string
param searchLocation string = 'westus2'
param accountName string = 'governediq-${uniqueString(resourceGroup().id)}'
param storageName string = 'governediq${uniqueString(resourceGroup().id)}'
param searchName string = 'governediq-${uniqueString(resourceGroup().id)}'
param deploymentName string = 'gpt-4.1-mini'

var securityTags = {
  SecurityControl: 'Ignore'
  SecurityExemption: 'StorageAccountKeyAccess'
}
resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  tags: securityTags
  name: accountName
  location: location
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: accountName
    allowProjectManagement: true
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}
resource model 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  tags: securityTags
  parent: account
  name: deploymentName
  sku: { name: 'GlobalStandard', capacity: 5 }
  properties: { model: { format: 'OpenAI', name: 'gpt-4.1-mini', version: '2025-04-14' } }
}
resource embedding 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'text-embedding-3-small'
  dependsOn: [model]
  sku: { name: 'Standard', capacity: 1 }
  properties: { model: { format: 'OpenAI', name: 'text-embedding-3-small', version: '1' } }
}
resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'governed-iq'
  location: location
  dependsOn: [embedding]
  identity: { type: 'SystemAssigned' }
  properties: { displayName: '45 - Permission-native Foundry IQ and Fabric IQ' }
}
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  tags: securityTags
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}
resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'synthetic-restricted'
  properties: { publicAccess: 'None' }
}
resource search 'Microsoft.Search/searchServices@2025-05-01' = {
  tags: securityTags
  name: searchName
  location: searchLocation
  sku: { name: 'basic' }
  identity: { type: 'SystemAssigned' }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'Default'
    disableLocalAuth: true
    publicNetworkAccess: 'enabled'
    semanticSearch: 'free'
  }
}
var searchRoles = [
  '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
  '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
  '1407120a-92aa-4202-b7e9-c0e197c71c8f'
]
resource presenterSearch 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in searchRoles: {
  name: guid(search.id, principalId, role)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    principalId: principalId
    principalType: 'User'
  }
}]
resource presenterStorage 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(container.id, principalId, 'contributor')
  scope: container
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: principalId
    principalType: 'User'
  }
}
resource presenterStorageReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, principalId, 'reader')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
    principalId: principalId
    principalType: 'User'
  }
}
resource searchStorage 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, search.id, 'reader')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
    principalId: search.identity.principalId
    principalType: 'ServicePrincipal'
  }
}
resource searchModels 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, search.id, 'model-user')
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalId: search.identity.principalId
    principalType: 'ServicePrincipal'
  }
}
resource presenterFoundry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, 'foundry-user')
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
    principalId: principalId
    principalType: 'User'
  }
}
output endpoint string = 'https://${account.name}.openai.azure.com/'
output projectEndpoint string = 'https://${account.name}.services.ai.azure.com/api/projects/${project.name}'
output deployment string = model.name
output projectResourceId string = project.id
output embeddingDeployment string = embedding.name
output storageEndpoint string = storage.properties.primaryEndpoints.blob
output storageResourceId string = storage.id
output containerResourceId string = container.id
output searchEndpoint string = 'https://${search.name}.search.windows.net'
output searchResourceId string = search.id
output tenantId string = tenant().tenantId
output principalId string = principalId
