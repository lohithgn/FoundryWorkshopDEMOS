targetScope = 'resourceGroup'

@allowed(['australiaeast', 'canadacentral', 'eastus', 'eastus2', 'japaneast', 'southcentralus', 'southeastasia', 'swedencentral', 'uksouth', 'westeurope', 'westus', 'westus3'])
param location string = 'eastus2'
param principalId string
param accountName string = 'foundry-cu-${uniqueString(resourceGroup().id)}'
param projectName string = 'content-understanding'
param deploymentName string = 'cu-ga-completion'
param completionModel string = 'gpt-5.2'
param completionVersion string = '2025-12-11'
param embeddingDeploymentName string = 'cu-embedding'
param embeddingModel string = 'text-embedding-3-large'
param embeddingVersion string = '1'
@minValue(1)
@maxValue(10)
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
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: accountName
    allowProjectManagement: true
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource completion 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  tags: securityTags
  parent: account
  name: deploymentName
  sku: { name: 'GlobalStandard', capacity: capacity }
  properties: {
    model: { format: 'OpenAI', name: completionModel, version: completionVersion }
  }
}

resource embedding 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  tags: securityTags
  parent: account
  name: embeddingDeploymentName
  sku: { name: 'GlobalStandard', capacity: capacity }
  properties: {
    model: { format: 'OpenAI', name: embeddingModel, version: embeddingVersion }
  }
  dependsOn: [completion]
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  tags: securityTags
  parent: account
  name: projectName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    displayName: 'GA Content Understanding custom analyzers'
    description: 'Grounded schema extraction of synthetic document and audio.'
  }
  dependsOn: [embedding]
}

var userRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
resource presenterRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, userRoleId)
  scope: account
  properties: {
    roleDefinitionId: userRoleId
    principalId: principalId
    principalType: 'User'
  }
}

var inferenceRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
resource inferenceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, account.id, inferenceRoleId)
  scope: account
  properties: {
    roleDefinitionId: inferenceRoleId
    principalId: account.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output endpoint string = 'https://${accountName}.services.ai.azure.com/'
output projectEndpoint string = 'https://${accountName}.services.ai.azure.com/api/projects/${projectName}'
output deployment string = completion.name
output embeddingDeployment string = embedding.name
output completionModelName string = completionModel
output embeddingModelName string = embeddingModel
output projectResourceId string = project.id
output resourceLocation string = location
