# 45 — Permission-native Foundry IQ + Fabric IQ

Two **native** integrations, using synthetic data only:

* A managed Azure Blob **knowledge source** synchronizes container RBAC scope into
  permission-filter fields. The knowledge base receives the caller's real Entra
  identity in `x-ms-query-source-authorization`. Search constructs the security
  filter internally; this is **not** lab 21's application-generated `$filter`.
* `MicrosoftFabricPreviewTool` calls a published **Fabric data agent**, through a
  documented Foundry project connection, with signed-in-user identity passthrough.
  This is an actual Fabric IQ data-agent integration, not a local CSV query or an
  invented ontology API. The CSV is solely the synthetic provisioning asset.

## Deploy and run

Python 3.11+, Azure CLI/Bicep, signed-in Entra user with resource/role-assignment
rights, supported model quota, and permission-filter preview availability.
Foundry is eastus2, Search westus2. Billable: Basic Search, chat capacity **5**
and embedding capacity **1**, small LRS storage; no Fabric capacity is purchased
automatically. Chat capacity 5 avoids throttling two ~900-token permission probes.

From the repository root (runtime outputs are mapped/loaded automatically):

```bash
python3 deploy.py --demo 45
```

Standalone:

```bash
python3 -m venv 45-governed-iq/.venv
45-governed-iq/.venv/bin/pip install -r 45-governed-iq/requirements.txt
45-governed-iq/.venv/bin/python 45-governed-iq/deploy_lab.py --infra-only
cd 45-governed-iq
.venv/bin/python -m unittest -v
.venv/bin/python demo.py
```

The standalone wrapper creates the group itself and records
`.azure/lab-deployment.json`; existing groups must match both that record and its
`managedBy`/`lab` ownership tags. It refuses unrecorded/unowned groups and
subscription/target changes. `--infra-only` deploys without running live gates.
Fresh default is the requested RG; a previously recorded owned target is retained.
Equivalent tagged RG command (reference, **not a pre-create step for the wrapper**):

```bash
az group create --name rg-foundry-governed-iq-demo --location eastus2 --tags managedBy=foundry-lab-script lab=45-governed-iq SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
```

`infra/main.bicep` requires `location` and `principalId` (**User** object ID from
`az ad signed-in-user show --query id -o tsv`). Outputs: `endpoint`, `deployment`,
`projectEndpoint`, `projectResourceId`, `embeddingDeployment`, `storageEndpoint`,
`storageResourceId`, `containerResourceId`, `searchEndpoint`, `searchResourceId`,
`tenantId`, `principalId`. All resources and logs remain for presentation.

## Honest access gates

The default run uploads only `data/restricted.txt`, reads back native permission
settings, waits for successful indexing, and requires the authorized retrieval to
contain `HELIOS-045-ORCHID`. It also tests the **genuinely omitted** query-source
identity: restricted content must not be returned.

For the required two-real-user test, set `SECOND_AZURE_CONFIG_DIR` to a separately
signed-in Azure CLI profile (no profile is created or directory identity changed).
That real principal must differ from the presenter. The script requires an actual
**403** reading the synthetic source blob, then verifies a successful knowledge-base
query does not expose the restricted marker. Service authorization still uses the
presenter's credential; source authorization uses the secondary user's token.
An absent secondary profile means **twoRealUsers BLOCKED**, not a fictional denial.
Never export bearer tokens or pass tokens in command-line arguments.
The presenter has explicit Data Reader on this lab's dedicated synthetic storage
account, plus Data Contributor on its single synthetic container for uploading.

## Fabric setup and actual smoke

The script makes **read-only capacity metadata probes** against:

* `GET https://api.fabric.microsoft.com/v1/capacities`
* `GET https://api.powerbi.com/v1.0/myorg/capacities` (documented Premium alternative)
* Subscription ARM `Microsoft.Fabric/capacities` inventory.

It never lists or reads existing business content. Capacity/API authorization
failures are saved as specific BLOCKED evidence.

In an administrator-approved **designated synthetic** Fabric workspace on paid
**F2+** or Fabric-enabled Power BI **P1+**, import `data/fabric-sales.csv` into a
semantic model (revenue = sum of units × unit_price = **400**). Create and publish
a data agent named `synthetic-governed-iq-sales` using only that model. Give each
caller Read access to the agent and model; model RLS/CLS still apply. Enable data
agent and cross-geo tenant settings where required; keep source/agent capacities
in the same region and Foundry/Fabric in the same tenant.

Set `FABRIC_WORKSPACE_ID`, `FABRIC_DATA_AGENT_ID`, `FABRIC_SYNTHETIC_ONLY=1`.
The script validates the designated item's synthetic name; uses documented ARM
`PUT .../projects/.../connections/synthetic-fabric-iq?api-version=2025-04-01-preview`
with `CustomKeys` containing only `workspace_id`/`artifact_id` GUIDs (not secrets);
creates a persistent native Fabric-tool agent and forces a real tool query.
PASS requires a completed Fabric tool execution **and** revenue 400, not text alone.
No IDs/capacity/license/tenant consent means **Fabric IQ BLOCKED**.

The full lab exits **2** if either Fabric or two-user verification is blocked.
Offline test doubles validate contracts only; they cannot establish live success.
`.azure/deployment.json`, `fabric-preflight.json`, `live-report.json` are ignored;
no stored keys, tokens, OAuth secrets, or broad directory changes.

## Current facts and boundaries

Verified against official Learn and installed `azure-ai-projects==2.7.0`:

* [Blob native RBAC ingestion](https://learn.microsoft.com/azure/search/search-blob-indexer-role-based-access)
* [Query-time source identity enforcement](https://learn.microsoft.com/azure/search/search-query-access-control-rbac-enforcement)
* [Knowledge-base retrieve](https://learn.microsoft.com/azure/search/agentic-retrieval-how-to-retrieve)
* [Fabric connection, OBO identity and native tool](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/fabric)
* [Fabric capacity API](https://learn.microsoft.com/rest/api/fabric/core/capacities/list-capacities)

Search preview API `2026-08-01-preview`; no SharePoint app registration is needed
for this Blob-native variant. Container RBAC is the supported source permission
model, not arbitrary per-document ACL emulation. Group synchronization and
revocation can have propagation delays. Pylance transport was unavailable during
implementation; installed SDK inspection, syntax compilation and unittest are the
verification fallback. See ignored live report for the exact current execution.

## Live verification — 2026-11-03

**18 offline tests PASS**, Bicep build/lint PASS, and actual ARM deployment PASS in
`rg-foundry-governed-iq-demo` (Foundry eastus2 / Search westus2).
Native managed source permission synchronization, successful indexing, real
authorized source read **200**, protected knowledge-base retrieval **1 reference**,
and genuinely omitted query-source identity denial all **PASS**.
RBAC/source authorization caches needed propagation during setup; no elevated
read or application filter was used to establish retrieval success.

Full lab remains **BLOCKED**: no secondary signed-in CLI user is configured; Fabric
and Power BI token probes both returned **AADSTS500014** (tenant service principal
disabled), ARM returned **zero Fabric capacities**, and designated synthetic Fabric
workspace/data-agent IDs are absent. No Fabric tool query or two-user success is
claimed. Exact evidence is retained in `.azure/live-report.json` and
`.azure/fabric-preflight.json`.
