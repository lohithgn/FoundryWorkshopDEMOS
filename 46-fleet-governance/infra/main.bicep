targetScope = 'resourceGroup'

param location string
param principalId string
param accountName string = 'fleetgov-${uniqueString(resourceGroup().id)}'
param deploymentName string = 'gpt-5.4-mini'

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
  properties: { model: { format: 'OpenAI', name: 'gpt-5.4-mini', version: '2026-03-17' } }
}
resource operations 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'fleet-operations'
  location: location
  dependsOn: [model]
  identity: { type: 'SystemAssigned' }
  properties: { displayName: '46 - Synthetic fleet operations' }
}
resource audit 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'fleet-audit'
  location: location
  dependsOn: [operations]
  identity: { type: 'SystemAssigned' }
  properties: { displayName: '46 - Synthetic fleet audit' }
}
resource presenter 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, 'foundry-user')
  scope: account
  properties: {
    principalId: principalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
  }
}
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  tags: securityTags
  name: '${accountName}-logs'
  location: location
  properties: { retentionInDays: 30, sku: { name: 'PerGB2018' } }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  tags: securityTags
  name: '${accountName}-insights'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
  }
}
var identities = [principalId, operations.identity.principalId, audit.identity.principalId]
var identityResources = [principalId, operations.id, audit.id]
resource projectModelUsers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(1, 2): {
  name: guid(account.id, identityResources[i], 'model-user')
  scope: account
  properties: {
    principalId: identities[i]
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
  }
}]
resource publishers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, 3): {
  name: guid(insights.id, identityResources[i], 'publisher')
  scope: insights
  properties: {
    principalId: identities[i]
    principalType: i == 0 ? 'User' : 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}]
resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, 3): {
  name: guid(insights.id, identityResources[i], 'reader')
  scope: insights
  properties: {
    principalId: identities[i]
    principalType: i == 0 ? 'User' : 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
  }
}]
resource logReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, 3): {
  name: guid(workspace.id, identityResources[i], 'log-reader')
  scope: workspace
  properties: {
    principalId: identities[i]
    principalType: i == 0 ? 'User' : 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '73c42c96-874c-492b-b04d-ab87d138a893')
  }
}]
resource operationsConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01' = {
  parent: operations
  name: 'application-insights'
  dependsOn: [audit, publishers, readers, logReaders]
  properties: {
    category: 'AppInsights'
    authType: 'ProjectManagedIdentity'
    target: insights.id
    isSharedToAll: false
    metadata: {
      ResourceId: insights.id
      ApplicationInsightsConnectionString: insights.properties.ConnectionString
    }
  }
}
resource auditConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01' = {
  parent: audit
  name: 'fleet-audit-insights'
  dependsOn: [operationsConnection]
  properties: {
    category: 'AppInsights'
    authType: 'ProjectManagedIdentity'
    target: insights.id
    isSharedToAll: false
    metadata: {
      ResourceId: insights.id
      ApplicationInsightsConnectionString: insights.properties.ConnectionString
    }
  }
}
resource policy 'Microsoft.Authorization/policyAssignments@2024-04-01' = {
  name: 'fleet-keyless-audit'
  properties: {
    displayName: '46 - Isolated fleet keyless audit'
    policyDefinitionId: '/providers/Microsoft.Authorization/policyDefinitions/71ef260a-8f18-47b7-abcb-62d0673d94dc'
    enforcementMode: 'Default'
    parameters: { effect: { value: 'Audit' } }
  }
}
output endpoint string = 'https://${account.name}.openai.azure.com/'
output deployment string = model.name
output projectEndpoint string = 'https://${account.name}.services.ai.azure.com/api/projects/${operations.name}'
output projectResourceId string = operations.id
output secondProjectEndpoint string = 'https://${account.name}.services.ai.azure.com/api/projects/${audit.name}'
output secondProjectResourceId string = audit.id
output applicationInsightsResourceId string = insights.id
output workspaceId string = workspace.properties.customerId
output policyAssignmentId string = policy.id
output tenantId string = tenant().tenantId
