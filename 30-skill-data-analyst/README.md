# 30 — Skill-powered CSV analyst

A standalone Foundry lab: reuse a version-pinned **instructional skill**, execute
Python in **native Code Interpreter's cloud sandbox**, and download a real report
and PNG chart. No local analysis or fabricated-artifact fallback exists.

## What happens

1. [SKILL.md](skills/csv-analyst/SKILL.md) defines the analysis procedure and
   artifact contract. It is not an executable tool or a Python script.
2. The application packages and uploads the skill to the **preview Foundry Skills
   API**, downloads that exact version (not the default alias), and verifies its
   bytes. It injects the downloaded text into the agent's instructions.
3. It uploads six synthetic attendance/cost CSV rows, creates a uniquely named
   native container with that input file, and attaches the built-in
   `CodeInterpreterTool` to a temporary prompt-agent version.
4. One paid Responses request, pinned to that agent version, asks the sandbox
   to calculate totals and create `report.json` and `attendance-cost.png`.
5. The harness requires completed native tool calls in its container, executed
   code, a sandbox log marker, and two distinct real container-file citations.
   The Responses request explicitly includes `code_interpreter_call.outputs` so
   the service returns execution logs; without it, the log marker may be omitted
   even when the code ran. It prints execution/citation evidence and actual token
   usage when returned.
6. It downloads both cited files through the container-content API. The actual
   JSON bytes must match the independent oracle, including the skill marker,
   all totals, grouping, integer types, and no extra/missing/duplicate fields.
   PNG signature, chunk checksums, dimensions, compressed image data and scanline
   structure are validated. Sizes and SHA-256 hashes are printed.

Expected report: **6 rows, 55 attendance instances, 95000 cents**. Agents:
25 / 50000 cents; Foundry: 30 / 45000 cents. Attendance is not unique people.
The oracle stays local and is never supplied to the model; local code validates
cloud outputs but does not perform the demo analysis or generate replacement
artifacts. PNG validation establishes a decodable image, not that every plotted
pixel semantically matches the report; inspect the downloaded chart visually.

Skill selection, version pinning, ZIP verification, instruction injection,
container lifecycle and artifact validation are **application-managed**.
This lab intentionally follows lab 28's direct-injection pattern rather than
native skill binding or automatic discovery/progressive loading. The skill
does not add model weights or executable tools; Code Interpreter is a separate
native tool. No local subprocess execution, Toolbox, MCP, or hosted agent is used.

## Prerequisites and preview caveats

- Python 3.10+, Azure CLI/Bicep, and `az login` as an interactive user.
- Resource deployment and role-assignment permissions; the template assigns
  Foundry User (formerly Azure AI User) to that user at account scope.
- GPT-4.1-mini `2025-04-14` GlobalStandard quota/availability in the selected
  region (default `eastus2`).
- Both preview Skills operations and native Code Interpreter, container
  creation/content/deletion and Responses agent references must be supported by
  your Foundry project/region/model. SDK method availability alone does not
  establish service support. `allow_preview=True` enables preview headers.

The installed SDK types/signatures were checked with `azure-ai-projects==2.7.0`
and its transitive `openai==3.35.1` in offline validation. The Projects dependency
owns the compatible OpenAI range; do not independently install an incompatible
OpenAI major version. Some current Learn examples use auto-created containers;
this lab uses the SDK-supported explicit container ID so its sandbox can be
deleted even if inference fails. Unsupported explicit containers or other
preview routes fail visibly; no auto-container or local fallback is attempted.

Entra authentication uses `AzureCliCredential` only; local/key authentication is
disabled in Bicep. Public networking is enabled for workshop convenience.
GlobalStandard processing may occur outside the resource region. Only synthetic
data is uploaded. The harness is noninteractive once Azure CLI login is available.

**Live retries on 2026-11-09:** deployment, skill upload and native sandbox calls
completed, but an initial response omitted execution logs. After explicitly
including Code Interpreter outputs, the next response returned real logs and
calculated the expected totals, but the model did not print the required marker;
the strict gate stopped before the report and chart were verified. A subsequent
run with the marker instruction reinforced passed the complete gate: the
downloaded report contained 6 rows, 55 attendance instances and 95000 cents,
and the visually reviewed chart matched it. The sandbox corrected its initial
file path and integer serialization before producing those artifacts. The
run's resource group was verified deleted. Offline tests alone do not prove
current service availability, inference success, role propagation, or cloud
deletion support.

## Deploy and run

From the repository root:

```bash
az login
python3 deploy.py --subscription "<subscription-id>" --demo 30
```

The root runner discovers this folder; Bicep exposes `endpoint`, `deployment`,
and `projectEndpoint`, accepts `principalId`/`location`, and applies the
repository's `SecurityControl`/`SecurityExemption` tags.

Alternatively, deploy manually from this folder:

```bash
az login
az account set --subscription "<subscription-id>"
RG=rg-foundry-skill-data-analyst-demo
LOCATION=eastus2
PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv)
az group create --name "$RG" --location "$LOCATION" --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --name lab30 --resource-group "$RG" \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location="$LOCATION"
export FOUNDRY_PROJECT_ENDPOINT=$(az deployment group show -g "$RG" -n lab30 --query properties.outputs.projectEndpoint.value -o tsv)
export AZURE_OPENAI_DEPLOYMENT=$(az deployment group show -g "$RG" -n lab30 --query properties.outputs.deployment.value -o tsv)
```

Then install dependencies and run from this folder. For an existing deployment,
set the two environment variables to its project endpoint and deployment name
instead:

```bash
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py
python demo.py --save-artifacts
```

Default: verified downloads remain in memory and are discarded at exit.
`--save-artifacts` retains **only verified user artifacts** in this lab's
git-ignored `.azure/artifacts/<unique-run-id>/`; no arbitrary output path is
accepted. Input CSV and skill ZIP are constructed in memory. Each run creates
unique cloud names and a fresh sandbox, so parallel runs do not share state.

Requests have 180-second read/request timeouts (15-second Azure connection and
30-second CLI credential timeouts), with automatic retries disabled. These are
per-operation bounds, not an overall wall-clock deadline. Inference is not
retried after timeout. Only the initial skill upload retries the exact
`404/NotFound: Project not found` response for up to three minutes while a newly
deployed project becomes available; it does not retry ambiguous timeouts or
other errors. Errors remain nonzero: 400/404 can mean unsupported
preview routes/model/region, 403 can mean RBAC/propagation, 429 quota/rate limits.
The original service/validation error is retained with explicit preview guidance.

## Costs and cleanup

Each live run makes one model response request and uses a **billable Code
Interpreter sandbox**, plus applicable storage/service costs. Repeated tool
calls and failed/timed-out requests can still incur charges. Check current
[Code Interpreter pricing guidance](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/code-interpreter)
and your region's pricing; no price or token count is invented.

Cleanup runs in reverse order for every successfully returned object: response,
agent version, container (including generated files), uploaded CSV, and skill.
All deletions are attempted even if one fails. Cleanup-only failures exit
nonzero; when another failure already exists it is preserved and deletion
errors are printed to stderr. Infrastructure is not deleted by the script.
The container requests a 20-minute inactivity expiry as additional protection.
Abrupt termination or a creation request that times out after server acceptance
can leave resources whose IDs were never returned. Printed unique names/IDs
identify known resources for manual cleanup; expiry is not proof of deletion.

Delete only this lab's dedicated resource group after checking its actual name
from deployment output and the selected subscription:

```bash
az group delete --subscription "<subscription-id>" --name rg-foundry-skill-data-analyst-demo --yes --no-wait
```

Optionally remove locally saved user artifacts with
`rm -rf 30-skill-data-analyst/.azure/artifacts` **from the repository root**.

## Offline verification

From this folder:

```bash
.venv/bin/python -m unittest discover -s . -p test_demo.py -v
az bicep build --file infra/main.bicep --stdout >/dev/null
az bicep lint --file infra/main.bicep
```

Tests exercise real SDK response/tool/citation shapes at mocked network
boundaries: pinned skill roundtrip, happy path, independent fixture oracle,
missing/failed tool evidence, malformed/foreign citations, downloaded content,
optional persistence, creation failures, cleanup failures, unique names, CLI
timeouts/Entra/preview configuration, and API signatures. They make no paid calls.

## Official references

- [Native Code Interpreter and container-file downloads](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/code-interpreter)
- [Foundry skills and direct injection](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/skills)
- [Azure AI Projects Python reference](https://learn.microsoft.com/python/api/azure-ai-projects/azure.ai.projects)
