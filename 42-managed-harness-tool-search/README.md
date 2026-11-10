# 42 — Managed Copilot harness and native tool search

This lab uses **real Foundry features**, not a local agent loop:

1. A service-hosted Toolbox contains the anonymous, read-only [Microsoft Learn MCP connector](https://learn.microsoft.com/api/mcp) and `ToolSearchToolboxTool` (`toolbox_search`). The initial catalog must contain only native `tool_search` and `call_tool`. The script searches the service's index, discovers the actual docs-search schema, and calls the discovered tool through native `call_tool`. Both full results are recorded.
2. A new prompt agent selects `MicrosoftCopilotHarness`, references an immutable native `SkillReference`, and uses an explicit conversation. The service definition must retain both harness and skill. The synthetic brief must contain the skill-only marker. **Skill contents are never downloaded or injected into the prompt.**

These are independently verified capabilities in the same isolated project. The MCP client verifies the explicit Toolbox; the harness uses its top-level skill reference. The lab does not claim that this explicit Toolbox is attached to the harness or that client MCP calls are harness-generated calls. Both checks must succeed for overall PASS.

Only filesystem reading is enabled in the harness. Shell, writes, web fetching, and subagents are disabled. The agent identity receives Foundry User at project scope for the run; that assignment, agent, skill, Toolbox, and conversation are cleaned up. Azure infrastructure remains deployed.

## Run

Requires Python 3.10+, Azure CLI/Bicep, a signed-in Entra user with resource
creation/role-assignment rights, quota for the model below, and access to the
managed harness/Skills/Toolbox previews. Microsoft Learn MCP is a public,
read-only external service; network access to it is required.

From the repository root:

```bash
dotnet run --file deploy.cs -- --demo 42 --location swedencentral
```

Manual equivalent:

```bash
cd 42-managed-harness-tool-search
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
.venv/bin/python -m unittest -q
az login
az account set --subscription "<subscription-id>"
mkdir -p .azure
az group create --name rg-foundry-managed-harness-tool-search-demo --location swedencentral --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group rg-foundry-managed-harness-tool-search-demo --template-file infra/main.bicep --parameters location=swedencentral principalId="$(az ad signed-in-user show --query id -o tsv)" \
  --query '{outputs:properties.outputs}' -o json > .azure/deployment.json
.venv/bin/python demo.py --preflight
.venv/bin/python demo.py
```

The manual commands save the required `outputs` envelope; the root runner adds
`subscription`/`resourceGroup` metadata too. Existing deployments can instead use
these environment mappings (clear stale overrides when changing projects):

| Output | Environment |
|---|---|
| `endpoint` | `AZURE_OPENAI_ENDPOINT` |
| `projectEndpoint` | `FOUNDRY_PROJECT_ENDPOINT` |
| `deployment` | `AZURE_OPENAI_DEPLOYMENT` |
| `projectResourceId` | `FOUNDRY_PROJECT_RESOURCE_ID` |

Required ARM parameters: `location`, `principalId` (User). Optional: `accountName`, `projectName`, `deploymentName`, `capacity` (1–10; default 10). Default model: reasoning/tool-calling `gpt-5.4-mini`, version `2026-03-17`, GlobalStandard. Azure CLI Entra authentication only; no saved tokens or secrets.

The caller also needs permission to create/delete role assignments at `projectResourceId` for the temporary agent identity (for example Owner or Role Based Access Control Administrator). Project/model access preflight alone does not prove this IAM permission.

**Region restriction:** Copilot harness preview supports `australiaeast`, `canadacentral`, `centralus`, `germanywestcentral`, `japaneast`, `swedencentral`, `uksouth`, `westeurope`, and `westus`. East US 2 is not supported. Creating/changing the harness of an existing agent is not supported; the lab always creates a fresh agent.

## Verification and costs

12 offline tests verify native SDK serialization, immutable skill packaging, hidden-tool discovery, and rejection of nonnative/missing evidence. Offline mocks test contracts only, never claim live PASS. Syntax compilation and Bicep compilation are required. Pylance was unavailable; installed SDK signatures, runtime serialization, syntax compilation, and unittest were used instead.

Actual deployment: **succeeded**, `rg-foundry-managed-harness-tool-search-demo`, Sweden Central, subscription `1217c203-6c76-47b4-b61c-9bba62dc8e02`.

Actual native Toolbox live search and call: **PASS**. Actual managed harness + immutable native skill: **PASS**, completed service response `caresp_0fb2252efa1b3b1200URMiDHBHUqz6M3k1s5yqhTterx5NFsza` included all brief headings and `NATIVE-SKILL-42`. Overall live exit **0/PASS**. Results are separately recorded in `.azure/live-report.json`; the script exits **1/BLOCKED** if either native capability is unavailable on a subsequent run.

One Toolbox search, one docs call, one agent conversation, one response; no resubmission loop. MCP stage timeout: 180 seconds; response timeout: 180 seconds. Harness restrictions disallow output-token-limit overrides. Files under ignored `.azure/` contain deployment outputs, ARM and live transcripts, read-back definitions, discovered schemas, and results, never authentication tokens.

## Cost and cleanup

The managed harness and model requests can incur service/usage charges;
preview access is not a free-usage guarantee. Public networking and GlobalStandard
processing outside the account region are workshop simplifications.
Normal execution attempts temporary-object/role cleanup, but interrupted or
failed cleanup can leave assets. Inspect the printed/report IDs, then delete only
the dedicated lab group after confirming the subscription and actual group name:

```bash
az group delete --name rg-foundry-managed-harness-tool-search-demo --yes --no-wait
```

Local ignored evidence remains after group deletion.

## Official facts verified

- [Use the Microsoft Copilot harness](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/use-tools-skills-copilot-harness): Python SDK ≥2.9.0, `allow_preview=True`, native harness and top-level immutable skills.
- [Regional and model limits](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/limits-quotas-regions#microsoft-copilot-harness-preview).
- [Create and manage a Toolbox](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/toolbox).
- [Native tool search](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/tool-search): native `tool_search` and `call_tool`, hiding unpinned tools.
- Actual installed `azure-ai-projects==2.9.0` signatures and model serialization verified before calls; actual regional model availability queried using Azure CLI.
