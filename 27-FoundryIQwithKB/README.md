# Lab 27 - Foundry IQ with a Storage-backed Knowledge Base

Create a **persistent agent in the new Microsoft Foundry portal**, connected to a
real **Foundry IQ knowledge base**, backed by two private Azure Blob containers.
Unlike [lab 11](../11-foundry-iq/README.md), this lab includes Storage, managed
ingestion/embeddings, a Foundry project, and a portal-visible agent. It is not
classic Agent Service file search, a direct Search index tool, or local RAG.

```text
Storage account (Entra ID; anonymous access and shared keys disabled)
  support-cases/*.txt -> managed Blob knowledge source: surface-support-cases
  surface-kb/*.txt    -> managed Blob knowledge source: surface-reference
                         |
               Azure AI Search managed ingestion
               chunking -> embeddings -> indexes; hourly refresh
                         |
               Foundry IQ: surface-support-kb
               LLM planning + multi-source retrieval + references
                         |
               knowledge-base MCP endpoint
               RemoteTool / ProjectManagedIdentity connection
                         |
               Foundry project: surface-support
               agent: surface-support-agent
               New Foundry -> Build -> Agents -> playground
```

## Synthetic scenario

[data/support-cases](./data/support-cases) contains four fictional computer cases:
Surface display/dock trouble, travel battery drain, an Arm64 VPN incompatibility,
and a desktop cable issue. [data/surface-kb](./data/surface-kb) contains four
fictional Surface troubleshooting/evidence runbooks. Case IDs link to runbook IDs.
All documents explicitly say **SYNTHETIC WORKSHOP DATA**. These are not official
Microsoft repair instructions, product guarantees, warranty policies, or real
customer records. No external documents are downloaded or copied.

## Prerequisites and cost

- Python 3.10+, Azure CLI with Bicep, and interactive `az login`.
- Permission to create resources and role assignments, including the
  **Foundry Project Manager** role used to create project connections.
- Quota for `gpt-4.1-mini` (`2025-04-14`, Global Standard) and
  `text-embedding-3-small` (`1`, Standard) in East US 2.
- Agentic retrieval/semantic ranker and the **`2026-08-01-preview`** Search API
  in the Search region (West US 2 by default). Project connections use
  `2025-10-01-preview`; agent SDK is pinned to `azure-ai-projects==2.7.0`.
- The **new** Foundry portal at <https://ai.azure.com>, not the classic portal.
  Portal paths and preview availability can change.

Basic Search incurs ongoing charges even when idle. Storage, model planning,
embeddings, agent inference, and scheduled ingestion also incur usage charges.
The template uses public network endpoints with Entra authentication, not public
blob access; private networking and production hardening are outside this lab.
RBAC is scoped to each resource: presenter can upload blobs/manage Search and
Foundry; Search identity reads blobs and calls models; project identity has
Search Index Data Reader. No API keys, SAS URLs, or connection-string secrets.
The trusted workshop participants all see the same synthetic corpus.

## Deploy and run

From the repository root:

```bash
az login
dotnet run --file deploy.cs -- --subscription "<subscription-id>" --demo 27 \
  --location eastus2 --search-location westus2
```

This compiles/lints Bicep, installs dependencies in the lab's virtual environment,
runs offline tests, deploys a dedicated resource group, then uploads and reads
back all eight documents. The **Blob knowledge-source service**, not this Python
script, creates indexes, indexers, skillsets, chunks, and embeddings. The script
waits for successful ingestion and visible indexed content, creates the knowledge
base and both project connections, creates/reads back an agent version, displays
actual retrieval activity/references, and invokes the agent through MCP.

The default group is `rg-foundry-FoundryIQwithKB-demo`. State is saved in ignored
`.azure/deployment.json`; actual ingestion, definitions, agent version, retrieval,
and agent response evidence are retained in `.azure/run.json`. A failed run
leaves `complete: false` and exits nonzero. No fallback/local answer is used.
Newly assigned HTTP 403 access is visibly retried at most ten times; schema and
other HTTP failures fail immediately. Ingestion has a 15-minute per-source limit.

Standalone deployment (run from this folder; use a dedicated group):

```bash
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
az provider register --namespace Microsoft.Search --wait
az provider register --namespace Microsoft.Storage --wait
RG="rg-foundry-FoundryIQwithKB-demo"
az group create -n "$RG" -l eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create -g "$RG" -n lab27 --template-file infra/main.bicep \
  --parameters principalId="$(az ad signed-in-user show --query id -o tsv)"
mkdir -p .azure
az deployment group show -g "$RG" -n lab27 \
  --query '{outputs:properties.outputs}' -o json > .azure/deployment.json
python3 -m venv .venv
source .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py
```

Reruns use the same blobs/sources/base/connections and call `create_version`
for the same agent name, not a disposable agent. The service can reuse a version
for an identical definition (observed in live testing). The agent is deliberately
**not deleted** after the script exits so it remains usable in the portal.
Existing versions remain until the resource group is deleted.

## New Foundry portal walkthrough (5-10 minutes)

1. Open <https://ai.azure.com>. Turn **New Foundry** on if the toggle is shown.
   Select the deployed account and project **surface-support** (confirm the
   project resource ID against deployment outputs if other projects have similar names).
2. Select **Build -> Knowledge**. Use the Search connection **surface-iq-search**
   and open **surface-support-kb**. If your portal first asks to connect Search,
   choose the already deployed service/connection; do not create a duplicate KB.
   Inspect its two Blob knowledge sources, container names, embedding deployment,
   and hourly ingestion schedule. This is the actual managed knowledge base.
3. Select **Build -> Agents -> surface-support-agent** and the latest version.
   Inspect its knowledge/tool configuration: the **surface-support-kb-mcp**
   project connection targets `/knowledgebases/surface-support-kb/mcp`, uses the
   project identity, and allows only `knowledge_base_retrieve`. Depending on the
   preview portal build, it can appear as Knowledge or as an MCP tool.
4. Open the agent playground and paste the compound question below. Inspect the
   knowledge-base tool invocation and source references, not just fluent text.
   Compare CASE-1001/1002 with KB-SURFACE-001/002 in Storage.
5. Ask the Arm64 follow-up and the unanswerable warranty question. Answers should
   separate case evidence from runbook advice and abstain on unknown information.
   Missing evidence is not permission to use the model's pretrained knowledge.

Suggested prompts:

> A Surface Laptop 5 external monitor is blank after sleep, and a Surface Laptop 6
> for Business loses battery in a bag. Compare the previous case outcomes and
> recommend the runbook steps and escalation criteria for both. Cite the CASE and KB document IDs.

> Why did the workshop VPN fail on Surface Laptop 7th Edition, and what approved
> replacement and escalation path does our runbook recommend? Cite the case and article.

> What is the exact warranty replacement price and next year's Surface Laptop specification?

The last question has no answer in this corpus. Expect
**"I don't know based on this knowledge base"**, not an invented price/specification.
The SDK run checks that an actual successful MCP retrieval occurred and that
any cited CASE/KB IDs exist in tool evidence; this is not a full factuality evaluator.

From this folder, query the same persistent agent without re-uploading or
creating another version:

```bash
.venv/bin/python demo.py --ask-only --question \
  "Why did the workshop VPN fail on Surface Laptop 7th Edition? Cite the case and runbook."
.venv/bin/python demo.py --ask-only --question \
  "What is the exact warranty replacement price and next year's Surface Laptop specification?"
```

## Capabilities to demonstrate

Based on [Microsoft Learn: Foundry IQ capabilities](https://learn.microsoft.com/azure/foundry/agents/concepts/what-is-foundry-iq?tabs=portal#capabilities):

| Capability | What to show here | Boundary |
| --- | --- | --- |
| One KB across multiple knowledge sources | Case-history and runbook containers feed one KB and agent | Both sources are Blob; not SharePoint/OneLake/web |
| Automated chunking, embeddings, and metadata | Inspect each source's generated index, skillset, and indexer in Search | `minimal` extraction for text; no Content Understanding/image demo |
| Incremental refresh | Hourly schedule plus the manual refresh exercise below | Scheduled runs and indexing are asynchronous; not instant sync |
| Keyword/vector/hybrid retrieval | Generated indexes have searchable text/vector fields and vectorizers; inspect the pipeline | This lab uses managed retrieval, not an application-selected three-mode benchmark (see lab 17) |
| LLM query planning, source selection, parallel searches, aggregation | Inspect real `activity` in `.azure/run.json` alongside the compound answer | Query/subquery count and parallelism are model decisions, not guaranteed |
| Extractive evidence with citations | KB uses `extractiveData`; agent reasons over returned content, cites CASE/KB IDs | Check tool evidence against documents; citations alone don't guarantee correctness |
| Reuse one KB across agents | Optionally create a second agent in Build -> Agents and attach this same KB | No re-ingestion needed; default script creates only one agent |
| ACLs, Purview labels, caller identity enforcement | Discuss as supported-source capabilities | **Not configured or demonstrated here.** Project identity is not end-user document authorization |
| Copilot Studio integration | Explain the documented connection of an Agent Service agent to Copilot Studio | Not deployed; requires separate Copilot Studio setup |

Never claim this lab proves ACL synchronization, sensitivity-label enforcement,
or per-user security trimming. Plain Blob containers here have no document ACL
ingestion or end-user token forwarding. The project identity intentionally sees
the entire synthetic corpus. Use [lab 21](../21-permission-aware-retrieval/README.md)
for a separate permission-aware exercise.

### Incremental refresh exercise

1. Ask: "What is the workshop battery triage review window?" Confirm **2 business days**
   from KB-SURFACE-002.
2. Change only that window in [KB-SURFACE-002.txt](./data/surface-kb/KB-SURFACE-002.txt)
   to **1 business day** (retain its synthetic label).
3. Run `.venv/bin/python demo.py --refresh-only`. The script uploads/readbacks
   the text and requests a managed indexer run, waiting for success. It does not
   manually patch a Search index or recreate the agent. The hourly schedule is
   an alternative to this immediate manual run.
   Unchanged blobs are read/verified but not rewritten, preserving their
   timestamps so incremental indexing processes only changed content.
4. Ask again in a **new playground conversation** or with `--ask-only`. Verify the
   new value and citation. Restore the original text and refresh when finished.
   Avoid reusing previous conversation history as evidence that ingestion changed.

## Validation, troubleshooting, cleanup

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
.venv/bin/python -m unittest discover -s . -p 'test_*.py' -v
```

Offline tests verify Blob ingestion contracts, source/base and MCP wiring,
readback/error handling, exact document coverage, polling, and grounded-response validation. They don't
establish live Azure access or portal rendering.

### Live verification on 2026-10-26

Deployed and left running in `rg-foundry-FoundryIQwithKB-demo`, with Foundry
and Storage in East US 2 and Basic Search in West US 2:

- Foundry account **surface-iq-hvjxeur5pdz74**, project **surface-support**,
  persistent agent **surface-support-agent**, version **1**.
- Actual Storage readback verified all eight documents. Independent Search
  readback matched every indexed snippet to the exact corresponding local text:
  four case chunks and four runbook chunks, with source Blob URLs.
- Both generated indexes have text/vector fields, managed Azure OpenAI
  vectorizers, and hourly indexer schedules. No embedding/index upload loop runs
  in the application.
- The compound question produced LLM query planning and **six actual Blob
  subqueries** across both sources. The agent made a successful
  `knowledge_base_retrieve` MCP call and cited CASE-1001/1002 and
  KB-SURFACE-001/002.
- The Arm64 question cited CASE-1003 and KB-SURFACE-003 and recommended the
  approved Arm64 VPN client. The warranty/future-spec question explicitly
  abstained after a real MCP retrieval.
- Incremental refresh changed the battery review window from **2 business days
  to 1 business day**. The runbook indexer processed exactly **one** changed
  document; the case indexer processed **zero**. A fresh call to the same agent
  returned the new value and citation. Both local and deployed content were
  restored, and a fresh agent call confirmed **2 business days** again.
- Independent readback verified the persistent agent and both project
  connections, including the project-managed-identity MCP authentication.
- **16 lab tests**, **32 root runner tests**, Python compilation, and Bicep
  build/lint passed. Test counts reflect the repository state at validation time.

The browser reached the Foundry sign-in screen, so **authenticated portal
rendering and playground interaction were not visually verified**. The deployed
project, portal-discoverable Search connection, and actual agent/MCP behavior
were verified through Azure APIs; sign in and use the walkthrough above.

Actual run evidence is retained in ignored `.azure/live-verification.json`,
`.azure/live-final.json`, `.azure/live-arm64.json`, `.azure/live-unknown.json`,
and `.azure/live-refresh-*.json`, with transcripts alongside them. Six recorded
live scenarios were checked independently. These runs are proof of this
synthetic scenario, not production-quality or permission-trimming certification.

- **403:** confirm presenter, Search, and project identity assignments. Allow
  propagation; an enduring 403 is an error, not a successful empty result.
- **Search provisioning capacity:** use a supported `--search-location` in a new
  dedicated group. Existing Search resources cannot move region in place.
- **Ingestion failure:** inspect generated indexer status/errors in Search.
  Confirm Search can read Storage and call the embedding deployment. Warnings
  and failed items fail explicitly so missing chunks aren't hidden.
- **404/API/tool failure:** verify endpoints, region/preview availability, the
  MCP connection audience, and the latest agent version. Never substitute classic
  file search or a direct index tool for this knowledge-base MCP endpoint.
- **Knowledge not listed:** select the correct project and Search connection in
  the new portal; reload after connection creation. The lab also creates an AAD
  `CognitiveSearch` connection so the Search-backed KB can be discovered.

Resources and scheduled indexers remain deployed and billable. Delete only the
dedicated lab group after the presentation:

```bash
az group delete --subscription "<subscription-id>" \
  --name rg-foundry-FoundryIQwithKB-demo --yes
```

References:
- [Foundry IQ overview and new portal workflow](https://learn.microsoft.com/azure/foundry/agents/concepts/what-is-foundry-iq?tabs=portal)
- [Managed Blob knowledge sources](https://learn.microsoft.com/azure/search/agentic-knowledge-source-how-to-blob)
- [Knowledge-base MCP integration with Foundry agents](https://learn.microsoft.com/azure/foundry/agents/how-to/foundry-iq-connect)
- [F027 acceptance criteria](../BACKLOG.md#f027---microsoft-foundry-foundry-iq-with-a-storage-backed-knowledge-base)
