targetScope = 'resourceGroup'

@description('Use a region supporting Browser Automation AND the selected model. Remote MCP preview access is still required.')
@allowed([
  'australiaeast'
  'eastasia'
  'eastus'
  'japaneast'
  'switzerlandnorth'
  'westeurope'
  'westus3'
])
param location string = 'eastus'
@description('Object ID of the presenter signed in through Azure CLI.')
param principalId string
param accountName string = 'foundry-browser-${uniqueString(resourceGroup().id)}'
param projectName string = 'browser-runbook'
param deploymentName string = 'gpt-4.1-mini'
param modelName string = 'gpt-4.1-mini'
param modelVersion string = '2025-04-14'
@minValue(1)
param capacity int = 10
@description('Dedicated Function hosting region; independent of model capacity.')
param functionLocation string = location

var securityTags = {
  SecurityControl: 'Ignore'
  SecurityExemption: 'StorageAccountKeyAccess'
}
var functionName = 'browser-site-${uniqueString(resourceGroup().id)}'
var siteBase = 'https://${functionName}.azurewebsites.net/api/runbook'

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
  sku: { name: 'GlobalStandard', capacity: capacity }
  properties: {
    model: { format: 'OpenAI', name: modelName, version: modelVersion }
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  tags: securityTags
  parent: account
  name: projectName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    displayName: 'Synthetic browser runbook'
    description: 'Managed remote browser, local skill injection, exact-action approval.'
  }
  dependsOn: [model]
}

var foundryUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
resource presenterRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, principalId, foundryUser)
  scope: account
  properties: {
    roleDefinitionId: foundryUser
    principalId: principalId
    principalType: 'User'
  }
}

resource workspace 'Microsoft.LoadTestService/playwrightWorkspaces@2025-07-01-preview' = {
  tags: securityTags
  name: 'browser-${uniqueString(resourceGroup().id)}'
  location: location
  properties: {
    localAuth: 'Disabled'
    regionalAffinity: 'Enabled'
  }
}

var playwrightContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '78cf819f-0969-4ebe-8759-015c6efcd5bf')
resource browserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(workspace.id, project.id, playwrightContributor)
  scope: workspace
  properties: {
    roleDefinitionId: playwrightContributor
    principalId: project.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  tags: securityTags
  name: 'runbook${uniqueString(resourceGroup().id)}'
  location: functionLocation
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' = {
  parent: storage
  name: 'default'
}
resource runs 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobs
  name: 'runs'
  properties: { publicAccess: 'None' }
}
resource expiry 'Microsoft.Storage/storageAccounts/managementPolicies@2025-01-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'expire-workshop-runs'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: ['blockBlob'], prefixMatch: ['runs/'] }
            actions: { baseBlob: { delete: { daysAfterModificationGreaterThan: 1 } } }
          }
        }
      ]
    }
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-11-01' = {
  tags: securityTags
  name: 'browser-fixture-plan'
  location: functionLocation
  sku: { name: 'B1', tier: 'Basic', capacity: 1 }
}
resource app 'Microsoft.Web/sites@2024-11-01' = {
  tags: securityTags
  name: functionName
  location: functionLocation
  kind: 'functionapp'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'node' }
        { name: 'WEBSITE_NODE_DEFAULT_VERSION', value: '~22' }
        { name: 'RUN_STORAGE', value: storage.name }
        { name: 'SITE_BASE', value: siteBase }
      ]
    }
  }
}

var blobOwner = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
resource storageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, app.id, blobOwner)
  scope: storage
  properties: {
    roleDefinitionId: blobOwner
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource fixture 'Microsoft.Web/sites/functions@2024-11-01' = {
  parent: app
  name: 'runbook'
  properties: {
    config: {
      scriptFile: 'index.js'
      bindings: [
        {
          name: 'req'
          type: 'httpTrigger'
          direction: 'in'
          authLevel: 'anonymous'
          route: 'runbook/{*rest}'
          methods: ['get', 'put', 'post', 'delete']
        }
        { name: 'res', type: 'http', direction: 'out' }
      ]
    }
    files: { 'index.js': loadTextContent('../fixture/index.js') }
  }
  dependsOn: [storageRole, runs]
}

output endpoint string = 'https://${account.name}.openai.azure.com/'
output deployment string = model.name
output projectEndpoint string = 'https://${account.name}.services.ai.azure.com/api/projects/${project.name}'
output toolEndpoint string = siteBase
output projectResourceId string = project.id
output playwrightWorkspaceResourceId string = workspace.id
output playwrightMcpEndpoint string = 'https://${location}.mcp.playwright.microsoft.com/playwrightworkspaces/${workspace.properties.workspaceId}/mcp'
output browserConnectionPrerequisite string = 'Create a Browser Automation connection in this project using project managed identity, then set BROWSER_AUTOMATION_PROJECT_CONNECTION_ID. Workspace provisioning alone does not enable the remote MCP preview.'
