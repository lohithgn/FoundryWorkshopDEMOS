targetScope = 'resourceGroup'

param location string = 'eastus2'
param principalId string
param accountName string = 'token-backend-${uniqueString(resourceGroup().id)}'
param gatewayName string = 'token-gateway-${uniqueString(resourceGroup().id)}'
param publisherEmail string = 'workshop@example.com'

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

resource model 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'gpt-4.1-mini'
  sku: { name: 'GlobalStandard', capacity: 10 }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-4.1-mini', version: '2025-04-14' }
  }
}

resource projectA 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'workshop-project-a'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: { displayName: 'Workshop Project A' }
  dependsOn: [model]
}

resource projectB 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  tags: securityTags
  parent: account
  name: 'workshop-project-b'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: { displayName: 'Workshop Project B' }
  dependsOn: [projectA]
}

resource gateway 'Microsoft.ApiManagement/service@2024-05-01' = {
  tags: securityTags
  name: gatewayName
  location: location
  sku: { name: 'Developer', capacity: 1 }
  identity: { type: 'SystemAssigned' }
  properties: {
    publisherEmail: publisherEmail
    publisherName: 'Synthetic Foundry Workshop'
  }
}

var inferenceRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
resource gatewayAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, gateway.id, inferenceRole)
  scope: account
  properties: {
    principalId: gateway.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: inferenceRole
  }
}

var routes = [
  { name: 'project-a-rate', projectId: projectA.id, kind: 'rate' }
  { name: 'project-a-quota', projectId: projectA.id, kind: 'quota' }
  { name: 'project-b-rate', projectId: projectB.id, kind: 'rate' }
  { name: 'project-b-quota', projectId: projectB.id, kind: 'quota' }
]

resource apis 'Microsoft.ApiManagement/service/apis@2024-05-01' = [for route in routes: {
  parent: gateway
  name: route.name
  properties: {
    displayName: route.name
    path: route.name
    protocols: ['https']
    subscriptionRequired: false
    serviceUrl: 'https://${account.name}.openai.azure.com'
  }
}]

resource operations 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [for (route, i) in routes: {
  parent: apis[i]
  name: 'chat'
  properties: {
    displayName: 'Bounded workshop chat'
    method: 'POST'
    urlTemplate: '/chat/completions'
    responses: []
  }
}]

var policyTemplate = loadTextContent('gateway-policy.xml')
resource policies 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = [for (route, i) in routes: {
  parent: apis[i]
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: replace(replace(replace(replace(policyTemplate,
      '__TENANT__', subscription().tenantId),
      '__PRINCIPAL__', principalId),
      '__COUNTER__', '${route.projectId}-${route.kind}'),
      '__LIMIT__', route.kind == 'rate'
        ? 'tokens-per-minute="100" remaining-tokens-header-name="X-Rate-Remaining"'
        : 'token-quota="100" token-quota-period="Hourly" remaining-quota-tokens-header-name="X-Quota-Remaining"')
  }
  dependsOn: [operations, gatewayAccess]
}]

output endpoint string = 'https://${account.name}.openai.azure.com/'
output deployment string = model.name
output gatewayEndpoint string = gateway.properties.gatewayUrl
output projectResourceId string = projectA.id
output secondProjectResourceId string = projectB.id
output gatewayResourceId string = gateway.id
