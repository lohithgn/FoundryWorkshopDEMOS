# Microsoft Foundry - Agent with Simple Tools

A 3-5 minute terminal demo: ask an agent for the workshop price for 12 attendees.
The agent discovers a tool through **Foundry Toolbox**, calls a deployed
**Azure Function**, and explains the returned price: USD 25 per attendee,
USD 300 total. The script prints the real tool result before the answer.

> **Migration transition:** Use the .NET 10 commands below. Original Python files
> and requirements are temporarily retained unchanged for the root runner while
> issue #1 integrates the lab conversions. Historical live results below are from
> the Python version and are not .NET live validation.

## What runs where

- **Bicep:** a Foundry account/project, `gpt-4.1-mini` Global Standard deployment
  (`2025-04-14`), presenter Foundry User role, and a Windows Function App on a
  single-instance **P0v3 (PremiumV3) dedicated App Service plan** with storage
  and Always On in **West US 2**. Foundry remains in **East US 2**.
  The function's JavaScript is deployed inline by Bicep.
  No zip upload, external code package, or manual portal editing is needed.
- **Foundry Tools:** C# registers the function's OpenAPI contract in a
  temporary toolbox version. Foundry exposes it as an authenticated MCP endpoint.
- **Local .NET:** the app uses the MCP endpoint to discover and call the tool,
  with model inference in Foundry. The agent itself is not hosted.
  The toolbox version is deleted in `finally`; the Azure resources remain.

This is a read-only, toy pricing tool: no payment, booking, database, or local
function-call substitute. The Function API is intentionally **anonymous and
public** so Foundry can call it without extra connection/secrets plumbing.
It contains no private data or write operations. Do not reuse this authentication
design for private or privileged APIs. The Function uses its system-assigned
managed identity and storage-scoped Storage Blob Data Owner role for host
storage; shared-key storage access and Foundry API keys are disabled.
Dedicated hosting does not require the Consumption plan's Azure Files content
connection string, so no storage keys are read or placed in app settings.

## Prerequisites

The .NET 10 SDK, Azure CLI with Bicep, an interactive Azure user, model access/quota
in **East US 2**, and resource-creation plus role-assignment permissions.
Contributor alone cannot assign roles. Foundry Toolbox is a preview service; the
C# app uses its native REST and MCP contracts. Python requirements are retained
temporarily only for the staged root-runner migration.
No local Node.js is needed to run the demo; Node.js is optional for tool tests.

Public endpoints, Global Standard inference, and storage incur charges and are
workshop rather than production defaults. The dedicated P0v3 plan is billed
continuously while provisioned, even when idle or the Function App is stopped.
Delete the dedicated resource group after the workshop to stop these charges.
P0v3 in West US 2 passed actual deployment preflight. East US 2 rejected Y1,
S1, P0v3, P1v3, and P0v4 with effective zero quota, even though its quota-list
API reported P0v3 capacity. Validate deployment, not just the quota listing.

## Deploy

From this folder in Bash/Zsh:

Alternatively, the lab-local .NET deploy helper creates billable resources, runs
the offline tests, and performs a live toolbox/tool call. Use only with separate
authorization; `--help` is safe and performs no Azure calls.

```bash
dotnet run --file deploy_lab.cs -- --help
dotnet run --file deploy_lab.cs -- --subscription YOUR_SUBSCRIPTION_ID --location eastus2
dotnet run --file cleanup.cs -- --help
dotnet run --file cleanup.cs
```

Cleanup prompts for the full recorded resource-group name; use `--yes` only for
separately authorized unattended cleanup.

```bash
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
az provider register --namespace Microsoft.Web --wait
az provider register --namespace Microsoft.Storage --wait
RESOURCE_GROUP="rg-foundry-agent-tools-demo"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group "$RESOURCE_GROUP" \
  --name agent-tools-demo --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location=eastus2

export FOUNDRY_PROJECT_ENDPOINT="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" --name agent-tools-demo \
  --query properties.outputs.projectEndpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" --name agent-tools-demo \
  --query properties.outputs.deployment.value -o tsv)"
export WORKSHOP_TOOL_ENDPOINT="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" --name agent-tools-demo \
  --query properties.outputs.toolEndpoint.value -o tsv)"
```

If Graph access is blocked, supply your user's object ID directly. Model
name/version, deployment name, and capacity are parameters if quota or access
requires a different tool-capable model. The `functionLocation` parameter
defaults to `westus2` independently of the Foundry `location`. Function App,
App Service plan, and storage share that region. To change it, choose a region
with actual P0v3 quota and use a new resource group: an existing storage account
cannot be moved to a different region in place.

## Run

```bash
# Optional: verify the deployed function independently.
curl --fail-with-body "$WORKSHOP_TOOL_ENDPOINT/workshop-price?attendees=12"

dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet run --file demo.cs
dotnet run --file demo.cs -- --attendees 20
```

The three endpoint/deployment arguments can replace the environment variables.
Valid attendee counts are 1-999. The [root runner](../README.md) also supports
this demo and passes both endpoint outputs automatically.

### Presenter talk track

1. Show the tiny pricing function and its OpenAPI `getWorkshopPrice` operation.
2. Show the toolbox registration and the versioned MCP endpoint in C#.
3. Run it: show `Tool result`, the source marker `workshop-price-tool-v1`, and
   the agent answer. For 20 attendees the tool returns USD 500.
4. Explain that the business rule runs in Azure, not in the model or local Python;
   the toolbox provides a reusable, managed tool-discovery surface.

## Validate and troubleshoot

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
dotnet restore test_demo.cs
dotnet build test_demo.cs --no-restore
dotnet run --file test_demo.cs
dotnet restore deploy_lab.cs
dotnet build deploy_lab.cs --no-restore
dotnet run --file deploy_lab.cs -- --help
dotnet restore cleanup.cs
dotnet build cleanup.cs --no-restore
dotnet run --file cleanup.cs -- --help
```

Tests use offline Foundry/MCP fixtures. When Node.js is installed, they also execute the
actual deployed JavaScript source for valid counts and invalid input boundaries;
otherwise that one test is explicitly skipped.
An infrastructure contract test compiles Bicep locally and checks P0v3, the
separate Function region, Always On, and keyless storage; it is explicitly
skipped when Azure CLI is unavailable.

### Live verification on 2026-10-26

Deployed to `rg-foundry-agent-tools-demo`: Foundry in East US 2, and Windows
Function hosting/storage in West US 2 on one P0v3 instance. The Function is
running with Always On and managed-identity host storage.

- Direct API calls returned correct totals for 1, 12, 20, and 999 attendees.
- Counts `0`, `1000`, `1.5`, and `abc` returned HTTP 400 with explicit errors.
- Both real Function -> Foundry Toolbox -> agent runs returned the source
  marker and correct prices: 12 attendees = USD 300; 20 attendees = USD 500.
- Both temporary toolbox versions were deleted after their runs; independent
  Azure readback confirmed HTTP 404 for each version.
- All six unit tests, including the compiled infrastructure contract, and Bicep
  lint passed.

Deployment outputs, direct API results, and agent transcripts are retained
locally under ignored `.azure/`. Resources remain deployed and the dedicated
plan continues billing until deleted.

For 403, allow RBAC propagation and check Toolbox access. For a failed tool call,
try the direct `curl` above; the Function host may need a short cold start after
deployment. Confirm the `/api` suffix in `toolEndpoint`. For 404/429 on inference,
check model name, region, and quota. Missing tool results or the source marker
cause an explicit failure, not a model-only fallback. If version cleanup fails,
the error is surfaced; remove the printed toolbox version or delete the group.

## Cleanup

The temporary toolbox version is removed automatically. Delete all persistent
infrastructure only by deleting the dedicated resource group:

```bash
az group delete --name "$RESOURCE_GROUP" --yes
```

- [OpenAPI tools and FoundryToolbox integration](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/openapi?pivots=python)
- [Foundry Toolbox](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/toolbox)
- [Inline Function deployment pattern](https://github.com/Azure/azure-quickstart-templates/tree/master/quickstarts/microsoft.web/function-http-trigger)
- [Dedicated hosting plans and Always On](https://learn.microsoft.com/azure/azure-functions/dedicated-plan)
- [Feature F004](../BACKLOG.md#f004---microsoft-foundry-agent-with-simple-tools)
