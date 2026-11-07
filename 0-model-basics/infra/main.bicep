targetScope = 'resourceGroup'

param location string = 'eastus2'
param principalId string
param accountName string = 'basics-${uniqueString(resourceGroup().id)}'
@allowed(['User', 'ServicePrincipal', 'Group'])
param principalType string = 'User'
@minValue(1)
param capacity int = 10
@minValue(1)
param batchCapacity int = 10

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
  properties: {
    customSubDomainName: accountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource chat 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'basics-chat'
  sku: { name: 'GlobalStandard', capacity: capacity }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-4.1-mini', version: '2025-04-14' }
  }
}

resource reasoning 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'basics-reasoning'
  sku: { name: 'GlobalStandard', capacity: capacity }
  properties: {
    model: { format: 'OpenAI', name: 'o4-mini', version: '2025-04-16' }
  }
  dependsOn: [chat]
}

resource batch 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'basics-batch'
  sku: { name: 'GlobalBatch', capacity: batchCapacity }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-4.1-mini', version: '2025-04-14' }
  }
  dependsOn: [reasoning]
}

var roleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
resource inferenceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, roleId)
  scope: account
  properties: {
    roleDefinitionId: roleId
    principalId: principalId
    principalType: principalType
  }
}

output endpoint string = 'https://${account.name}.openai.azure.com/'
output deployment string = chat.name
output reasoningDeployment string = reasoning.name
output batchDeployment string = batch.name
