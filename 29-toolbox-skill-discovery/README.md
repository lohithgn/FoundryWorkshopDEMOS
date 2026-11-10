# 29 - Runtime skill discovery through Foundry Toolbox

Upload three version-pinned skills to Foundry, attach them to one temporary
Toolbox, and discover them through its real MCP `resources/list` endpoint.
For three independent user requests, a Foundry-backed Responses agent sees only
discovery metadata, selects a skill, and loads its body through `resources/read`.
The live script requires exactly one relevant skill read and an exact output
contract per case. The briefing additionally requires an on-demand read of its
bundled checklist; the other requests must not load it. Instructions and output
markers are not supplied in the user request.

## What runs where

Foundry stores skills, serves the Toolbox MCP endpoint, and performs inference.
The **Python harness runs locally** and implements the read-only skill provider
and bounded tool loop. This is not a native `skills=` prompt-agent option, a
hosted-container deployment, or the .NET Agent Framework provider. It demonstrates
the same advertise/load progressive-disclosure mechanism using the MCP Python SDK.
It never downloads all skill bodies eagerly or falls back to local files on a
service failure. Files on disk are used only for the initial upload.

The provider follows the Foundry preview's documented `resources/list` discovery
surface; it is not a complete implementation of every MCP Skills extension
revision. Skill file URIs must use the explicit `/SKILL.md` form. Only the bundled
checklist sibling becomes readable after the relevant skill is loaded; arbitrary
URIs, path traversal and repeated/unrelated reads fail.

This deliberately small lab contains instructions and a text reference: no scripts,
external tools, or state-changing actions. A skill supplies
procedure, not model weights. Metadata itself consumes tokens; this is not a
token-savings benchmark.

## Prerequisites and costs

- Python 3.10+, Azure CLI/Bicep and `az login` as an interactive user.
- Resource creation and role-assignment rights; Foundry User is assigned by Bicep.
- GPT-4.1-mini `2025-04-14` GlobalStandard quota in the chosen region.
- Preview Skills API and Toolbox skill discovery support. The pinned SDK enables
  preview operations; the MCP server must implement resource discovery and reads.

Public endpoints and GlobalStandard processing are workshop simplifications.
Use synthetic data only. The demo makes paid model calls (normally seven, maximum
twelve); errors are not replaced with sample answers. A 400/404 for unsupported
preview operations or a missing resource list fails explicitly.

## Deploy and present

From the repository root:

```bash
az login
dotnet run --file deploy.cs -- --subscription "<subscription-id>" --demo 29
```

Or from this folder:

```bash
az login
RESOURCE_GROUP=rg-foundry-toolbox-skill-discovery-demo
PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv)
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group "$RESOURCE_GROUP" --name demo \
  --template-file infra/main.bicep --parameters principalId="$PRINCIPAL_ID"
export FOUNDRY_PROJECT_ENDPOINT=$(az deployment group show -g "$RESOURCE_GROUP" -n demo --query properties.outputs.projectEndpoint.value -o tsv)
export AZURE_OPENAI_DEPLOYMENT=$(az deployment group show -g "$RESOURCE_GROUP" -n demo --query properties.outputs.deployment.value -o tsv)
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
python -m unittest discover -s . -p 'test_*.py' -v
python demo.py
```

Show the three local skill descriptions first. In the transcript, distinguish
metadata discovery, the model's actual read request, returned skill instructions,
and checked final JSON. Each request starts without a previous response ID;
only its own tool-result continuation uses server response state.
The first request omits `previous_response_id` entirely: sending JSON `null`
causes Foundry to reject it with HTTP 400, `Expected string, got Null`.

## Validation and cleanup

```bash
python -m unittest discover -s . -p 'test_*.py' -v
az bicep build --file infra/main.bicep --stdout >/dev/null
az bicep lint --file infra/main.bicep
```

Offline tests exercise real MCP/OpenAI model payloads with mocked network
boundaries. They are not live discovery or instruction-following evidence.
The live harness deletes the temporary Toolbox before deleting its skills, also
on inference or MCP failure. Unique resource names are printed for manual recovery
after interruption. Model responses remain subject to service retention policies.
Infrastructure remains billable until its dedicated resource group is deleted:

```bash
az group delete --name "$RESOURCE_GROUP" --yes
```

Confirm the selected subscription and group before deletion.

## Sources

- [Foundry skills, Toolbox discovery and progressive disclosure](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/skills)
- [Official .NET hosted skills-provider sample (different runtime)](https://github.com/microsoft-foundry/foundry-samples/tree/main/samples/csharp/hosted-agents/agent-framework/foundry-toolbox-mcp-skills)
