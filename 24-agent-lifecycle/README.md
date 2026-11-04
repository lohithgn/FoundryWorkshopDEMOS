# 24 · Publish, version, monitor and roll back an agent

[Feature backlog](../BACKLOG.md#f024---microsoft-foundry-agent-application-lifecycle)

This lab **creates actual Azure Agent Application and managed agent deployment
resources** through the documented ARM publishing API, invokes the application's
Entra-authenticated Responses endpoint, updates the deployment's agent version,
then restores the previous version and verifies the same questions again.
It does not mistake a local Agent Framework script or a project-only agent call
for publishing.

## Important: publishing-model transition

Microsoft Learn now describes a newer agent object model in which each new agent
has a stable endpoint and version selector; publishing to M365/Teams is a different
operation with **no public publish API**. The separate Agent Application route
used here is explicitly documented as **legacy but still supported**, with
deprecation planned and no end-of-support date specified. This lab deliberately
demonstrates the requested **application** artifact through that documented route,
not Teams distribution. Do not silently replace it with a project invocation if
your region/subscription no longer supports application creation: fail visibly
and use the new endpoint/version-selector lab design instead.

ARM API: `2025-10-01-preview`. Responses API: `2025-11-15-preview`.
Prompt-agent deployments use `deploymentType=Managed`, not hosted containers.
The template provisions the account/project/model; the **demo's live run** creates
the application and versioned deployment after creating actual agent versions.
Model deployment precedes project creation to avoid ARM `RequestConflict` from
concurrent writes to the same Foundry account.

## Prerequisites / deploy / run

Python 3.10+, Azure CLI, `az login`, `gpt-4.1-mini` quota, Agent Service and Agent
Applications preview availability in the selected region. The caller needs:

- **Foundry User** (`53ca6127-db72-4b80-b1b0-d745d6d5456d`) at the account scope
  (assigned by Bicep) for creating agent
  versions and invoking the published application through inherited RBAC.
- **Contributor** at the project/account scope (or a custom application/deployment
  write role) to publish via ARM. This control-plane role
  is intentionally **not** silently granted by the template.
- Activity Log read permission at subscription scope (for example **Monitoring
  Reader**, or an existing subscription Reader/Contributor/Owner). The demo's
  monitoring query fails visibly if this permission is missing.
- Resource creation and role-assignment permissions for deploying Bicep.

Use a disposable resource group; run from this directory:

```bash
az login
az account set --subscription "<subscription-id>"
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
az group create -n lab24-lifecycle -l eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create -g lab24-lifecycle -n lab24 -f infra/main.bicep \
  -p principalId="$(az ad signed-in-user show --query id -o tsv)"
export FOUNDRY_PROJECT_ENDPOINT="$(az deployment group show -g lab24-lifecycle -n lab24 --query properties.outputs.projectEndpoint.value -o tsv)"
export FOUNDRY_PROJECT_RESOURCE_ID="$(az deployment group show -g lab24-lifecycle -n lab24 --query properties.outputs.projectResourceId.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show -g lab24-lifecycle -n lab24 --query properties.outputs.deployment.value -o tsv)"
.venv/bin/python demo.py
```

Root runner mappings: standard `endpoint`, `deployment`, `projectEndpoint`, plus
`projectResourceId` → `FOUNDRY_PROJECT_RESOURCE_ID`. The demo also reads that
extra value from its own root-runner `.azure/deployment.json` if not exported.
For federated callers use `principalType=ServicePrincipal` and the SP object ID.
Separate caller publish permissions still apply.

## What happens, and what proves it

1. Create a uniquely named real agent version instructed to return `USD 25`.
2. ARM PUT the application (`displayName` and allowed agent name), then ARM PUT its
   `active` managed deployment with Responses protocol `1.0` and that version.
3. Poll ARM until the deployment is **Running and references the expected
   agent/version**, then send both questions to
   `/applications/<name>/protocols/openai/responses`, not the project endpoint.
4. Create a changed agent version returning `USD 40`; update the same deployment
   to that version. Poll/read back its state and invoke the same stable endpoint.
5. In a `finally` block, restore the known-good version, read back Running state
   and invoke the original questions expecting `USD 25`. A failed rollout still
   attempts rollback; a failed rollback raises an error, never a success message.

`lifecycle-report.json` persists application/deployment IDs, full ARM deployment
snapshots at every stage, configured versions, response IDs, client/server request
IDs, durations, output, usage (where returned), errors and `rollback_verified`.
The version recorded for each request is the **ARM-read-back configured version**,
not an invented server-side response-version field. The distinct exact answer
checks catch stale routing. This is a sequential lab; concurrent deployment writers
would require a lock/ETag and stronger server-side version attribution.

## Inspect actual monitoring data

The demo queries the subscription's **Azure Activity Log API** filtered to the
managed deployment resource and this run's start time. It stores the real returned
events as `activity_log`, including operation/status/correlation IDs. Activity Logs
are eventually consistent: an empty immediate result is reported as empty, not
fabricated telemetry. In Azure portal open the application/deployment resource →
**Activity log**, filter to the run's time and inspect write operations for initial,
changed and restored routing. Refresh after several minutes if needed.

Use report client/server request IDs and response IDs to investigate data-plane
requests in Foundry monitoring where available. Local duration/usage records are
client evidence, **not a claim that App Insights ingestion occurred**. This lab
does not configure Application Insights or hosted-agent traces; lab 18 covers
that path. Activity Logs show control-plane changes, not model/tool latency.

## Offline tests / limitations / costs

```bash
.venv/bin/python -m unittest -v
az bicep build --file infra/main.bicep --stdout > /dev/null
```

Mocks validate real ARM request contracts, Managed deployment/version selection,
actual application endpoint invocation with the `https://ai.azure.com` audience,
same-test rollback, failed-rollout recovery, failed/stale state rejection.
No Azure resources are changed during tests. The dated [live verification
record](../tests.md) reports six actual application responses across versions
1 → 2 → 1, three successful Activity Log writes, and final version-1 ARM
readback. That historical pass does not guarantee current preview availability
or permissions in another subscription.

Only public Azure, stateless text Responses, a single active deployment and 100%
traffic to one version. No canary traffic split, Teams/M365 publishing, container
hosting, separate consumer role assignment or approval pipeline. Exact output
prompts may occasionally be ignored by the model; mismatches fail visibly.
RBAC/control-plane/data-plane propagation can delay availability; the demo uses
bounded polling, not silent project-endpoint fallback. Bearer tokens are never
printed or persisted. All prompts/outputs are synthetic.

Costs: six short model requests per complete run plus any provider-specific
managed-agent charges. This is **not** the container-hosted route: no minimum
replicas or always-on container compute is requested. Activity Log platform
collection is distinct from paid log export/retention. Check current Foundry
pricing and preview terms; publishing is not asserted to be free. Re-running
creates new uniquely named application/agent resources for inspectability.

## Cleanup

The final endpoint deliberately stays published to the known-good version.
Delete the disposable group (including application/deployment/model/project):

```bash
az group delete -n lab24-lifecycle --yes --no-wait
```

If keeping the project, first delete the application's `deployment_id` and then
`application_id` from the report with `az resource delete --ids "<id>"
--api-version 2025-10-01-preview`, then delete the recorded good/changed agent
versions using `AIProjectClient.agents.delete_version(agent_name=..., agent_version=...)`.
Check Entra for any dedicated agent identity/blueprint left by preview lifecycle
behavior. Remove local report/state only after cleanup evidence is recorded.

## Official API grounding

- [Publish Agent Applications: ARM bodies, invocation, update and RBAC](https://learn.microsoft.com/azure/foundry/agents/how-to/agent-applications)
- [New model and migration: legacy support and no Teams publish API](https://learn.microsoft.com/azure/foundry/agents/how-to/migrate-agent-applications)
- [Exact managed deployment schema and Running states](https://learn.microsoft.com/azure/templates/microsoft.cognitiveservices/2025-10-01-preview/accounts/projects/applications/agentdeployments)
- [Activity Logs REST API](https://learn.microsoft.com/rest/api/monitor/activity-logs/list)
