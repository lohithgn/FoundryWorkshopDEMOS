targetScope = 'resourceGroup'

param location string = 'eastus2'
param principalId string
param accountName string = 'guardrails-${uniqueString(resourceGroup().id)}'

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
    allowProjectManagement: true
  }
}

resource blocklist 'Microsoft.CognitiveServices/accounts/raiBlocklists@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'workshop-synthetic-marker'
  properties: { description: 'Benign synthetic workshop marker; no unsafe content.' }
}

resource item 'Microsoft.CognitiveServices/accounts/raiBlocklists/raiBlocklistItems@2025-06-01' = {
  tags: securityTags
  parent: blocklist
  name: 'synthetic-marker'
  properties: { pattern: 'WORKSHOP_RESTRICTED_ORCHID', isRegex: false }
}

resource policy 'Microsoft.CognitiveServices/accounts/raiPolicies@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'workshop-native-guardrail'
  properties: {
    basePolicyName: 'Microsoft.Default'
    mode: 'Default'
    contentFilters: [
      { name: 'Hate', source: 'Prompt', enabled: true, blocking: true, severityThreshold: 'Low' }
      { name: 'Violence', source: 'Prompt', enabled: true, blocking: true, severityThreshold: 'Low' }
      { name: 'Sexual', source: 'Prompt', enabled: true, blocking: true, severityThreshold: 'Low' }
      { name: 'Selfharm', source: 'Prompt', enabled: true, blocking: true, severityThreshold: 'Low' }
      { name: 'Hate', source: 'Completion', enabled: true, blocking: true, severityThreshold: 'Low' }
      { name: 'Violence', source: 'Completion', enabled: true, blocking: true, severityThreshold: 'Low' }
      { name: 'Sexual', source: 'Completion', enabled: true, blocking: true, severityThreshold: 'Low' }
      { name: 'Selfharm', source: 'Completion', enabled: true, blocking: true, severityThreshold: 'Low' }
    ]
    customBlocklists: [
      { blocklistName: blocklist.name, source: 'Prompt', blocking: true }
    ]
  }
  dependsOn: [item]
}

resource model 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'gpt-4.1-mini'
  sku: { name: 'GlobalStandard', capacity: 3 }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-4.1-mini', version: '2025-04-14' }
    raiPolicyName: policy.name
  }
}

var inferenceRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
resource agentModel 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'agent-baseline'
  sku: { name: 'GlobalStandard', capacity: 5 }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-4.1-mini', version: '2025-04-14' }
    raiPolicyName: 'Microsoft.Default'
  }
  dependsOn: [model]
}
resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'native-guardrails'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: { displayName: 'Native agent guardrails' }
  dependsOn: [agentModel]
}
var projectRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
resource agentAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(project.id, principalId, projectRole)
  scope: project
  properties: { principalId: principalId, principalType: 'User', roleDefinitionId: projectRole }
}
resource projectModelAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, project.id, inferenceRole)
  scope: account
  properties: { principalId: project.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: inferenceRole }
}
resource access 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, inferenceRole)
  scope: account
  properties: { principalId: principalId, principalType: 'User', roleDefinitionId: inferenceRole }
}

output endpoint string = 'https://${account.name}.openai.azure.com/'
output deployment string = model.name
output accountResourceId string = account.id
output guardrailPolicyName string = policy.name
output blocklistName string = blocklist.name
output projectEndpoint string = 'https://${account.name}.services.ai.azure.com/api/projects/${project.name}'
output agentDeployment string = agentModel.name
