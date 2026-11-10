# Lab 34: A Foundry agent uses Document Intelligence

## What you build

A **persistent prompt agent**, `document-intelligence-agent`, in the new Microsoft
Foundry project `document-intelligence`. Its OpenAPI tool runs entirely in Azure:

```text
Foundry agent -> key-protected Azure Function -> Document Intelligence
                                              prebuilt-invoice (2024-11-30)
```

The Function reads a stored one-page synthetic PDF and uses its **managed identity**
to submit and poll real Document Intelligence analysis. It returns vendor,
invoice number, total, currency, per-field confidence, page/text evidence and the
service operation ID and submitted PDF SHA-256. The agent formats those results,
not its own visual guesses.
The lab deliberately keeps OCR/extraction separate from generative reasoning.

Twelve approved PDFs are kept in [samples](./samples/). The original two are
preserved alongside ten additional documents. Open the linked PDFs during the
presentation and use the filename without `.pdf` as the tool's document ID:

| Document | Ground truth | Required behavior |
| --- | --- | --- |
| [clear](./samples/clear.pdf) | Synthetic Workshop Supplies, LAB-3401, USD 125.50 | Exact fields, confidence >= 0.8, no review |
| [missing-total](./samples/missing-total.pdf) | Synthetic Workshop Supplies, LAB-3402; no total/currency | Return null and require review |
| [office-supplies](./samples/office-supplies.pdf) | Synthetic Office Supplies, LAB-3403, USD 48.75 | Different vendor and amount |
| [training-materials](./samples/training-materials.pdf) | Synthetic Training Materials, LAB-3404, USD 240.00 | Whole-number amount with printed cents |
| [catering](./samples/catering.pdf) | Synthetic Workshop Catering, LAB-3405, USD 89.90 | Preserve decimal amount |
| [equipment](./samples/equipment.pdf) | Synthetic Equipment Rentals, LAB-3406, USD 1250.00 | Larger amount |
| [printing](./samples/printing.pdf) | Synthetic Print Studio, LAB-3407, USD 18.25 | Small amount |
| [zero-total](./samples/zero-total.pdf) | Synthetic Demo Credits, LAB-3408, USD 0.00 | Explicit zero is not a missing value |
| [large-total](./samples/large-total.pdf) | Synthetic Event Services, LAB-3409, USD 9876.54 | Larger decimal amount |
| [eur-invoice](./samples/eur-invoice.pdf) | Synthetic Workshop Europe, LAB-3410, EUR 315.20 | Explicit non-USD currency |
| [missing-total-office](./samples/missing-total-office.pdf) | Synthetic Office Supplies, LAB-3411; no total/currency | Return null and require review |
| [missing-total-catering](./samples/missing-total-catering.pdf) | Synthetic Workshop Catering, LAB-3412; no total/currency | Return null and require review |

No uploaded documents, arbitrary URLs, personal data or public Blob access.
Bicep embeds the actual repository PDF bytes in a private Function file; the
Function does not regenerate invoices at runtime. Every live extraction checks
the submitted PDF hash against its repository file, so a stale deployment or
changed document fails visibly. Ground-truth labels remain in
[manifest.json](./samples/manifest.json), outside the agent prompt and deployed
tool's metadata.

To reproduce the PDFs deterministically after intentionally editing the manifest:

```bash
cd 34-document-intelligence
node tools/generate-samples.js
.venv/bin/python -m unittest -v
```

The generator requires no npm packages. Store the regenerated PDFs with the
manifest and redeploy; offline tests reject missing, extra or non-reproducible
documents and check that all twelve IDs are wired into Bicep and OpenAPI.

This is not a custom hosted agent/container or an Agent Application publication.
It is a saved Foundry Agent Service prompt agent with a cloud-executed OpenAPI
tool, usable from the portal **without a local function-call loop**.

## Prerequisites and costs

- Python 3.10+, Node.js 22+ (offline tool tests), Azure CLI and Bicep.
- `az login` as a user allowed to create resources, assign roles, read Function
  host keys, create project connections and create/invoke Foundry agents.
- East US 2 Document Intelligence S0 availability and `gpt-4.1-mini`
  Global Standard quota. Function/Storage default to West US 2 independently.
- Internet access to the Azure control/data planes. RBAC/startup propagation can
  take several minutes; bounded retries report access failures explicitly.

**Billable resources remain deployed** for the portal demonstration. The Windows
P0v3 dedicated Function plan incurs fixed hourly charges even when idle (it
follows lab 4's proven hosting pattern). Storage, paid Document Intelligence
pages and model tokens also incur charges. A successful default pass analyzes
**36 pages**: twelve direct checks and twenty-four independent agent requests.
Retries may add paid calls. Each rerun makes new paid calls; this is a small
smoke test, not a production accuracy benchmark.

Function authorization uses a host function key held in a project-only Custom
Keys connection. The key is never embedded in the OpenAPI definition, logs or
local reports. Only the Function identity has scoped Cognitive Services User
access to Document Intelligence; Document Intelligence local/key authentication
is disabled. Host storage also uses managed identity. Rotate the Function key
and rerun setup to refresh the connection. Public endpoints are protected by
authentication, not private networking.

## Deploy and live-test

From the repository root:

```bash
az login
dotnet run --file deploy.cs -- --demo 34
```

The shared runner discovers the lab, builds/lints Bicep, installs its isolated
dependencies, runs Python and Node tests, deploys resources and runs the live
quality gate. It saves outputs and live logs under this lab's ignored `.azure/`.

Alternatively, deploy manually:

```bash
cd 34-document-intelligence
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
.venv/bin/python -m unittest -v
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
az group create --name rg-foundry-document-intelligence-demo --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
mkdir -p .azure
az deployment group create --name document-intelligence-demo \
  --resource-group rg-foundry-document-intelligence-demo \
  --template-file infra/main.bicep \
  --parameters principalId="$(az ad signed-in-user show --query id -o tsv)" \
  --query properties.outputs -o json > .azure/deployment.json
.venv/bin/python demo.py
```

For manual deployment, register `Microsoft.CognitiveServices`, `Microsoft.Web`,
and `Microsoft.Storage` if the subscription has not already registered them.
Redeployment preserves stable resource names; each normal demo run saves an
agent version (Foundry may reuse an identical version) and refreshes the secret
connection. Do not copy deployment
outputs between labs. Configuration can alternatively come from
`FOUNDRY_PROJECT_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT`,
`FOUNDRY_PROJECT_RESOURCE_ID`, `WORKSHOP_FUNCTION_RESOURCE_ID`,
and `WORKSHOP_TOOL_ENDPOINT`.

## Show it in Foundry

1. Open [Microsoft Foundry](https://ai.azure.com/), select the deployed
   `foundry-docs-*` resource and project **document-intelligence**.
2. Under **Build -> Agents**, open **document-intelligence-agent** and its latest
   version. Inspect the `invoice_documents` OpenAPI tool and project connection.
3. In the playground, ask: `Extract the clear invoice. Return the required JSON.`
4. Ask: `Extract the missing-total invoice. Return the required JSON.`
5. Inspect tool activity, extracted values and operation IDs. The second answer
   must have `total: null`, `currency: null`, and `needs_review: true`.
6. Compare `Extract the zero-total invoice. Return the required JSON.` with a
   missing-total case. Zero must remain `0`, with currency `USD` and no review.
7. Ask `Extract the eur-invoice invoice. Return the required JSON.` to demonstrate
   an explicit EUR amount rather than an assumed USD default.

Recheck the already saved agent/connection without changing either:

```bash
.venv/bin/python demo.py --ask-only --repetitions 1
```

There is no local tool execution in this mode. Python only sends requests and
assesses the returned cloud tool evidence. The portal may render JSON differently;
the machine-readable contract is tested through the same saved agent.

## Quality gate and failure handling

The ignored `.azure/quality-report.json` retains full response/tool evidence,
token usage, operation IDs, individual cases and overall completion. Failure
leaves `complete: false` and exits nonzero; no fabricated local result replaces
a cloud failure.

The gate requires:

- Real successful `prebuilt-invoice` analysis, exactly one page/document.
- Exact agreement with all four ground-truth values for all twelve PDFs.
- Submitted PDF SHA-256 matches the corresponding repository file.
- Evidence text/page for present fields; valid [0,1] confidence values.
- Complete-document confidence >= 0.8; missing/low-confidence fields require review.
- Exactly one completed cloud OpenAPI call per agent request.
- Strict seven-key agent JSON, matching the actual tool operation and values.
- Two independent requests per document by default, all must pass.

The expanded twelve-PDF corpus passed the shared deployment command on
2026-10-27: **12/12 direct analyses, 24/24 cloud agent requests and 96/96 agent
field comparisons**. All 36 operations had distinct service IDs and matching
repository PDF hashes. All six missing-total agent cases required review;
zero and EUR values were preserved correctly. Complete-document confidence
ranged from 0.819 to 0.988, above the unchanged 0.8 gate.
The saved agent is now **version 2**, with all twelve document IDs independently
read back. Throttling was handled by bounded retries; the final workflow exited
0 with no unhandled application errors.

The initial two-document version was live-tested on 2026-10-27, including
two direct extractions and four cloud agent cases. A separate six-request
ask-only run also passed: **10/10 agent requests, 40/40 ground-truth field
comparisons**, with correct missing-total review in all five relevant requests.
Clear-document field confidence was 0.929 / 0.988 / 0.929. Unauthenticated access
returned 401 and an authenticated unknown document returned 400 as intended.
The persistent agent was independently listed after testing. See the repository
[validation record](../tests.md#lab-34-document-intelligence-agent-live-validation-on-2026-10-27).

Confidence is a service estimate, **not an accuracy probability or correctness
guarantee**. Repeated requests over twelve controlled PDFs do not establish production
performance on varied layouts, scans, languages or poor-quality images. A real
application needs a representative labeled dataset, stronger access/rate limits,
retention controls, telemetry and a human-review workflow.

For failures, inspect the local report/log and Function logs. Do not lower
accuracy gates to hide incorrect extraction. Unknown document IDs are rejected;
upstream errors produce a logged HTTP 502, not an apparently successful answer.
Polling has a finite budget and operation URLs are restricted to the configured
Document Intelligence origin. Model HTTP 429 responses are logged and retried
at most five times, honoring numeric `Retry-After` / `retry-after-ms` headers
with a 120-second maximum delay; exhausted retries still fail visibly.

## Cleanup

Delete the dedicated group after the presentation; this removes the persistent
agent/project, connection secret, Document Intelligence, Function plan and
Storage together. Do not delete a group shared with unrelated resources.

```bash
az group delete --name rg-foundry-document-intelligence-demo --yes
```

The root `removeAll.sh` discovers lab 34 automatically and previews deletion by
default. Cognitive Services soft deletion may retain resource names; consult the
root cleanup instructions before redeploying a deleted group with the same names.
Ignored local reports remain on your machine until you remove them.

## Microsoft references

- [Foundry OpenAPI tools and API-key connections](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/openapi)
- [Document Intelligence invoice model](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/prebuilt/invoice?view=doc-intel-4.0.0)
- [Document Intelligence managed identities](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/authentication/managed-identities?view=doc-intel-4.0.0)
