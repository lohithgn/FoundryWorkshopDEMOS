# Foundry workshop demos

A collection of **46 standalone labs** for Microsoft Foundry and Microsoft
Agent Framework. Each lab combines a small synthetic scenario, deployable
infrastructure, Python code, tests, and presentation/cleanup instructions.
Some orchestration runs locally; other labs create managed prompt agents,
hosted runtimes, or service-native tools. These are deliberately distinguished
in the lab descriptions.

- [Lab catalog](./demos.md): what each lab does and its implementation boundary.
- [Verification results](./tests.md): dated offline, deployment and live evidence.
- [Backlog](./BACKLOG.md): acceptance criteria and remaining work.
- [Cleanup](#remove-all-lab-resource-groups): preview and remove workshop resources.

## Choose a learning path

| Focus | Suggested labs |
| --- | --- |
| First model calls and output quality | 14 fundamentals, 1 routing, 16 structured outputs, 15 benchmarking |
| Agent orchestration and tools | 2 workflow, 13 orchestration patterns, 4 Toolbox pricing, 20 approval |
| Retrieval and knowledge | 17 application RAG, 11 minimal Foundry IQ, 27 managed Blob ingestion, 21 application permissions, 45 native permissions/Fabric |
| Skills, state and automation | 19 local state, 28-30 skills, 31 native memory, 32 browser, 33 skill releases, 42 managed harness/Tool Search, 43 routines |
| Evaluation, safety and operations | 6 Content Safety, 22 injection defenses, 38 guardrails, 8/9 evaluation/red teaming, 23 CI gates, 25 resilience, 39 gateway limits, 44 optimization |
| Documents, audio and enterprise context | 26 vision, 34 Document Intelligence, 40 voice, 41 Content Understanding, 35 Work IQ |
| Identity, deployment and fleet operations | 12 Entra Agent ID, 18 tracing, 24 publishing, 36 hosted code, 37 monitoring, 46 fleet governance |
| Gateway response reuse | 7 semantic caching |

This is a navigation guide, not a dependency chain. Start with one lab and
check its prerequisites and latest live status before selecting the next.
Private-network deployment, native File Search and general Teams/Copilot/A2A
publication are not implemented as dedicated labs. Work IQ's specific A2A tool
integration is not a general publication lab.

## Deploy and test selected labs

Requires Python 3.10-3.13 for running all demos (the red-teaming SDK restricts
Python versions), Azure CLI, Bicep (`az bicep version`), and an interactive
Azure user with resource creation and role-assignment permissions. See each
demo's prerequisites for model quota and access requirements.

The shared runner uses POSIX virtual-environment paths (`.venv/bin/python`);
run it on macOS, Linux or WSL. Lab 12's separate tenant setup additionally uses
PowerShell. Windows users should follow each lab's platform-specific guidance
rather than assuming every Bash example runs unchanged in PowerShell.

Prefer selecting a small subset rather than
deploying every resource and running every paid operation at once:

```bash
az login
python3 deploy.py --subscription "<subscription-id>" --demo 1
python3 deploy.py --subscription "<subscription-id>" --demo 20 --demo 25
```

`--demo` accepts a demo number or exact folder name and can be repeated. Selected
demos run in numeric order; prerequisites for unselected demos are not required.
Labs 38-46 add the nine selected coverage-review priorities. The duplicate
Content Understanding selection is one lab. Private networking, native File
Search, and Teams/Copilot/general A2A publication were not selected.
Do not run all nine as one deployment before reading their prerequisites:
native voice, managed harness, routines, optimization, and governed IQ have
feature-specific model, region, preview, identity, or tenant requirements.
Missing native service evidence is a failed or blocked live test, never an
offline substitute. See [verification results](./tests.md) for each stage.
The runner checks the selected location and authenticated native-service
prerequisites for labs 40 and 41 before provider registration or resource-group
creation. Their default live runs use synthetic audio rather than requiring
an attendee microphone.
Labs 42-44 also have read-only native-access preflights. Because these checks
need a real project and model, the runner executes them after ARM deployment
and saving outputs, but before paid agent/dataset/optimizer runs. Lab 42 must
be selected separately with a supported location, for example:

```bash
python3 deploy.py --demo 42 --location swedencentral
python3 deploy.py --demo 43 --demo 44
```

Their model/project access checks do not prove advance permission to create
agent IAM assignments. Read each lab's role requirements before deployment.
All nine additions are implemented and Azure-deployed, with 178 passing local
tests. Current native live gates remain blocked for lab 43 (agentic-token
issuance; tenant/service cause unresolved) and lab 45 (second real user and
enabled/licensed Fabric prerequisites). Lab 46's API gates passed, but its
portal Control Plane UI is not verified. See [verification results](./tests.md)
before presenting these labs as live-ready.
Historical passes also have implementation-specific limits: lab 3's current
with/without-search comparison and lab 11's project-enabled template have not
been live-verified; lab 7's replacement gateway cache still needs its live hit
test. Do not extend an earlier implementation's pass to a changed workflow.
Lab 32 requires a Browser Automation-supported region such as `eastus`, not the
runner's `eastus2` default. The runner rejects unsupported template locations
before any Azure resource changes. Present lab 32 separately using its manual
connection setup instructions; selecting every lab with default settings now
fails that regional check rather than partially provisioning the collection.
Lab 35 additionally needs a tenant-admin-approved Work IQ OAuth application,
Copilot Credits billing and individual user authorization. Its first deployment
without that connection deliberately fails the live phase; complete the
[Work IQ setup](./35-work-iq/README.md) before expecting an end-to-end pass.

Before selecting **lab 12**, complete the tenant setup in
[Entra Agent ID](./12-entra-agent-id/README.md). It requires separate tenant
roles/admin consent and sets `ENTRA_TENANT_ID`, `ENTRA_BLUEPRINT_CLIENT_ID`,
`ENTRA_AGENT_ID`, and `ENTRA_BLUEPRINT_SECRET`. Run the root command from that
same PowerShell session (change back to the repository root). The runner checks
these prerequisites before any Azure changes and passes only the non-secret
agent ID to Bicep. The blueprint secret is not a deployment parameter or output.
Labs 21 and 31 additionally require `AZURE_TENANT_ID` and `SECOND_AZURE_CONFIG_DIR`,
an isolated Azure CLI configuration authenticated as a different real user in
the same tenant. The runner invokes each selected lab's `--preflight` after installing
its dependencies but before provider registration or resource creation. Its
README documents the Graph checks; lab 21 also covers optional restricted-group setup.
Labs 23 and 24 require their documented evaluation/publishing permissions and
regional API support. Lab 23's workflow, protected environment, scoped managed
identity, and GitHub OIDC are configured and live-verified in this repository;
other repositories need their own exact subject, environment, and variables.
See its README for owner approval and baseline/regression runs.
Lab 24 uses the documented legacy Agent Application ARM
publishing route and additionally needs subscription Activity Log read access;
read its API/availability caveats before presenting. To present individual demos,
use `--demo` or their standalone deployment instructions.

Selecting every lab is an advanced, paid operation, not a one-command quick
start. Labs 32 and 42 have incompatible location requirements, so there is no
single location that deploys the entire collection successfully. Select them
in separate invocations and complete their manual connections first.

Omitting `--demo` selects all 46 labs. This is not a supported single-region
end-to-end quick start for the current collection; use explicit selectors
as shown above.

If `--subscription` is omitted, the script uses the currently selected Azure CLI
subscription. It does not change that selection. Optional flags:
`--location eastus2`, `--prefix rg-foundry`, and `--principal-id <user-object-id>`
(useful when Microsoft Graph access is restricted).
For templates exposing `searchLocation`, `--search-location` keeps Search in a
separate supported region. Live lab 17 required West US 2 Search because East
US 2 lacked capacity:

```bash
python3 deploy.py --demo 17 --location eastus2 --search-location westus2
```

Keep this override on subsequent deployments to that resource group: Azure
cannot move an existing Search service to another region in place.

### Required workshop tags

Every lab deployment assigns `SecurityControl=Ignore` and
`SecurityExemption=StorageAccountKeyAccess` to its resource group and every
taggable resource, including model deployments and Foundry projects. The
standalone lab scripts preserve their `managedBy` and `lab` ownership tags.
Resource tags are explicit because Azure does not inherit resource-group tags.
Lab 24 also sets both tags under `properties.tags` when creating or updating its
Agent Applications and managed agent deployments at runtime.

Azure does not support resource tags on role assignments, federated identity
credentials, policy assignments, API Management API policy resources, individual
Function definitions, Blob services, Blob containers,
Blob lifecycle management policies, and Foundry project connections used by
these templates. Entra directory
objects and data-plane objects are not
ARM-tagged resources. These objects are excluded; their taggable parent resources
receive both tags.

These changes apply on deployment; they do not retroactively update resources
already running in Azure. Redeploy the infrastructure to update existing tags.
The root runner also executes paid live demos, so use a lab's documented
`az group create` and `az deployment group create` commands for infrastructure-only
updates. Tag values must retain the exact casing shown above.

The script:

1. Discovers every immediate demo folder containing `infra/main.bicep` or
   `requirements.txt`, rejecting incomplete folders rather than silently skipping them.
2. Compiles/lints Bicep, creates/reuses each demo's `.venv`, installs its
   requirements, and runs its local unit tests before making Azure changes.
3. Registers the resource providers referenced by the compiled templates and creates a dedicated resource group
   named `<prefix>-<demo-folder-without-number-prefix>-demo` for each demo.
4. Deploys the existing template, passing `principalId` and `location`.
5. Saves deployment outputs to `<demo>/.azure/deployment.json`, maps the `endpoint`
   output plus optional `deployment`, `embeddingDeployment`, `projectEndpoint`, and `toolEndpoint`
   outputs to the demo's environment, and runs `demo.py`.
   Foundry IQ also uses `searchEndpoint`; Entra Agent ID uses `agentIdentityId`.
   Prompt injection uses `contentSafetyEndpoint` for its separate safety classifier.
   Fundamentals uses `deniedEndpoint` for its intentional authorization failure;
   benchmarking uses `alternateDeployment` and `routerDeployment`.
   Tracing uses `applicationInsightsConnectionString`; lifecycle operations use
   `projectResourceId`.
   Lab 27 also maps `searchResourceId`, `storageEndpoint`, and `storageResourceId`
   to `AZURE_SEARCH_RESOURCE_ID`, `AZURE_STORAGE_BLOB_ENDPOINT`, and
   `AZURE_STORAGE_RESOURCE_ID` for its portal-connected Blob knowledge base.
6. Saves live output to `<demo>/.azure/demo.log` and stops with a nonzero exit code
   if any step fails. Newly assigned 403 access errors and 404
   `DeploymentNotFound` errors, plus explicit DNS name-resolution failures, are
   visibly retried at 30-second intervals, up to 11 attempts. Other errors
   (including TLS/certificate errors) fail immediately. Each retry restarts the
   demo, so prompts completed before the error may be sent again and incur charges.

If DNS failures persist, compare the endpoint in `<demo>/.azure/deployment.json`
with the account's endpoints in Azure, then check your local DNS/VPN/network.
A successful ARM deployment does not guarantee the endpoint is immediately
resolvable from your machine. The runner preserves every failed attempt in the
live log and exits nonzero if its retry limit is reached; it does not change
endpoints, bypass certificate validation, or replace failed responses.

**This command creates billable Azure resources, makes live model requests,
runs managed evaluation/red teaming, and submits a paid fine-tuning job.**
The added labs can also incur gateway, audio, Content Understanding, Search,
telemetry, dataset-generation and optimization charges. Native routine tests
must remove their temporary triggers; infrastructure remains deployed until
explicit cleanup.
Fine-tuning only reports job submission by default; it does not wait for training
to finish. Every rerun submits another training job. Use that demo's `--job-id`
option directly to inspect an existing job instead.
Resources remain deployed for presenting. Rerunning uses the same resource groups
and deployment names and repeats the tests and live calls. The script does not
automatically delete resources on failure. The ignored `.azure` state/logs contain
resource identifiers and demo responses, not API keys or access tokens.
Red-team reports/SDK logs may contain disturbing safety-test text; do not
project raw transcripts or commit them.

To add another demo, use the same folder convention: `infra/main.bicep`,
`requirements.txt`, `demo.py`, and `test_demo.py`. Templates must accept `principalId`
and `location` and expose a string output `endpoint`, plus `deployment` when
using an inference model; Python demos consume `AZURE_OPENAI_DEPLOYMENT` when
applicable and the endpoint appropriate to their API, exit
nonzero on failure, and validate the live response. Account-level inference uses
`AZURE_OPENAI_ENDPOINT`; project APIs use `FOUNDRY_PROJECT_ENDPOINT` from
`projectEndpoint`; the hosted pricing tool uses `WORKSHOP_TOOL_ENDPOINT` from
`toolEndpoint`. Embeddings use `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` from
`embeddingDeployment`. Content Safety has no model deployment and consumes its
Cognitive Services URL through the shared endpoint variable. The Decision Model
consumes its Foundry services URL through that variable. Foundry IQ uses
`AZURE_SEARCH_ENDPOINT` from `searchEndpoint`; the Entra identity demo receives
`ENTRA_AGENT_ID` from `agentIdentityId` and needs its separate tenant setup.
Fundamentals maps `deniedEndpoint` to `WORKSHOP_DENIED_ENDPOINT`. Benchmarking
maps `alternateDeployment` to `AZURE_OPENAI_ALTERNATE_DEPLOYMENT` and
`routerDeployment` to `AZURE_OPENAI_ROUTER_DEPLOYMENT`. Prompt Shields maps
`contentSafetyEndpoint` to `CONTENT_SAFETY_ENDPOINT`.
Tracing maps `applicationInsightsConnectionString` to
`APPLICATIONINSIGHTS_CONNECTION_STRING`; publishing maps `projectResourceId` to
`FOUNDRY_PROJECT_RESOURCE_ID`.
The runner only maps the output names listed above. A lab needing additional
outputs must either load them explicitly from its saved deployment record
(as several newer labs do) or add a tested mapping to the runner. Numbered
folders are processed in numeric order.
Lab 27 additionally uses `searchResourceId`, `storageEndpoint`, and
`storageResourceId` as documented above, along with the project and embedding outputs.

## Demos and tracking

- [Model Router instructions and cleanup](./1-model-router/README.md)
- [Workflow Agent: writer -> editor](./2-workflow-agent/README.md)
- [Simple WebIQ Agent: web search with citations](./3-webiq-agent/README.md)
- [Agent with Simple Tools: Azure Function through Foundry Toolbox](./4-agent-tools/README.md)
- [Microsoft Decision Model: typed support-ticket routing](./5-decision-model/README.md)
- [Content Safety: category severity and a simple policy gate](./6-content-safety/README.md)
- [Semantic caching: APIM Consumption AI gateway and Azure Managed Redis](./7-semantic-cache/README.md)
- [Agent evaluation: Foundry cloud coherence evaluation](./8-agent-evaluation/README.md)
- [Agent red teaming: a small managed baseline scan](./9-agent-red-teaming/README.md)
- [Fine-tuning: managed classification and verified local Phi domain adaptation](./10-fine-tuning/README.md)
- [Foundry IQ: a tiny Learn-based managed knowledge base](./11-foundry-iq/README.md)
- [Entra Agent ID: an Agent Framework tutor with its own identity](./12-entra-agent-id/README.md)
- [Agent Framework: Sequential, Concurrent, Group Chat, and Magentic](./13-agent-orchestrations/README.md)
- [Foundry fundamentals: project, deployment, streaming, and errors](./14-foundry-fundamentals/README.md)
- [Model benchmark: two models versus Model Router](./15-model-benchmark/README.md)
- [Prompt design and schema-constrained outputs](./16-structured-outputs/README.md)
- [RAG fundamentals: ingestion, chunking, and retrieval comparisons](./17-rag-fundamentals/README.md)
- [Agent tracing: correlated model/tool spans and controlled failures](./18-agent-tracing/README.md)
- [Conversation state, user isolation, and context limits](./19-conversation-state/README.md)
- [Human approval: exact-action approval and idempotent synthetic bookings](./20-human-approval/README.md)
- [Permission-aware retrieval with authenticated identities](./21-permission-aware-retrieval/README.md)
- [Prompt injection: trust boundaries, Prompt Shields, and validated output](./22-prompt-injection/README.md)
- [Evaluation-driven CI/CD and GitHub OIDC](./23-evaluation-cicd/README.md)
- [Agent application publishing, versioning, and rollback](./24-agent-lifecycle/README.md)
- [Resilience, quotas, deadlines, and cost controls](./25-resilience-cost/README.md)
- [Multimodal extraction with synthetic images and uncertainty](./26-multimodal/README.md)
- [Foundry IQ with Blob knowledge sources and a persistent new-portal agent](./27-FoundryIQwithKB/README.md)
- [Agent skills: version-pinned workshop instructions from Foundry](./28-agent-skills/README.md)
- [Toolbox skill discovery and progressive disclosure](./29-toolbox-skill-discovery/README.md)
- [Skill-powered Code Interpreter analysis and verified artifacts](./30-skill-data-analyst/README.md)
- [Native Foundry memory across independent conversations](./31-native-agent-memory/README.md)
- [Browser runbook automation with explicit approval](./32-browser-runbook/README.md)
- [Skill evaluation, default promotion, pinned consumers, and rollback](./33-skill-release-governance/README.md)
- [Document Intelligence: invoice extraction through an agent's Function tool](./34-document-intelligence/README.md)
- [Work IQ in a persistent Foundry agent with per-user OAuth](./35-work-iq/README.md)
- [Custom-code Foundry hosted agent with a simple UI](./36-hosted-agent/README.md)
- [Live production-quality monitoring and recovery with a simple UI](./37-production-monitoring/README.md)
- [Native Foundry runtime guardrails](./38-native-guardrails/README.md)
- [Enforced AI Gateway token limits and quotas](./39-gateway-token-limits/README.md)
- [Managed voice-based prompt agent](./40-voice-agent/README.md)
- [Schema-based Content Understanding](./41-content-understanding/README.md)
- [Managed Copilot harness and native Toolbox Tool Search](./42-managed-harness-tool-search/README.md)
- [Native agent timer routines](./43-agent-routines/README.md)
- [Cloud evaluation breadth and native agent optimization](./44-evaluation-optimizer/README.md)
- [Permission-native Foundry IQ and Fabric IQ](./45-governed-iq/README.md)
- [Cross-project fleet governance](./46-fleet-governance/README.md)
- [Backlog](./BACKLOG.md)

Each demo has standalone Bicep, local Python code, dependencies, tests, and manual
deployment/cleanup instructions. Model names, versions, SKUs and capacities are
lab-specific: consult the lab's template and README rather than assuming one
shared model default. Model Router and Microsoft Decision use specialized
models; voice and Content Understanding have additional model requirements.
Content Safety needs no generative-model deployment, while retrieval and
semantic caching can add embedding deployments. Lab 7's cache runs in gateway
policies with Azure Managed Redis, not in application code or a native Foundry
cache toggle.
Model access, quota, regional availability, Web Search policy, Toolbox/evaluation/
red-teaming/agentic-retrieval availability, fine-tuning access, and Entra Agent ID
tenant permissions must be checked before presenting. Foundry IQ provisions
Basic Azure AI Search, which has ongoing charges while deployed. The Entra demo
uses a one-day local-development blueprint secret; production should use
managed identity federation/certificates. All included example data is
synthetic/non-sensitive.

Lab 28 requires the preview Foundry Skills API. Its local harness downloads a
pinned skill version and explicitly injects it into a Foundry prompt agent;
it does not deploy a custom hosted application or use automatic skill discovery.

Labs 29-33 extend that introduction with Toolbox/MCP discovery, native Code
Interpreter, managed memory, remote browser automation, and independent skill
release governance. Skills are reusable instructions, not executable tools:
the execution and persistence capabilities come from the respective Foundry tools.
Lab 31 uses the low-level native Memory Store API with explicit injection of
search results into fresh prompt-agent conversations, not an attached auto-writing
memory tool. The Python orchestration/providers still run locally; these labs do not silently
claim hosted-container deployment. Read lab 31's scope/identity requirements and
lab 32's remote-browser/controlled-site setup before selecting them. Browser
submission requires explicit approval and is not enabled by the all-demo runner.
Lab 32's configuration-only preflight requires
`BROWSER_AUTOMATION_CONNECTION_NAME` or `BROWSER_AUTOMATION_PROJECT_CONNECTION_ID`.
Provision its infrastructure and create the manual project-managed-identity
Browser Automation connection before the live step; the runner does not create
that connection. Use `--demo 32 --location eastus` after that setup.
Skills/discovery and memory have preview dependencies; offline tests do not
establish regional API availability. The latest recorded verification for labs
28-33 is offline-only; see the dated [results](./tests.md) rather than interpreting
this as a fresh deployment attempt.

Demos 1-7 are intended for short terminal presentations. Managed evaluation and
red teaming can take several minutes; submit fine-tuning ahead of time and show
the existing job during the presentation. Each demo can be deployed and run
independently using its own instructions. See [live test results](./tests.md)
and the [backlog](./BACKLOG.md) for actual service verification and remaining
blockers. Offline tests and template compilation are not proof of live access.

Labs 14-26 extend the feature demonstrations with engineering exercises. Start
with fundamentals and structured outputs, then retrieval, state, tools, tracing,
and evaluation before publishing or advanced identity scenarios. Expected
failures are explicitly labeled; mocked unit-test results are not live service
verification. Approval lab 20 proposes an action by default and never grants
approval automatically. Follow its README to approve or reject the exact proposal.
Lab 14's real permission-denial exercise requires its documented restricted
identity setup; an administrator with inherited inference rights cannot prove
the negative case simply by using a second endpoint. Previously generated
development certificates may have expired.
The CI regression variant in lab 23 is intentionally expected to exit nonzero.
Search and monitoring resources can have ongoing charges while provisioned.

## Test the runner without Azure

```bash
python3 -m unittest discover -s . -p 'test_deploy.py' -v
```

These shared tests check discovery, configuration and deployment contracts;
they do not call live Azure services. Each lab also has a `test_demo.py` suite.
Run that suite from its lab folder after following its dependency setup.
The latest recorded shared run is 37/38 passing, with one pre-existing
lab-13 infrastructure tag-layout assertion failure; the new labs have
178/178 passing local tests. See [results](./tests.md) for dates and limitations.

## Presenter reference material

- [Agent definitions, runtime hosting and external registration](./Presentation/agent-types.md).
- [Built-in web search versus the separate Web IQ APIs](./Presentation/webiqVSwebsearchtool.md).
- [Private-network architecture discussion and coverage gap](./Presentation/foundryInfrastructure.md).

Reference notes explain concepts; they are not additional implemented labs or
evidence of live feature availability.

## Remove all lab resource groups

Use [removeAll.sh](./removeAll.sh) with Bash, Azure CLI, and `jq`. Preview first:

```bash
./removeAll.sh --subscription "<subscription-id>"
./removeAll.sh --subscription "<subscription-id>" --execute
```

The script covers all 46 labs: root and standalone runner defaults, documented
manual/historical names, and saved deployment records for the selected subscription.
Legacy output-only `deployment.json` files have no subscription/group metadata;
the script warns and uses known group names instead. Add `--group` if such a
deployment used an unrecorded custom name.
It matches exact names, not broad prefixes, and never changes the Azure CLI's
selected subscription. Repeat for other subscriptions. Use repeated `--prefix`
options for custom runner prefixes or `--group` for unrecorded manual group names.
Names alone are not proof of ownership: review the preview carefully, especially
if a group contains resources unrelated to the workshop.

**Execution now includes irreversible purge, scoped only to workshop groups.**
The preview includes existing soft-deleted Foundry/Cognitive Services accounts and
API Management services, even when their resource groups are already gone.
After deleting groups, the script rediscovers and purges newly soft-deleted
accounts/services, retrying up to six times at ten-second intervals and verifying
their absence. Azure may take additional time to release names.

Log Analytics workspaces are permanently deleted with `--force`. Azure requires
recovering already soft-deleted workspaces first; the script temporarily recreates
their original workshop group if necessary, recovers the workspace, permanently
deletes it, then deletes that group. Recovery/creation happens only with `--execute`.
These permanent-delete operations cannot be undone. Purge can require
subscription-level permissions beyond resource-group deletion permissions.

`--execute` requires typing `DELETE`; add `--yes` for unattended cleanup.
Deletion waits and verifies absence, continues after individual operation failures,
and exits nonzero if any deletion, purge, or verification fails. Resource locks
are not removed. The script handles the lab services with supported purge APIs,
not arbitrary retention stores such as soft-deleted Storage blobs. Local logs/state
and GitHub configuration are retained. See `./removeAll.sh --help` for all options.

**Entra tenant objects aren't deleted with the resource group.** Follow demo 12's
separate cleanup instructions for its child agent, blueprint principal, blueprint,
and development credential. Also follow labs 14 and 23 for their separate
identity/credential and GitHub configuration cleanup.
