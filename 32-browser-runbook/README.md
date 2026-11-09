# 32 — Browser runbook: draft, approve, verify

A reusable skill guides an ephemeral, locally orchestrated agent through a
**real managed remote browser**. `SKILL.md` is read locally and directly injected
into the Foundry Responses request; it is **not** automatically discovered or
registered as a central skill.

```text
Foundry model → constrained host tool loop → native BrowserAutomationToolboxTool
             → Foundry Toolbox MCP endpoint → Playwright Workspace remote browser
             → this lab's synthetic Azure Function registration site
```

There is no local Playwright dependency, browser installation, fake browser log,
real attendee information, real booking, or external registration service.
The local process controls the workflow; only the browser executes remotely.
This is the native **Toolbox** integration, not a prompt-agent
`BrowserAutomationTool` example or a hosted-agent deployment.

## What runs

1. Verify the controlled fixture identity and project connection. Create a
   uniquely named native Browser Automation toolbox and discover its MCP tools.
2. Create one managed browser session. The model navigates to the session list,
   locates **Foundry Browser Lab**, opens its form, fills `Workshop Bot` /
   `bot@example.invalid`, and saves a draft.
3. The host verifies a fresh accessibility snapshot **and** the independently
   stored draft. Final model prose is never evidence.
4. The host deliberately asks its own draft-tool gate to Submit **without
   approval**. The gate rejects this proposal **before any remote tool call**:
   no Submit click or POST is sent. A second actual page snapshot and independent
   fixture-state read verify the draft is unchanged, no server submission attempt
   occurred and there is **no registration record**. This is a host-gate rejection,
   not a fabricated remote-browser rejection. Backend POST rejection is also tested offline.
5. Default execution stops without granting approval. It prints the full exact
   action and its SHA-256 digest. It retains a private pending run for up to
   30 minutes so a human can review it. No console input is requested.
6. Only a separate, explicit `--approve-action <displayed-digest>` invocation can
   grant the server's 120-second, single-use permit. The host reopens and verifies
   the actual saved form before granting it, clicks Submit once, and requires
   both a receipt-page record ID and a matching stored record. It then deletes
   the run. There is no automatic submission retry.

Approval binds the exact **site, POST path, run ID, form ID, session, name and
email**. Changing any of them invalidates approval. A fresh run has a different
digest. Atomic local file claiming and conditional Blob ETag writes prevent
concurrent/replayed execution. A rejected request alone is not reported as a
successful registration.

## Prerequisites and availability — read before deploying

- Python 3.11+, Node.js 22+ for offline fixture tests, Azure CLI/Bicep and `az login`.
- Permission to provision Cognitive Services, App Service, Storage and
  `Microsoft.LoadTestService` resources, plus permission to create role assignments.
  Register those providers if required. The deployer's resource permissions are
  separate from the presenter's runtime roles.
- Foundry model quota and access to the selected version; defaults match the
  repository's `gpt-4.1-mini`, version `2025-04-14`, Global Standard pattern.
  The model does ordinary function calling; browser execution is through Toolbox.
  Change `modelName`, `modelVersion`, `deploymentName` if availability requires it.
- **Playwright Workspaces remote MCP preview access in this subscription.**
  A successfully provisioned workspace does **not** establish this entitlement.
  The remote MCP server is preview, has no SLA, and is not recommended for production.
- Browser Automation is documented in `australiaeast`, `eastasia`, `eastus`,
  `japaneast`, `switzerlandnorth`, `westeurope`, `westus3`. The template defaults to
  `eastus`; also check current Foundry/Toolbox/model availability and regional quota.
  Workspace availability, remote MCP access and model availability are separate checks.
- A **Browser Automation project connection** using **project managed identity**,
  created manually after provisioning. This is an explicit prerequisite, not a
  pretend-success ARM connection resource. The SDK connection ID is an ARM resource
  ID, **not** the workspace ID, `wss://` browser endpoint, or remote MCP URL.

The lab was validated offline only. No Azure deployment, browser allocation or
paid model request was performed during implementation.

## Provision and connect

From this folder:

```bash
export BROWSER_AUTOMATION_CONNECTION_NAME=browser-workspace
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
.venv/bin/python demo.py --preflight
mkdir -p .azure
az login
az group create --name rg-browser-runbook --location eastus --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create \
  --resource-group rg-browser-runbook \
  --template-file infra/main.bicep \
  --parameters principalId="$(az ad signed-in-user show --query id -o tsv)" \
  --query '{outputs:properties.outputs}' -o json > .azure/deployment.json
```

The template provisions:

- Keyless Foundry account/project/model, presenter's Foundry User role.
- Playwright Workspace (`Microsoft.LoadTestService/playwrightWorkspaces@2025-07-01-preview`),
  local token authentication disabled. The project's system-assigned identity gets
  **Playwright Workspace Contributor** at workspace scope.
- A dedicated Windows B1 (Basic) Function plan with Always On, Node 22 HTTP Function with its fixture
  code deployed inline (same provisioning pattern as lab 4), private Blob container,
  keyless managed-identity storage access and a one-day Blob lifecycle deletion rule.
  There is no separate site publishing step or npm dependency.
- Standard runner outputs: `endpoint`, `deployment`, `projectEndpoint`,
  `toolEndpoint` (the fixture's `/api/runbook` base). Additional outputs:
  `projectResourceId`, `playwrightWorkspaceResourceId`, `playwrightMcpEndpoint`,
  `browserConnectionPrerequisite`.

The B1 plan avoids the Premium v3 quota requirement (`P0v3 VMs`) and uses
a lower-cost dedicated tier supported by Azure Functions. If a previous deployment
failed with `InternalSubscriptionIsOverQuotaForSku` for P0v3, rerun the deployment
command above with this template; no resource-group deletion is required.
Basic-tier quota and regional capacity still apply. If B1 is also unavailable,
request the quota indicated by Azure or set `functionLocation` to another region
with available Basic-tier capacity.

In the Foundry portal, open **this project**, create a **Browser Automation**
connection, select the provisioned Playwright Workspace and choose **project
managed identity**. The connection creator needs **Foundry Project Manager** (or
an equivalent role); activate that permission just in time. The runner only needs
Foundry User after setup. Confirm the workspace IAM assignment names the
**project** identity, not the Foundry account identity or presenter.

Create the connection with the configured name (`browser-workspace` above).
Live execution resolves that name through the deployed project's connection API.
Alternatively, copy the full connection resource ID from its details:

```bash
export BROWSER_AUTOMATION_PROJECT_CONNECTION_ID='/subscriptions/.../resourceGroups/.../providers/Microsoft.CognitiveServices/accounts/.../projects/.../connections/...'
.venv/bin/python demo.py --verify-services
```

`--preflight` is **configuration-only**: it checks that a syntactically valid
connection name or full ID is configured, does not read deployment outputs,
construct credentials or clients, or make any Azure request. No project, workspace
or site must exist. Missing/invalid configuration exits **2**, before provider
registration or resource-group mutation; valid configuration exits **0**, explicitly
reminding you that manual connection setup and preview entitlement are still required.
This is not proof the connection exists or a subscription/region/quota check.

For other modes, command-line values take precedence over environment variables
and deployment outputs. If all three endpoint/model values are supplied, the
local deployment file is not read. A malformed file is reported when an output
from it is needed.

`--verify-services` is the separate **post-deployment** check for the fixture,
connection lookup, MCP initialization and native tool names/parameter schemas.
It creates/deletes a temporary toolbox, but **does not create a browser session
or call the model**. Missing connection/preview access or service failure exits **1**
without claiming success. Run this only after creating the manual connection.
If remote MCP preview access, native Toolbox expansion, Entra configuration or
tool schemas are unavailable, the lab fails closed. Do not replace it with a
local browser, custom fake tool or success transcript.

The expected remote MCP endpoint is documented as
`https://<workspace-region>.mcp.playwright.microsoft.com/playwrightworkspaces/<workspace-id>/mcp`.
The lab connects to the **Foundry Toolbox** endpoint instead; the native connection
supplies the downstream Playwright authentication. No API keys are required.

## Run and explicitly decide

```bash
.venv/bin/python demo.py
```

Review the printed action, actual managed-tool completion evidence and rejection
verification. To leave it unapproved, use the printed cleanup command:

```text
python demo.py --run-id <printed-run-id> --cleanup
```

Only if you want the **synthetic** registration created, use the exact printed
approval command, using this folder's `.venv/bin/python`:

```text
python demo.py --run-id <printed-run-id> --approve-action <printed-action-sha256>
```

The angle-bracket values above are instructions to copy actual run output, not
working IDs or an expected-success transcript. Do not approve a digest without
reviewing its complete action. Natural-language “register me”, page instructions,
model output, a generic `--yes`, or a previous run's approval cannot grant a permit.

The existing root runner maps the four standard outputs. The demo additionally
reads its own ignored `.azure/deployment.json` (`{"outputs": ...}`), so custom
output wiring does not require modifying the shared runner. Explicit options or
environment override that file:

| Option | Environment |
| --- | --- |
| `--project-endpoint` | `FOUNDRY_PROJECT_ENDPOINT` |
| `--deployment` | `AZURE_OPENAI_DEPLOYMENT` |
| `--site-endpoint` | `WORKSHOP_TOOL_ENDPOINT` |
| `--connection` | `BROWSER_AUTOMATION_PROJECT_CONNECTION_ID`, or `BROWSER_AUTOMATION_CONNECTION_NAME` |

When using the shared runner, select `--location eastus` (or another documented
supported region); its usual `eastus2` default is not accepted by this template.
Complete the manual connection/preview prerequisites before the runner's live step.
From the repository root, the runner command is:

```bash
BROWSER_AUTOMATION_CONNECTION_NAME=browser-workspace python3 deploy.py --demo 32 --location eastus
```

Before that command mutates providers/groups, the runner automatically invokes
`32-browser-runbook/.venv/bin/python 32-browser-runbook/demo.py --preflight`
with the same environment. The runner does not create the connection: on a first
deployment its live step will fail until you manually create `browser-workspace`
in the newly provisioned project. After that setup, use this lab's
`--verify-services`, then `demo.py`; do not repeatedly reprovision merely to
retry a missing manual connection. If both connection environment variables are
set, the full-ID variable takes precedence; clear stale IDs before using a name.

Do not point this code at a real registration service. The fixture preflight
requires this lab's exact synthetic identity and canonical base URL.

## Evidence, safety boundary and cleanup

- Native MCP `tools/call` results, rather than model-authored logs, drive the loop.
  The host discovers/validates the documented `create_browser_session`,
  `close_browser_session`, `browser_navigate`, `browser_snapshot`, `browser_find`,
  `browser_click`, and `browser_fill_form` parameter shapes. Model calls are
  limited to this run's URLs, two field values and draft-only selectors.
- No `browser_run_code`, `browser_evaluate`, arbitrary typing/Enter, file upload,
  tabs, external links or arbitrary tool access is exposed. Host and fixture
  enforcement, not the skill text, provide the approval boundary.
- The fixture accepts **only** the fixed synthetic values. The model/browser
  never receives the random 256-bit control capability used to approve/delete.
  This per-run application capability is not an Azure service API key. Only its
  hash is stored remotely; the local pending file is created mode `0600`.
- The fixture is intentionally internet-accessible to remote browsers. Run
  creation is anonymous; opaque random run IDs isolate browser access, but this
  is **not a production multi-tenant service**. There is no WAF, per-user login
  or abuse-rate limiting. Keep the workshop short and delete the dedicated group.
  Never add real personal data to it.
- Successful submission and ordinary failures delete the run state; an
  unapproved successful draft intentionally remains pending for review.
  `--cleanup` deletes it without model/browser usage. If deletion fails, a
  cleanup-only local capability is retained; submission cannot be replayed.
  Do not delete that capability until remote cleanup succeeds.
- Browser closure runs in `finally`, then the unique toolbox version is deleted.
  If connectivity fails before a session ID is returned or during closure, the
  host cannot guarantee immediate remote release; the service documents
  **15-minute inactive-session cleanup**. Inspect workspace diagnostics instead
  of blindly retrying. Live View URLs/session IDs are not printed.
- The fixture rejects expired runs after 30 minutes. Blob lifecycle deletion
  starts after one day and is asynchronous, not immediate deletion at expiry.
  Expired local pending files still require `--cleanup` (or removal after deleting
  the resource group). Tenant retention/soft-delete policy can retain deleted data.

**Costs:** model tokens, Playwright browser usage, Blob transactions/storage,
and the **continuously billed dedicated B1 (Basic) plan**, even when no demo runs.
Preview does not mean free. Check current
[Playwright pricing](https://azure.microsoft.com/pricing/details/app-testing/),
[App Service pricing](https://azure.microsoft.com/pricing/details/app-service/windows/),
and your model's pricing. Closing the browser or deleting a run does not stop
the hosting plan's charges.

After the workshop:

```bash
az group delete --name rg-browser-runbook --yes --no-wait
```

After group deletion completes, remove this lab's private pending/claimed files
from `.azure`. Do not remove deployment outputs while a run still needs cleanup.

## Offline validation

```bash
.venv/bin/python -m unittest discover -s . -p test_demo.py -v
.venv/bin/python -m compileall -q demo.py test_demo.py
az bicep build --file infra/main.bicep --stdout >/dev/null
az bicep lint --file infra/main.bicep
```

Tests mock Azure/model/MCP boundaries, check installed SDK serialization and
Responses function-call shapes, reject model-text-only evidence, verify
approval/action binding and no retry after ambiguous failure, and exercise
session/toolbox cleanup and pending-file replay guards. They also execute the
real fixture state machine with Node's built-in test runner (no browser mock
presented as a live run), including ETag races, expiration, rejection, single-use
submission, receipt records and deletion.

### Verified references

- [Foundry Browser Automation](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/browser-automation):
  Python SDK **2.8.0+**, native `BrowserAutomationToolboxTool`, connection ID and MI setup.
- [Playwright Workspaces remote MCP](https://learn.microsoft.com/azure/app-testing/playwright-cloud-browsers/how-to-playwright-workspaces-remote-mcp):
  preview access, endpoint format, tool names/parameters and session lifecycle.
- [Foundry Toolbox](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/toolbox):
  versioned MCP endpoint, `https://ai.azure.com/.default`, MCP discovery and calls.
- [Tool/model/region support](https://learn.microsoft.com/azure/foundry/agents/concepts/limits-quotas-regions).
- [Workspace ARM shape](https://learn.microsoft.com/azure/templates/microsoft.loadtestservice/2025-07-01-preview/playwrightworkspaces)
  and [workspace RBAC](https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/devops#playwright-workspace-contributor).
- [Azure Functions dedicated hosting](https://learn.microsoft.com/azure/azure-functions/dedicated-plan):
  Basic-tier support, Always On and continuous plan billing.

Installed SDK signatures/serialization were inspected, and Bicep resource schemas
were checked against the Bicep tooling before build/lint. Those offline checks do
not prove subscription entitlement, deployment success or live browser execution.
