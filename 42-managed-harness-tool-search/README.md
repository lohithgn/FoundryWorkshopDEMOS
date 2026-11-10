# 42 — Managed Copilot harness and native tool search

> **Migration status:** .NET 10 file-based C# is the current lab workflow.
> Original Python sources and requirements are retained unchanged temporarily
> for staged root-runner integration; historical Python evidence below is not
> .NET live validation. No .NET live Azure validation is claimed.

This lab uses **real Foundry features**, not a local agent loop:

1. A service-hosted Toolbox contains the anonymous, read-only [Microsoft Learn MCP connector](https://learn.microsoft.com/api/mcp) and `ToolSearchToolboxTool` (`toolbox_search`). The initial catalog must contain only native `tool_search` and `call_tool`. The script searches the service's index, discovers the actual docs-search schema, and calls the discovered tool through native `call_tool`. Both full results are recorded.
2. A new prompt agent selects `MicrosoftCopilotHarness`, references an immutable native `SkillReference`, and uses an explicit conversation. The service definition must retain both harness and skill. The synthetic brief must contain the skill-only marker. **Skill contents are never downloaded or injected into the prompt.**

These are independently verified capabilities in the same isolated project. The MCP client verifies the explicit Toolbox; the harness uses its top-level skill reference. The lab does not claim that this explicit Toolbox is attached to the harness or that client MCP calls are harness-generated calls. Both checks must succeed for overall PASS.

Only filesystem reading is enabled in the harness. Shell, writes, web fetching, and subagents are disabled. The agent identity receives Foundry User at project scope for the run; that assignment, agent, skill, Toolbox, and conversation are cleaned up. Azure infrastructure remains deployed.

## Run

Requires the .NET 10 SDK, Azure CLI/Bicep, a signed-in Entra user with resource
creation/role-assignment rights, quota for the model below, and access to the
managed harness/Skills/Toolbox previews. Microsoft Learn MCP is a public,
read-only external service; network access to it is required.

From this lab directory, restore/build and run safe help and offline checks:

```bash
dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet run --file demo.cs -- --help
dotnet restore test_demo.cs
dotnet build test_demo.cs --no-restore
dotnet run --file test_demo.cs
```

After an authorized deployment and configuration of the documented outputs:

```bash
dotnet run --file demo.cs -- --preflight
dotnet run --file demo.cs
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

**Historical Python validation only:** 12 offline tests verified native SDK serialization, immutable skill packaging, hidden-tool discovery, and rejection of nonnative/missing evidence. Offline tests validate contracts only and never establish live success.

**Historical Python live validation only:** actual deployment succeeded in `rg-foundry-managed-harness-tool-search-demo`, Sweden Central. This is not a .NET deployment or live-test result.

The historical Python implementation passed native Toolbox search/call and managed harness/immutable skill checks. Its saved response and report are historical evidence only; the .NET live paths remain unverified.

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

- [Use the Microsoft Copilot harness](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/use-tools-skills-copilot-harness): preview API, native harness and top-level immutable skills.
- [Regional and model limits](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/limits-quotas-regions#microsoft-copilot-harness-preview).
- [Create and manage a Toolbox](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/toolbox).
- [Native tool search](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/tool-search): native `tool_search` and `call_tool`, hiding unpinned tools.
- Historical Python SDK signature evidence: installed `azure-ai-projects==2.9.0` and regional model availability queried using Azure CLI.
