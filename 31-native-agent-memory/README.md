# 31 - Native Foundry agent memory

Persist a preference from one conversation, recall it in a **fresh independent
conversation without its history**, isolate two real authenticated users, and
delete one user's memory without deleting the other's.

This uses the **real preview Foundry Memory Store APIs**, not SQLite, a local
dictionary, a vector database substitute, or saved chat history:

- `beta.memory_stores.create` with chat **and embedding** deployments.
- `begin_update_memories`: Foundry extracts user-profile memories from the seed
  conversation's user message. It must preserve the exact synthetic blend code.
- `search_memories`: retrieve only the authenticated user's explicit scope.
- `delete_scope`: forget all memories in the first user's scope; search twice
  must return no hits and item enumeration must be empty. The second user's
  preference must still be searchable.

**Architecture choice:** this is the documented **low-level memory API workflow**,
with application-managed injection of native search results into a Foundry prompt
agent, like lab 28's explicit injection pattern. It does **not** attach
`MemorySearchPreviewTool`, use automatic memory extraction, or demonstrate the
`{{$userId}}` resolver. The agents have `tools=[]`; recall cannot schedule a
background write that would recreate a forgotten preference. Only the two
explicit extraction operations write memories.

## What to present

1. Authenticate both real users before provisioning. The noninteractive runner
   refuses missing, duplicate or invalid user sessions; no invented names-as-auth.
2. A unique dedicated store starts empty for both users.
3. Each user supplies a different random synthetic coffee blend code in a new
   conversation. The response, conversation and temporary agent are deleted.
   Foundry then extracts memory from that exact original user message.
4. Native memory search runs separately for each user. Cross-scope results and
   the other user's code are errors. Search results, not seed messages, become
   quoted, untrusted facts in a new temporary agent's instructions.
5. Each recall starts a newly created conversation, with the same generic
   question and **no previous response ID, conversation ID or search ID**.
   Its exact JSON answer must contain only that user's blend code.
6. Delete the first scope; native search must be empty twice and native item
   listing empty. Another independent conversation must return `{"coffee":null}`.
   Verify that the second scope still returns its own code.
7. A finalizer deletes the entire unique store. Every temporary agent, response
   and conversation is also deleted, including on validation/inference failures.
   Cleanup failures are errors, not silently reported success.

No skill is added: this lab teaches state lifecycle, not procedure packaging.

## Authentication and trusted scope boundary

Like lab 21, **both end-user identities are real**, but Azure resource access is
through a trusted backend:

- The default Azure CLI login is the presenter/backend and first end user.
- `SECOND_AZURE_CONFIG_DIR` holds a separate real user's existing CLI login.
- Both token acquisitions explicitly target `AZURE_TENANT_ID`. Each identity
  comes from an authenticated Microsoft Graph `/me` call, not decoded unverified
  JWT claims, a user-supplied object ID, display name, prompt or header.
- The harness maps the canonical tenant ID and Graph object ID to the stable
  scope `entra-` + SHA-256(`tenant/object-id`). **The hash is not authorization.**
- All Foundry calls use the presenter's `AzureCliCredential` and project RBAC.
  The second end user needs Graph delegated access for `/me`, not Foundry RBAC.
  Neither end user signs individual Foundry requests here.

Low-level memory APIs **do not resolve identity automatically**. Their `scope`
is a backend-selected partition, **not an Entra ACL**. A backend with these
Foundry permissions can deliberately select any scope. This demonstrates
authenticated application-level isolation, not resistance to a compromised
backend, independent direct-user Foundry RBAC, or a general security proof.
Never expose arbitrary scope selectors or `x-memory-user-id` to an untrusted
caller. In a production service, derive scope from server-validated caller
authentication on every request, enforce authorization before all memory
operations, and use least privilege.

The alternative native tool's `{{$userId}}` resolver uses `x-memory-user-id`
when supplied, otherwise the caller's Entra TID/OID. That header must also be
trusted backend input; this lab deliberately does not send it.

## Prerequisites and authentication-only preflight

- Python 3.10+, Azure CLI with Bicep, and two **different real Entra users in the
  same tenant** able to acquire delegated Graph tokens and call `/me`.
- Deployment permissions including role assignment creation.
- A region with preview Memory Store support and quota for GPT-4.1-mini
  `2025-04-14` GlobalStandard and `text-embedding-3-small` version `1` Standard.
  Model/preview compatibility must be confirmed for your subscription; a
  successful Bicep build does not establish service availability.
- The template grants the presenter and the project's managed identity
  **Foundry User** at account scope. The project identity needs access to its
  underlying model resources, as required by the official memory documentation.

From this folder, prepare sessions **interactively before** the noninteractive
demo/runner. Do not supply passwords or token strings to the demo:

```bash
export AZURE_TENANT_ID="<tenant-uuid>"
az login --tenant "$AZURE_TENANT_ID"
az account set --subscription "<subscription-id>"
mkdir -p .azure/user2
export SECOND_AZURE_CONFIG_DIR="$PWD/.azure/user2"
AZURE_CONFIG_DIR="$SECOND_AZURE_CONFIG_DIR" az login \
  --tenant "$AZURE_TENANT_ID" --allow-no-subscriptions
# In that second login, select the OTHER real user.
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py --preflight
```

Protect both CLI caches; do not commit or share them. `--preflight` authenticates
via CLI/Graph but makes **no Foundry, model, provisioning or paid inference
calls**. It does not prove Foundry access, quota, preview support or model
compatibility. Live execution repeats preflight before creating a store. Failed
authentication never falls back to synthetic users or a one-user demo.

## Deploy and run

After the preflight succeeds, from the repository root, retain both exported
identity variables and use the existing runner:

```bash
dotnet run --file deploy.cs -- --subscription "<subscription-id>" --demo 31
```

The root runner automatically runs `demo.py --preflight` before provider
registration or resource-group changes. The explicit preflight above is also
useful for checking authentication independently. Other provisioning tools must
run it explicitly before deployment: the live entry point cannot undo
infrastructure already provisioned externally. The template
accepts `principalId` and `location` and returns `endpoint`, `deployment`,
`projectEndpoint`, and `embeddingDeployment` for the shared runner contract.

Standalone deployment, from this folder:

```bash
python demo.py --preflight
RG=rg-foundry-native-agent-memory-demo
LOCATION=eastus2
PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv)
az group create --name "$RG" --location "$LOCATION" --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --name lab31 --resource-group "$RG" \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location="$LOCATION"
export FOUNDRY_PROJECT_ENDPOINT=$(az deployment group show -g "$RG" -n lab31 --query properties.outputs.projectEndpoint.value -o tsv)
export AZURE_OPENAI_DEPLOYMENT=$(az deployment group show -g "$RG" -n lab31 --query properties.outputs.deployment.value -o tsv)
export AZURE_OPENAI_EMBEDDING_DEPLOYMENT=$(az deployment group show -g "$RG" -n lab31 --query properties.outputs.embeddingDeployment.value -o tsv)
python demo.py
```

The Bicep template disables API keys, enables public networking for the workshop,
and applies the repository's security tags. GlobalStandard processing may occur
outside the resource region. Use synthetic coffee preferences only: user object
IDs, temporary resource IDs, preferences and actual returned usage are printed.

## Limits, errors and costs

This is preview software pinned to `azure-ai-projects==2.7.0`. The SDK's update
poller returns `MemoryStoreUpdateCompletedResult.memory_operations` directly,
not an extra `.result` wrapper; operation kinds are `create`/`update`/`delete`.
The installed SDK wire-format tests cover the actual message serialization,
polling and deserialization, not just hand-written stand-in payloads.

- A successful run makes **five model response requests**, two native extraction
  operations, and multiple native searches. Extraction and search can incur chat
  and embedding charges beyond the five responses. Polling/search visibility
  checks can add requests; no fixed monetary estimate is promised.
- Search retrieves at most **20 memories** per call in this dedicated tiny store.
  Exact random-code extraction/recall is a narrow teaching assertion, not a
  memory quality or universal isolation benchmark.
- Extraction and each search-state wait are bounded to **180 seconds**;
  Foundry HTTP reads and OpenAI requests to **60 seconds**, connections to
  **15 seconds** for the project client. Native SDK poller timeout is a wait
  bound, not server-side cancellation; a cloud update can still be in flight
  when cleanup starts. Network operations/cleanup can extend total elapsed time.
- Automatic SDK HTTP retries are disabled. No inference/search error is treated
  as empty memory. Failed, superseded, empty or incomplete extraction, wrong
  scope, leaked code, invalid JSON, wrong answer, deletion rejection, timeout or
  cleanup failure exits nonzero. 403/RBAC propagation, 429/quota, unsupported
  models and unavailable preview operations remain explicit errors.
- A one-day memory TTL is defense in depth, **not** a substitute for deletion.
  This verifies current search/item visibility after scope deletion, not physical
  erasure timing, backup retention or deletion of every historical service log.

## Offline validation versus live verification

```bash
python -m unittest discover -s . -p 'test_demo.py' -v
az bicep build --file infra/main.bicep --stdout >/dev/null
az bicep lint --file infra/main.bicep
```

Tests are offline: actual SDK model instances, mocked service boundaries, and
the installed SDK's real native-memory operations through an in-memory HTTP
transport. They check extraction, independent conversations/no history replay,
scopes, native search, forgetting, failure handling and cleanup. They do **not**
prove live extraction quality, identity permissions or Foundry availability.
No Azure deployment or paid request was used to validate this implementation.

## Cleanup

Normal and failed runs attempt all known temporary deletions. Store and agent
cleanup are registered before creation, so a lost create response still triggers
deletion by the printed unique name. Conversation/response IDs are registered
once returned. A process kill, network outage or lost response before an ID is
returned can leave resources; finalizers cannot guarantee cleanup in those cases.
Inspect the printed `coffee-memory-*`, `memory-reader-*`, conversation and
response IDs in this dedicated project. Remove only this run's resources.

Infrastructure/model deployments remain after execution. After confirming the
subscription/resource group, delete the dedicated lab group:

```bash
az group delete --subscription "<subscription-id>" \
  --name rg-foundry-native-agent-memory-demo --yes --no-wait
```

This is also the root runner's default group name. If an earlier manual run used
`rg-foundry-native-memory-demo` or a custom prefix, delete that actual dedicated
group instead; confirm deployment state before deletion.

Also sign the second user out when finished:

```bash
AZURE_CONFIG_DIR="$SECOND_AZURE_CONFIG_DIR" az logout
```

## Sources

- [Official Memory usage guide: setup, project-identity RBAC, scope, native update,
  search and delete](https://learn.microsoft.com/azure/foundry/agents/how-to/memory-usage)
  (reviewed 26 October 2026; page dated 6 October 2026).
- [Memory concepts](https://learn.microsoft.com/azure/foundry/agents/concepts/what-is-memory)
- [Azure AI Projects Python SDK](https://learn.microsoft.com/python/api/overview/azure/ai-projects-readme)

The official guide offers both auto-writing tools and low-level API workflows;
this lab intentionally selects the latter for deterministic forgetting semantics.
