targetScope = 'resourceGroup'

param location string = 'eastus2'
@description('Search region; can differ from the model region when Search has capacity constraints.')
param searchLocation string = location
@description('Object ID of the backend/presenter signed in through Azure CLI, not the second test user.')
param principalId string
param accountName string = 'permissions-${uniqueString(resourceGroup().id)}'
param searchName string = 'permissions-${uniqueString(resourceGroup().id)}'
param deploymentName string = 'gpt-4.1-mini'
@minValue(1)
param capacity int = 10

var securityTags = {
  SecurityControl: 'Ignore'
  SecurityExemption: 'StorageAccountKeyAccess'
}

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  tags: securityTags
  name: accountName
  location: location
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: accountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource model 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  tags: securityTags
  parent: account
  name: deploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: capacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4.1-mini'
      version: '2025-04-14'
    }
  }
}

resource search 'Microsoft.Search/searchServices@2025-05-01' = {
  tags: securityTags
  name: searchName
  location: searchLocation
  sku: {
    name: 'basic'
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'Default'
    publicNetworkAccess: 'enabled'
    disableLocalAuth: true
  }
}

var inferenceRole = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
resource presenterInference 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, inferenceRole)
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', inferenceRole)
    principalId: principalId
    principalType: 'User'
  }
}

var searchRoles = [
  '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
  '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
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

output endpoint string = 'https://${account.name}.openai.azure.com/'
output deployment string = model.name
output searchEndpoint string = 'https://${search.name}.search.windows.net'
