# Microsoft Foundry - Semantic caching with Azure API Management

A short demo of **centralized semantic caching in the Azure API Management
(APIM) AI gateway**. APIM policies check and store responses in **Azure Managed
Redis**, using a Foundry embeddings deployment to find similar prompts.
The application sends normal chat requests; it contains no cache, vector
comparison, Redis client, or embedding calls.

```text
Python / any chat client
  -> APIM Consumption: validate caller, semantic-cache-lookup
       -> Foundry text-embedding-3-small -> Azure Managed Redis / RediSearch
       -> HIT: return the stored response
       -> MISS: Foundry gpt-4.1-mini -> semantic-cache-store -> Redis
```

## Lowest-cost workshop configuration

- **APIM Consumption**, capacity `0`: serverless, billed by operations rather
  than an always-on Developer/Basic/v2 instance. Microsoft lists the semantic
  caching policies and external Redis caching as supported on Consumption.
- **Azure Managed Redis Balanced B0 (0.5 GB), high availability disabled**:
  the smallest SKU, with no replica. This is a development/test configuration,
  **without an availability SLA**; cache loss and maintenance downtime are possible.
  It is not the retired Azure Cache for Redis Basic offering.
- Redis enables **RediSearch**, **EnterpriseCluster**, **NoEviction**, TLS, and
  access-key authentication. APIM currently requires a Redis connection string,
  not Entra authentication, for its external cache. The template reads the key
  server-side; it never exports it to the application or deployment outputs.
- Foundry generation and embeddings use APIM's system-assigned managed identity
  with **Cognitive Services OpenAI Contributor** RBAC (role ID
  `a001fd3d-188f-4b5d-821b-7da978bf7442`). This is broader than inference-only
  OpenAI User; the README does not claim least-privilege production RBAC.
  Foundry API keys stay disabled.
  The caller uses `az login`; APIM validates its tenant, audience, and presenter
  object ID **before** cache lookup. No APIM subscription key is needed.

Redis still has standing charges while deployed. APIM operations, embeddings,
and uncached generation can also incur charges; a hit still needs an embedding
lookup. Check regional availability and current prices before deploying.

## Deploy

Requires Python 3.10+, Azure CLI/Bicep, a signed-in user with resource creation
and role-assignment permissions, APIM Consumption and Managed Redis availability,
and quota for `gpt-4.1-mini` (`2025-04-14`, Global Standard) and
`text-embedding-3-small` (`1`, Global Standard). Endpoints are public for this workshop.
Provisioning APIM and Redis can take several minutes.

**Verification status:** on 2026-11-09, an isolated live run returned `4` for
all three requests. A separate traced session showed backend forwarding for
the first request and none for the paraphrase, with the same response ID and
answer. This verifies a cache miss followed by a paraphrase hit for that run,
not a guarantee for other prompts. See [the recorded test status](../tests.md).
The historical local-cache pass does not validate this replacement; model
responses alone are not proof of a live cache hit.

Both model deployments use `GlobalStandard`; inference may be processed outside
the resource's region. In Sweden Central, `text-embedding-3-small` version `1`
supports Global Standard but not regional `Standard`. If a previous deployment
failed with `InvalidResourceProperties` for that SKU, retry using the updated
template with the same region and Redis SKU; no cleanup is required for this
validation error. The Bicep upgrade warning is unrelated.

If deployment fails with `ValidationError`, target `resourceId`, and
`Value should represent absolute http URL`, the APIM external-cache registration
requires an absolute management URL, not a relative ARM resource ID. The template
now builds that URL using the current Azure cloud's Resource Manager endpoint.
Retry the updated template with the same region and Redis SKU; no cleanup is
required for this validation error.

The external-cache resource is named `default`, matching its
`useFromLocation` setting. APIM's cache identifier must be `default` or a valid
Azure region identifier; an arbitrary name such as `semantic-cache` can deploy
but leave the gateway unable to select the cache. A trace then reports
`No appropriate cache found for provided policy configuration` and skips
semantic lookup and storage. Redeploy the updated template to register the
`default` cache; incremental deployment may retain the obsolete `semantic-cache`
registration, which can be removed from APIM after validation without deleting
Redis.

From this folder, the standalone runner provisions resources, runs offline tests,
and runs the live demo; its recorded group can later be removed with `cleanup.py`:

```bash
az login
python3 deploy_lab.py --location eastus2
```

If Azure reports `CacheCapacityUnavailable` for `Balanced_B0`, you can explicitly
try a larger Balanced SKU in the same region:

```bash
python3 deploy_lab.py --location eastus2 --redis-sku Balanced_B1
python3 deploy_lab.py --location swedencentral --redis-sku Balanced_B1
```

Supported workshop choices are `Balanced_B0` (default), `Balanced_B1`,
`Balanced_B3`, and `Balanced_B5`. Larger SKUs have higher standing costs;
check pricing before choosing one. This is not a guarantee of available
capacity. There is no automatic SKU upgrade or region fallback. Keep the same
`--redis-sku` on retries: the selected value is recorded in
`.azure/lab-deployment.json` for inspection, but omitting the option selects B0.
Do not delete retained resources merely to retry a capacity error. If Redis
already exists, changing its SKU is a resize request, subject to Azure's
supported transitions and possible disruption, not a fresh allocation.

Alternatively, deploy manually:

```bash
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
az provider register --namespace Microsoft.ApiManagement --wait
az provider register --namespace Microsoft.Cache --wait
RESOURCE_GROUP="rg-foundry-semantic-cache-demo"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group "$RESOURCE_GROUP" --name demo \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location=eastus2 \
    scoreThreshold=0.15 cacheDuration=300
```

For a manual SKU override, add `redisSku=Balanced_B1` to `--parameters`.
The repository-wide runner retains the B0 default; use this lab's standalone
runner or manual deployment for the override. High availability, RediSearch,
TLS, authentication, and cache policy settings are unchanged by this option.

For a manual deployment, export its outputs as follows. For a standalone-runner
deployment, use its recorded resource group and deployment name
`semantic-cache-demo` instead of `demo`.

```bash
export AZURE_OPENAI_ENDPOINT="$(az deployment group show -g "$RESOURCE_GROUP" \
  -n demo --query properties.outputs.endpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show -g "$RESOURCE_GROUP" \
  -n demo --query properties.outputs.deployment.value -o tsv)"
```

**The endpoint output now points to APIM (`https://...azure-api.net/ai`),
not directly to Foundry.** Existing deployments must be redeployed and their
environment variables refreshed. The repository-wide runner already maps these
outputs; `python3 ../deploy.py --demo 7` also deploys and exercises this lab.

## Run and present

```bash
python3 -m venv .venv
source .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py
python demo.py --session workshop-1
python demo.py --session workshop-1
```

Each normal run uses a fresh cache-session partition, so its first request starts
cold. The next question is a paraphrase; the last repeats the original. The demo
sends **every** request to APIM and prints elapsed time and response IDs.
The questions ask for the integer result of `2 + 2`, once using digits and once
using words. The runner requires the exact answer `4` so an unrelated or
incorrect model response cannot be presented as a successful demo.
`REUSED RESPONSE` means the gateway returned an ID already observed in this run;
identical answer text alone is not treated as evidence. No local result is
returned in place of a request. Reusing `--session` across processes demonstrates
Redis-backed reuse until expiry (default **300 seconds**). The first ID in a new
process is printed as `NEW RESPONSE ID`, even if Redis already served it.

The demo pauses two seconds between calls for cache storage/index visibility
and fails explicitly if no response reuse is observed. Paraphrase hits and
latency savings are not guaranteed. It does **not** invent cosine scores,
HIT/MISS headers, or generation/embedding counts that APIM hasn't reported.

### Confirm the cache in APIM

In the Azure portal, open the APIM service, then **APIs -> workshop-chat ->
Chat completions -> Test**. Supply `api-version=2024-10-21`, an Authorization
Bearer token from the signed-in presenter, a fixed `x-workshop-cache-session`
header, and a non-streaming body such as:

```json
{"messages":[{"role":"user","content":"What is 2 + 2? Reply with the integer only."}],"max_completion_tokens":120,"stream":false}
```

Obtain the token locally with
`az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken -o tsv`;
do not save it in source control or shared logs.
Trace the first request, then repeat it and try the paraphrase with the same
session. Inspect **llm-semantic-cache-lookup**, **llm-semantic-cache-store**,
and backend forwarding in the trace: a cache hit returns the stored response
without a generation call. This is the authoritative HIT/MISS check.

### Tune the gateway, not the application

Change Bicep `scoreThreshold` and `cacheDuration` and redeploy. The threshold
is **cosine distance**, unlike the previous Python similarity threshold:
**lower means stricter**. The default is `0.15`; the template allows `0.05`,
`0.10`, `0.15`, or `0.20`. Test with a fresh session after a policy change.
The policy names `llm-semantic-cache-lookup` and `llm-semantic-cache-store`
are the current names documented by Microsoft for the Azure OpenAI semantic
cache policies.
An outbound guard stores only HTTP 200 responses with a completed (`stop`),
nonempty answer; truncated answers are not admitted to the cache.

Only one deployment, fixed generation settings, non-streaming, single-user-turn
public workshop questions are demonstrated. Cache partitions include tenant,
presenter, deployment, policy version, and session. System messages are not
ignored for similarity. This is not production isolation for arbitrary chat
histories, generation parameters, tools, RAG data, or private content.
Production requires representative threshold tests, complete context/parameter
partitioning, invalidation, rate limiting, and appropriate network controls.
Semantic similarity can still return an incorrect, stale, or unsafe answer.

## Validate, troubleshoot, and clean up

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
python -m unittest discover -s . -p 'test_*.py' -v
```

Offline tests verify ordinary gateway requests, response-ID observations,
error propagation, authentication ordering, cache-policy configuration,
and lowest-cost infrastructure defaults. They do not emulate Redis vector
search or prove a live cache hit.

For 401, check the signed-in tenant/presenter, token audience, and policy.
For 403, allow APIM managed-identity RBAC to propagate. For 404, check deployment
names and the APIM `/ai` endpoint. If no responses are reused, inspect APIM traces,
the external-cache connection, RediSearch, expiry, and threshold.
APIM can pass through to the model when its cache is unavailable: a successful
chat response alone does **not** prove that caching works.
With `NoEviction`, a full cache rejects writes; monitor memory and expiry.

Delete resources promptly to stop standing charges:

```bash
# Standalone runner deployment only:
python3 cleanup.py
# Manual deployment only (deletes the entire dedicated workshop group):
az group delete --name "$RESOURCE_GROUP" --yes
```

- [APIM semantic caching setup and supported tiers](https://learn.microsoft.com/azure/api-management/azure-openai-enable-semantic-caching)
- [Semantic cache lookup policy](https://learn.microsoft.com/azure/api-management/llm-semantic-cache-lookup-policy)
- [External Redis cache and access-key requirement](https://learn.microsoft.com/azure/api-management/api-management-howto-cache-external)
- [Managed Redis tiers and high availability](https://learn.microsoft.com/azure/redis/overview)
- [RediSearch requirements](https://learn.microsoft.com/azure/redis/redis-modules)
- [APIM pricing](https://azure.microsoft.com/pricing/details/api-management/)
- [Managed Redis pricing](https://azure.microsoft.com/pricing/details/managed-redis/)
- [Feature F007](../BACKLOG.md#f007---microsoft-foundry-semantic-caching)
