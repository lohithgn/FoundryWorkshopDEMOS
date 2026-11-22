# Microsoft Agent Framework - What is Microsoft Entra Agent ID?

A tiny workshop tutor calls Foundry **as its own Entra Agent ID**, not as the
presenter. Show three things:

1. A **blueprint** is the parent template and holds the credential.
2. Its **child agent identity** is a special service principal with a sponsor.
   Its object ID and client ID are the same.
3. Bicep grants that child identity **Cognitive Services OpenAI User** on just
   this demo's Foundry account. The Agent Framework tutor uses its token.

This is a real Agent ID token exchange, not a renamed ordinary app registration.
There is no user delegation, Graph data access, agent user account, or hosted app.

**Recorded status: blocked on interactive tenant identity setup/credentials.**
Offline token-flow and Graph mocks do not establish a successful Agent ID login
or live inference. Complete the tenant setup below before attempting a live run;
see [recorded test results](../tests.md).

The runnable .NET 10 file-based counterparts are `demo.cs`, `deploy_lab.cs`,
`cleanup.cs`, and `test_demo.cs`. The original Python files remain unchanged for
the staged migration; the PowerShell identity setup and Bicep template are shared.
**Transitional Python documentation:** Python-only commands below are retained
for the repository-wide runner and do not validate the .NET apps.

## Prerequisites

- .NET 10 SDK, Azure CLI with Bicep, and **PowerShell 7** (`pwsh`).
- Python 3.10+ is needed only for the retained Python entry points.
- A tenant with Entra Agent ID enabled and permission to create blueprints and
  agents, for example **Agent ID Developer**.
- An administrator able to consent to the explicit Graph delegated scopes in
  the setup script. Azure subscription Owner alone does not grant tenant rights.
- Azure resource creation/role-assignment permissions and `gpt-4.1-mini` quota
  in East US 2 (the template default), version `2025-04-14`, Global Standard.
  Global Standard can process data outside the account region; use synthetic
  prompts and confirm model/region access for your subscription.

The setup uses the supported Graph Agent ID APIs, including a beta endpoint for
child identity creation. Preview support/licensing and tenant policies may vary.
**Do not use Azure CLI tokens for Agent ID provisioning:** their Graph scopes can
be rejected by these APIs. The script uses `Connect-MgGraph` with explicit scopes.

## 1. Prepare the tenant identity (PowerShell)

From this demo folder, start an interactive PowerShell session and remain in it:

```powershell
pwsh
# Inside PowerShell:
Install-Module Microsoft.Graph.Authentication -Scope CurrentUser
./setup_identity.ps1 -TenantId "<tenant-id>"
```

The script signs in, uses the current user as owner/sponsor, creates the typed
blueprint, configures its identifier URI/scope, creates its blueprint principal,
creates the child agent, and adds a **one-day development-only blueprint secret**.
It sets these environment variables in the current PowerShell process:

- `ENTRA_TENANT_ID`
- `ENTRA_BLUEPRINT_CLIENT_ID`
- `ENTRA_AGENT_ID`
- `ENTRA_BLUEPRINT_SECRET`

Only non-secret IDs are saved to `.azure/identity.json`, including partial progress
for cleanup on failure. The secret/token is never printed or written to a file.
Do not enable PowerShell tracing/transcription or HTTP debug logging.
Run subsequent commands in this same session so child processes inherit
the variables. Rerunning setup with existing state fails rather than creating
duplicate identities.

Client secrets are for **local development only**. Production should use the
recommended identity SDK and managed identity federation or certificates with
proper token caching/refresh. The demos deliberately expose Learn's two short
token requests for teaching; each acquires tokens for one short run only.

## 2. Deploy Azure infrastructure (same PowerShell session)

```powershell
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
$group = "rg-foundry-entra-agent-id-demo"
$presenter = az ad signed-in-user show --query id -o tsv
az group create --name $group --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create -g $group -n demo --template-file infra/main.bicep `
  --parameters "principalId=$presenter" "agentIdentityId=$env:ENTRA_AGENT_ID" location=eastus2
$env:AZURE_OPENAI_ENDPOINT = az deployment group show -g $group -n demo `
  --query properties.outputs.endpoint.value -o tsv
$env:AZURE_OPENAI_DEPLOYMENT = az deployment group show -g $group -n demo `
  --query properties.outputs.deployment.value -o tsv
```

Bicep creates a keyless Foundry account, `gpt-4.1-mini` deployment (`2025-04-14`,
Global Standard), account-scoped inference RBAC for the **child agent**, and Reader
for the presenter. Tenant objects aren't ARM resources, so their separate setup
is intentional; Bicep doesn't pretend to create them.

## 3. Run and present

From this directory, build and run the standalone .NET demo:

```bash
dotnet restore demo.cs
dotnet build demo.cs
dotnet run --file demo.cs --
```

The C# entry point performs the same blueprint-to-child two-stage token exchange
and sends the child token as a bearer credential to the account-scoped Foundry
chat-completions endpoint. It does not fall back to the presenter identity or
account keys. The Python entry point remains available during migration:

```powershell
python3 -m venv .venv
./.venv/bin/python -m pip install -r requirements.txt
./.venv/bin/python demo.py
```

The demo prints the blueprint and agent IDs, requests a blueprint exchange token
using `fmi_path`, exchanges it for the **child's** Cognitive Services token, and
passes that token to the model endpoint. Python uses the Agent Framework OpenAI
client; C# sends the equivalent single-turn chat-completions request directly.
Foundry authorizes the request using the agent's account-scoped role. Raw tokens
are not displayed.
Open **Entra ID -> Agents** in the admin center to show the real blueprint,
child identity, sponsor, and sign-in activity (logs may take time to appear).

For a simple authorization contrast, inspect the role assignment: Reader alone
doesn't grant inference. This script never retries as the user or falls back to
an account key when the agent's request fails.

## Validate and troubleshoot

Offline tests use mock HTTP responses and, when `pwsh` is installed, mock Graph
commands; they do not contact Azure or make live model requests:

```bash
dotnet restore test_demo.cs
dotnet build test_demo.cs
dotnet run --file test_demo.cs
```

The equivalent retained Python checks and Bicep validation are:

```powershell
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
./.venv/bin/python -m unittest discover -s . -p 'test_*.py' -v
```

The .NET tests cover the two-stage token flow, credential errors, request
authentication, framework-equivalent tutor request, CLI validation, and a mocked
execution of the PowerShell Graph setup. No tenant changes are made by tests.
Allow time for both Graph consent and Azure RBAC propagation.
403 at provisioning means check tenant scopes/roles; 403 at inference means check
the **agent's** role. After leaving the session, reuse the IDs and configure a new
blueprint credential securely; the script cannot recover the old secret.

## Automated .NET lifecycle (optional)

Instead of the manual Azure deployment above, the .NET lifecycle helper runs the
local tests, deploys this lab's Bicep template, and then runs the live tutor. It
creates billable Azure resources and sends a live model request. From this
directory, use:

```powershell
dotnet restore deploy_lab.cs
dotnet build deploy_lab.cs
dotnet run --file deploy_lab.cs -- --help
dotnet run --file deploy_lab.cs -- --location eastus2
```

`--subscription`, `--prefix`, and `--principal-id` are also supported. Deployment
requires the identity environment variables created above and matching
`.azure/identity.json`. Do not run deployment unless Azure changes and live
inference are authorized.

## Cleanup (both Azure and tenant)

Deleting the resource group does **not** delete tenant identities. In the same
authenticated PowerShell session, an authorized blueprint owner/admin can run
the standalone helper:

```powershell
dotnet restore cleanup.cs
dotnet build cleanup.cs
dotnet run --file cleanup.cs -- --help
dotnet run --file cleanup.cs --
```

The helper prompts for the exact resource group name and blueprint object ID.
Pass `--yes` only for intentional unattended cleanup. `--azure-only` explicitly
deletes only the recorded Azure resource group, retaining tenant identities.
Cleanup requires Azure CLI for the group and `pwsh` plus Microsoft Graph
authorization for tenant objects. The previous manual cleanup procedure is:

```powershell
az group delete --name $group --yes
$ids = Get-Content .azure/identity.json -Raw | ConvertFrom-Json
# Only delete the objects whose IDs were recorded for this demo:
if ($ids.agentIdentityId) {
  Invoke-MgGraphRequest -Method DELETE `
    -Uri "https://graph.microsoft.com/beta/servicePrincipals/$($ids.agentIdentityId)"
}
if ($ids.blueprintPrincipalId) {
  Invoke-MgGraphRequest -Method DELETE `
    -Uri "https://graph.microsoft.com/v1.0/servicePrincipals/$($ids.blueprintPrincipalId)"
}
Invoke-MgGraphRequest -Method DELETE `
  -Uri "https://graph.microsoft.com/v1.0/applications/$($ids.blueprintObjectId)"
Remove-Item Env:ENTRA_BLUEPRINT_SECRET
```

If deletion requires additional tenant consent/roles, have the administrator
delete these recorded objects in the Entra admin center. After confirming cleanup,
remove only `.azure/identity.json` to permit a fresh setup. The one-day credential
must also be revoked if the blueprint is retained. Azure inference is billed;
the setup does not grant Graph application permissions.

- [Create the blueprint and credentials](https://learn.microsoft.com/entra/agent-id/create-blueprint)
- [Create child agent identities](https://learn.microsoft.com/entra/agent-id/create-delete-agent-identities)
- [Official autonomous token flow](https://learn.microsoft.com/entra/agent-id/autonomous-agent-authentication-authorization-flow)
- [Feature F012](../BACKLOG.md#f012---microsoft-agent-framework-entra-agent-id)
