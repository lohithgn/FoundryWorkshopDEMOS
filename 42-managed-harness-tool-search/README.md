# 42 — Managed Copilot harness and native tool search

This lab uses **real Foundry features**, not a local agent loop:

1. A service-hosted Toolbox contains the anonymous, read-only [Microsoft Learn MCP connector](https://learn.microsoft.com/api/mcp) and `ToolSearchToolboxTool` (`toolbox_search`). The initial catalog must contain only native `tool_search` and `call_tool`. The script searches the service's index, discovers the actual docs-search schema, and calls the discovered tool through native `call_tool`. Both full results are recorded.
2. A new prompt agent selects `MicrosoftCopilotHarness`, references an immutable native `SkillReference`, and uses an explicit conversation. The service definition must retain both harness and skill. The synthetic brief must contain the skill-only marker. **Skill contents are never downloaded or injected into the prompt.**

These are independently verified capabilities in the same isolated project. The MCP client verifies the explicit Toolbox; the harness uses its top-level skill reference. The lab does not claim that this explicit Toolbox is attached to the harness or that client MCP calls are harness-generated calls. Both checks must succeed for overall PASS.

Only filesystem reading is enabled in the harness. Shell, writes, web fetching, and subagents are disabled. The agent identity receives Foundry User at project scope for the run; that assignment, agent, skill, Toolbox, and conversation are cleaned up. Azure infrastructure remains deployed.

## Run

From the repository root:

```bash
python3 deploy.py --demo 42 --location swedencentral
```

Manual equivalent:

```bash
az group create --name rg-foundry-managed-harness-tool-search-demo --location swedencentral --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group rg-foundry-managed-harness-tool-search-demo --template-file 42-managed-harness-tool-search/infra/main.bicep --parameters location=swedencentral principalId="$(az ad signed-in-user show --query id -o tsv)"
cd 42-managed-harness-tool-search
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
.venv/bin/python -m unittest -q
.venv/bin/python demo.py --preflight
.venv/bin/python demo.py
```

For the manual path, supply the ARM outputs as environment variables or save them in `.azure/deployment.json` with shape `{subscription, resourceGroup, outputs}`. Output mappings:

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

## Official facts verified

- [Use the Microsoft Copilot harness](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/use-tools-skills-copilot-harness): Python SDK ≥2.9.0, `allow_preview=True`, native harness and top-level immutable skills.
- [Regional and model limits](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/limits-quotas-regions#microsoft-copilot-harness-preview).
- [Create and manage a Toolbox](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/toolbox).
- [Native tool search](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/tool-search): native `tool_search` and `call_tool`, hiding unpinned tools.
- Actual installed `azure-ai-projects==2.9.0` signatures and model serialization verified before calls; actual regional model availability queried using Azure CLI.
