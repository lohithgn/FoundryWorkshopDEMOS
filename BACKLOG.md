# Demo backlog

All 47 entries (labs 0-46) have implementations; unchecked criteria can still represent
missing live verification, optional exercises or genuine tenant/service
blockers. Status is based on the [dated verification report](./tests.md), not
on the existence of code or passing mocks. The 2026-11-03 documentation review
reconciled historical baseline results without rerunning paid Azure tests.
See the [catalog](./demos.md) for what each lab actually demonstrates.

## F000 - Microsoft Foundry: Model basics

**Type:** Feature

**Status:** Implemented; offline verification recorded in tests.md; live verification pending

**Demo:** [Model basics](./0-model-basics/README.md)

### Goal

As a beginner, I can explain temperature and nucleus sampling, distinguish
model choice from processing mode, and observe real chat, reasoning and batch
results without confusing illustrative probability math with service evidence.

### Acceptance criteria

- [x] Provide the standard Bicep, pinned dependencies, demo, tests and README.
- [x] Deploy keyless GPT-4.1-mini online/batch and o4-mini online with scoped RBAC.
- [x] Demonstrate synthetic probability math and repeated one-knob sampling calls.
- [x] Compare the same prompt with model-specific parameters and actual usage.
- [x] Persist/resume a native batch and strictly correlate/validate all results.
- [x] Integrate numeric selector 0 and deployment outputs in the shared runner.
- [x] Document prerequisites, paid operations, limitations and cleanup.
- [x] Pass the new lab's unit tests, focused shared regressions and Bicep checks.
- [ ] Deploy to Azure and verify sampling, reasoning and completed native batch.

## F001 - Microsoft Foundry: Model Router

**Type:** Feature  
**Status:** Implemented and live-tested on 2026-10-25  
**Demo:** [Model Router](./1-model-router/README.md)

### Goal

As a presenter, I can deploy a single model router endpoint and send different
prompts from my laptop to explain automatic model selection without building an app.

### Acceptance criteria

- [x] Provide a standalone Bicep template for a Foundry account, model router
  deployment, and the presenter's inference role.
- [x] Use Microsoft Entra authentication without storing API keys.
- [x] Provide a local Python example with simple, writing, and reasoning prompts,
  plus a custom-prompt option.
- [x] Display the actual serving model, elapsed time, token usage, and answer.
- [x] Document prerequisites, deployment, execution, limitations, and cleanup.
- [x] Compile/lint the infrastructure and pass local mocked tests.
- [x] Deploy to the target subscription and run the example against Azure.

### Live verification

The all-demo runner deployed the infrastructure to
`rg-foundry-model-router-demo` in East US 2 on 2026-10-25. All six local demo
tests passed, and all three live prompts returned nonempty answers with serving
model, latency, and token usage. The simple/writing prompts selected
`FW-GLM-5.3-Flash`; reasoning selected `grok-4-1-fast-reasoning`. Routing results
are illustrative, not guarantees. Resources remain deployed.

The runner and usage instructions are in [deploy.py](./deploy.py) and the
[repository README](./README.md). Deployment outputs and the live transcript are
saved locally under `1-model-router/.azure/` (ignored by Git).

### Scope

One account, one deployment, one local script. No UI, agents, database, or separate
deployments of the underlying models. Routing choices are not deterministic;
different prompts are not guaranteed to select different models.

## F002 - Microsoft Foundry: Workflow Agent

**Type:** Feature  
**Status:** Implemented; historical live pass after retry recorded in tests.md, not rerun during the documentation review
**Demo:** [Workflow Agent](./2-workflow-agent/README.md)

### Goal

As a presenter, I can show a writer-to-editor agent handoff using a local
Agent Framework workflow backed by a single Foundry model deployment.

### Acceptance criteria

- [x] Provide standalone Bicep for the account, project, model, and presenter RBAC.
- [x] Authenticate with Microsoft Entra ID, without API keys.
- [x] Execute writer then editor and display both stages.
- [x] Support a custom prompt and reject empty/incomplete stage output.
- [x] Include dependencies, offline tests, setup, talk track, costs, and cleanup.
- [x] Compile/lint Bicep and pass local tests.
- [x] Deploy and smoke-test both stages against the target Azure subscription (historical baseline).

### Scope

Local sequential orchestration; Foundry inference. No hosted workflow, portal
designer, UI, persistence, or additional infrastructure.

## F003 - Microsoft Foundry: Simple WebIQ Agent

**Type:** Feature  
**Status:** Historical with-tool Web Search live pass on 2026-10-26; current with/without-tool comparison not live-verified

**Demo:** [WebIQ Agent](./3-webiq-agent/README.md)

### Goal

As a presenter, I can ask a Foundry agent a public question and show actual web
search activity and source citations rather than relying on model memory.

### Acceptance criteria

- [x] Provide standalone Bicep for the account, project, model, and presenter RBAC.
- [x] Create a prompt agent with built-in Web Search and require tool usage.
- [x] Print the answer, completed search count, source titles, and URLs.
- [x] Fail explicitly when search evidence, citations, or answer text is absent.
- [x] Delete the temporary agent version after success or inference failure.
- [x] Document the WebIQ interpretation, Bing terms, costs, setup, and cleanup.
- [x] Compile/lint Bicep and pass local mocked tests.
- [x] Deploy and smoke-test search/citations under the target organization's policy.
- [ ] Live-test the current with/without-tool comparison; the historical pass covers with-tool default/custom questions only.

### Live verification

Redeployed `gpt-5-mini` in East US 2 under
`rg-foundry-webiq-agent-live-20261026` because the prior lab group was deleted.
Reproduced ARM `RequestConflict` from concurrent account child writes; fixed the
template by making the project depend on the model deployment.

Default and custom questions both completed with exit 0. Actual response JSON
contained one completed `web_search_call` each and three/five structured
`url_citation` annotations respectively. The default response cited Microsoft
Learn model-router pages; the custom response cited Agent Service sources.
Independent agent-version readback returned HTTP 404 for both deleted temporary
agents. Grounding checks were not relaxed. Nine offline tests and Bicep build/lint
passed. Evidence is retained under `3-webiq-agent/.azure/` in
`live-default-2026-10-26.log`, `live-custom-2026-10-26.log`, and
`live-2026-10-26.json`. The new infrastructure remains deployed and tracked for cleanup.

### Scope

"WebIQ" is this lab's historical label for built-in Web Search; it does not
call the separate Microsoft Web IQ APIs. Public web only; no private documents,
search index, separate Bing connection, or UI. See the
[web-grounding comparison](./Presentation/webiqVSwebsearchtool.md).

## F004 - Microsoft Foundry: Agent with Simple Tools

**Type:** Feature  
**Status:** Implemented and live-tested on 2026-10-26

**Demo:** [Agent with Simple Tools](./4-agent-tools/README.md)

### Goal

As a presenter, I can deploy a tiny pricing API, register it in Foundry Tools
through a toolbox, and show an agent discovering and invoking that hosted tool.

### Acceptance criteria

- [x] Provide standalone Bicep for Foundry, model, presenter RBAC, Function, and storage.
- [x] Deploy the actual JavaScript tool through Bicep without a separate code upload.
- [x] Register the OpenAPI tool in a Foundry toolbox and consume its MCP endpoint.
- [x] Show actual tool output and reject model-only answers or failed tool calls.
- [x] Support 1-999 attendees and test the actual tool's pricing/input boundaries.
- [x] Clean up the temporary toolbox version on success and inference failure.
- [x] Document the public read-only API, preview dependency, costs, setup, and cleanup.
- [x] Compile/lint Bicep and pass local tests.
- [x] Deploy and smoke-test Function -> Toolbox -> agent in the target subscription.

### Live verification

Deployed to `rg-foundry-agent-tools-demo` on 2026-10-26. Replaced Y1 with one
Windows P0v3 dedicated App Service instance and Always On in West US 2;
Foundry remains in East US 2. Actual deployment preflight rejected East US 2
alternatives with effective zero quota despite the quota API listing capacity.
The separate `functionLocation` parameter makes the successful layout reproducible.
The host uses managed identity and storage-scoped Blob Data Owner access,
without storage keys or a Consumption-only Azure Files connection string.

Direct Function calls verified correct prices for 1/12/20/999 attendees and
HTTP 400 for 0/1000/1.5/abc. Both complete Function -> Toolbox -> agent runs
returned the real source marker and correct totals: USD 300 for 12 attendees
and USD 500 for 20. Both temporary toolbox versions were deleted. Evidence is
retained in `4-agent-tools/.azure/deployment.json`,
`live-function-results.json`, and `live-agent-12.log` / `live-agent-20.log`
under the same ignored `.azure/` directory. The dedicated plan remains deployed
and bills continuously until deleted.

### Scope

One anonymous, read-only toy Azure Function; one toolbox; one local agent.
No payments, bookings, database, private data, hosted agent, or UI.

## F005 - Microsoft Foundry: Microsoft Decision Model

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-26

**Demo:** [Microsoft Decision Model](./5-decision-model/README.md)

### Goal

As a presenter, I can deploy Microsoft-Decision-1 and show typed classification
of toy support tickets without using a generative chat model.

### Acceptance criteria

- [x] Standalone Bicep deploys the Microsoft-format model, account, and user RBAC.
- [x] Local Python calls the dedicated Decision API using Entra authentication.
- [x] Display selected/expected teams, latency, and illustrative accuracy.
- [x] Reject HTTP errors and invalid or unsupported typed decisions.
- [x] Include dependencies, offline tests, deployment/run/cleanup instructions.
- [x] Compile/lint Bicep and pass local tests.
- [x] Deploy and smoke-test with Microsoft-Decision-1 access in the target region.

### Live verification

Deployed `Microsoft-Decision-1`, version `1`, using `DataZoneStandard` in East
US 2 to `rg-foundry-decision-model-demo` on 2026-10-26. The former
`GlobalStandard` SKU failed with `SpecialFeatureOrQuotaIdRequired`; the model
itself is available under the Data Zone SKU. Capacity defaults to 10 rather
than 1. Real inference still observed restrictive service-side request limits,
so the script explicitly reports HTTP 429 and follows the server's retry
headers for at most two retries per ticket, at most 65 seconds each.

All three real support-ticket classifications matched their expected teams.
Five offline tests cover typed results, invalid answers, HTTP failures,
retry-header handling, and exhausted/missing/excessive retry delays.
Deployment outputs and live logs are retained locally under ignored
`5-decision-model/.azure/`. Resources remain deployed.

### Scope

One account, one specialized model, three toy tickets; no real ticket routing,
free-form explanations, consequential decisions about people, or UI.

## F006 - Microsoft Foundry: Content Safety

**Type:** Feature

**Status:** Implemented; historical live pass recorded in tests.md, not rerun during the documentation review

**Demo:** [Content Safety](./6-content-safety/README.md)

### Goal

As a presenter, I can analyze text with Content Safety, explain the four harm
categories, and show how an application applies a severity threshold.

### Acceptance criteria

- [x] Standalone Bicep creates a keyless S0 Content Safety resource and user RBAC.
- [x] Local Python uses Entra ID and prints all four category severities.
- [x] Apply an inclusive ALLOW/BLOCK threshold on the 0/2/4/6 scale.
- [x] Missing categories, invalid scores, and service failures are not ALLOWs.
- [x] Tests cover both sides of the threshold without harmful example text.
- [x] Include dependencies, setup/run/cleanup documentation; compile/lint Bicep.
- [x] Deploy and smoke-test the service in the target subscription (historical baseline).

### Scope

One standalone Content Safety account and a local script. No model deployment,
custom classifier, image moderation, production policy, or UI.

## F007 - Microsoft Foundry: Semantic Caching

**Type:** Feature

**Status:** Current APIM/Managed Redis implementation validated offline; live gateway cache-hit verification pending (earlier local-cache pass does not apply)

**Demo:** [Semantic caching](./7-semantic-cache/README.md)

### Goal

As a presenter, I can show centralized semantic caching in the APIM AI gateway:
policies use Foundry embeddings to check and store responses in Azure Managed
Redis, without application-side caching logic.

### Acceptance criteria

- [x] Bicep creates APIM Consumption and Managed Redis Balanced B0 without HA.
- [x] Enable RediSearch, Enterprise clustering, NoEviction, and TLS Redis access.
- [x] Configure semantic lookup/store policies with an embeddings backend and TTL.
- [x] Authenticate the presenter before lookup and use APIM managed identity for Foundry.
- [x] Route every chat request through APIM; no application cache or embedding calls.
- [x] Display response IDs, observed reuse, and latency without invented cache metrics.
- [x] Test request routing, response validation, policy ordering, and cost defaults.
- [x] Document gateway tracing, distance threshold, isolation, costs, and cleanup.
- [x] Compile/lint Bicep and pass local tests; preserve shared-runner output mapping.
- [ ] Deploy the new gateway/Redis stack and confirm real semantic hits in APIM traces.

### Scope

Centralized Redis-backed gateway caching over public, single-turn workshop
questions. APIM Consumption minimizes gateway cost; Redis B0 without HA is for
development/test only. No production isolation, native Foundry cache toggle,
provider prompt caching, or latency guarantee.

## F008 - Microsoft Foundry: Agent Evaluation

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-26

**Demo:** [Agent evaluation](./8-agent-evaluation/README.md)

### Goal

As a presenter, I can evaluate real stored agent interactions with Foundry's
managed coherence evaluator and inspect actual run and per-item results.

### Acceptance criteria

- [x] Standalone Bicep deploys account/project/model and presenter/project RBAC.
- [x] Generate two interactions with a temporary Foundry tutor agent.
- [x] Submit exact generated question/answer pairs with response-ID metadata to
  Foundry cloud `builtin.coherence` evaluation using inline JSONL.
- [x] Poll with a deadline and reject failed, errored, or incomplete results.
- [x] Display run metadata/counts/per-item scores and clean up the agent version.
- [x] Include dependencies, offline tests, preview/cost/setup/cleanup documentation.
- [x] Compile/lint Bicep and pass local tests.
- [x] Deploy and confirm managed evaluation/judge permissions and completed results.

### Live verification

Deployed in East US 2 to `rg-foundry-agent-evaluation-demo` on 2026-10-26.
Reproduced HTTP 404 in response-retrieval cloud evaluation. A same-ID probe
proved the stored response exists through the agent-scoped client (HTTP 200),
but not the project-scoped client (HTTP 404). Replaced cross-scope retrieval
with documented inline JSONL over the exact questions/answers and response IDs.
Cloud coherence evaluation remains real, with strict source/grader/count checks.

Run `evalrun_a87a335f1e5943f79ea9dbaa1522db58` completed with 2 passed,
0 failed, 0 errored and coherence score 5/5 each. Independently verified
Azure run/items and agent-version deletion. Also serialized model/project
ARM writes after a reproduced `RequestConflict`. Evidence is retained locally
in ignored `8-agent-evaluation/.azure/live-testing-summary.json` and
`response-scope-probe.json` under the same directory. Resources remain deployed.
Coherence alone does not prove factual correctness.

### Scope

Two responses, one deployed judge, one cloud evaluation. No local surrogate
score, telemetry pipeline, comprehensive benchmark, or safety certification.

## F009 - Microsoft Foundry: Agent Red Teaming

**Type:** Feature

**Status:** Implemented; historical live pass after retry recorded in tests.md, not rerun during the documentation review

**Demo:** [Agent red teaming](./9-agent-red-teaming/README.md)

### Goal

As a presenter, I can run a small managed Foundry red-team scan against an
authorized tutor agent and explain its attack-success scorecard.

### Acceptance criteria

- [x] Standalone Bicep deploys account/project/model and user/project RBAC.
- [x] Use the official RedTeam SDK against a real model-backed agent callback.
- [x] Bound the demo to one category/objective with no strategy expansion.
- [x] Save report and SDK files in a new ignored directory; print the scorecard.
- [x] Reject missing results/scorecards and incomplete, skipped, or errored evaluations.
- [x] Document Python/preview/region requirements, sensitive reports, costs/cleanup.
- [x] Compile/lint Bicep and pass local tests without generating attack content.
- [x] Deploy and complete an authorized scan in a supported target region (historical baseline).

### Scope

One baseline objective in one category, single-turn text only. No embedded
attack corpus, jailbreak strategy expansion, production targets, or safety claim.

## F010 - Microsoft Foundry: Supervised Fine-Tuning

**Type:** Feature

**Status:** Implemented; real paid training completed on 2026-10-26; optional inference comparison not performed

**Demo:** [Fine-tuning](./10-fine-tuning/README.md)

### Goal

As a presenter, I can upload a tiny synthetic dataset and submit Foundry's
managed supervised fine-tuning without operating training infrastructure.

### Acceptance criteria

- [x] Standalone Bicep deploys a keyless account, base model, and presenter RBAC.
- [x] Include 12 balanced chat-format toy support-ticket training examples.
- [x] Validate the dataset, upload/process it, and submit one epoch of Global SFT.
- [x] Print job status/ID honestly; do not equate submission with completed training.
- [x] Inspect/resume existing jobs without resubmission; support bounded polling.
- [x] Reject failed/cancelled jobs and successful jobs missing a model ID.
- [x] Document optional trained-model deployment/inference, cancellation, costs/cleanup.
- [x] Compile/lint Bicep and pass offline tests.
- [x] Complete a paid job in the target subscription and retain its model ID.
- [ ] Optionally deploy that model and compare on held-out examples.

### Live verification

Reproduced HTTP 400 `invalidPayload` / `The fineTuningJob field is required.`
The actual request problem was `trainingType: "Global"` rather than the v1
API's `"GlobalStandard"`. Changing that enum alone fixed submission; a
`fineTuningJob` wrapper was not required.

Completed real paid job `ftjob-1f5660c4acff48438874163995f91d99` in East US 2
under `rg-foundry-fine-tuning-demo`, using the 12 synthetic examples and one
supervised epoch. Independent API readback confirmed `succeeded`, no error,
663 trained tokens, exactly one job, and nonempty downloaded training results.
Retained model ID:
`gpt-4.1-mini-2025-04-14.ft-1f5660c4acff48438874163995f91d99-workshop`.

Training took about 71 minutes including GPU queueing and finalization.
Resumed the same job after two local 30-minute polling timeouts without
resubmission. The script now saves current job/model JSON immediately and at
each poll to ignored `.azure/fine-tuning-job.json`.
Seven regression tests and Bicep build/lint passed. Evidence is retained in
`10-fine-tuning/.azure/live-testing-summary.json` and associated transcripts.
Resources and trained model remain retained; optional trained inference and
held-out comparison were not performed and do not block this feature's required
training acceptance.

### Scope

Managed SFT, one epoch, toy classification, no GPU cluster or RL. Training is
asynchronous; submit ahead of a presentation. Minimum-size data demonstrates
the API, not improved accuracy or production training quality.

## F011 - Microsoft Foundry: Foundry IQ

**Type:** Feature

**Status:** Historical managed retrieval live pass on 2026-10-26; current project-enabled infrastructure not live-verified

**Demo:** [Foundry IQ](./11-foundry-iq/README.md)

### Goal

As a presenter, I can reuse Microsoft Learn's agentic-retrieval pattern to show
a tiny managed knowledge base answering a compound workshop question with sources.

### Acceptance criteria

- [x] Standalone Bicep deploys keyless Foundry, a model, Basic Search, and RBAC.
- [x] Grant the Search managed identity permission to call the planning/answer model.
- [x] Include three synthetic documents; create a semantic text index, knowledge
  source, and knowledge base through the real managed APIs.
- [x] Request answer synthesis, references, and retrieval activity; print actual results.
- [x] Check document upload/visibility and reject missing grounding or activity errors.
- [x] Cite the Learn quickstart and document the simplified, vector-free scope.
- [x] Include dependencies, offline tests, deployment/run/cost/cleanup instructions.
- [x] Compile/lint Bicep, pass tests, and wire the Search endpoint into the runner.
- [x] Deploy in a supported region and show a grounded live answer.
- [ ] Deploy and live-test the current project-enabled template; the historical pass predates the project addition.

### Live verification

Resolved the regional Search capacity blocker with independent
`searchLocation=westus2`, keeping Foundry in East US 2. Both resources in
`rg-foundry-foundry-iq-demo` deployed successfully on 2026-10-26.
The template defaults to the verified split; the root runner's
`--search-location` override works without additional runner changes.

Created all three documents, the managed knowledge source, and the real
answer-synthesizing knowledge base. Both default and custom questions returned
the expected facts with citations to the required exact source documents.
Observed real planning, three/two Search subqueries, and synthesis respectively,
without activity errors. Independent API readback confirmed document count,
source/base configuration, and the cross-region model connection.
Six lab tests and 27 runner tests passed, including the compiled Search-region
contract; Bicep build/lint passed. Evidence is retained locally in ignored
`11-foundry-iq/.azure/live-testing-summary.json` and associated logs.
Resources remain deployed; Basic Search bills continuously while provisioned.

### Scope

The Azure AI Search knowledge layer underpinning Foundry IQ, not a hosted agent
or portal integration. One text index, one source, one base, three documents.
No vectors, SharePoint, enterprise permission synchronization, storage, or UI.
Basic Search has ongoing provisioning charges.

## F012 - Microsoft Agent Framework: Entra Agent ID

**Type:** Feature

**Status:** Implemented; authorized tenant provisioning and live inference pending

**Demo:** [Entra Agent ID](./12-entra-agent-id/README.md)

### Goal

As a presenter, I can explain blueprint versus child identity and show an Agent
Framework tutor authenticating to Foundry as a real Entra Agent ID, not a user.

### Acceptance criteria

- [x] Provide tenant setup using typed Graph blueprint/principal/agent endpoints,
  explicit consent scopes, OData headers, and an accountable human sponsor.
- [x] Keep the one-day development blueprint secret in the process environment,
  never in source, saved state, Bicep parameters, outputs, or normal console output.
- [x] Record non-secret created object IDs incrementally for cleanup after failures.
- [x] Standalone Bicep deploys Foundry/model and account-scoped inference RBAC
  for the actual child identity; no automatic user/key fallback.
- [x] Demonstrate the documented two-stage autonomous token exchange and use
  the child resource token in the Agent Framework client.
- [x] Document tenant prerequisites, production credential alternatives, and
  separate Azure and tenant cleanup.
- [x] Fail root runner preflight before Azure changes if tenant setup is missing.
- [x] Compile/lint Bicep; test token exchange, client wiring, and mocked Graph setup.
- [ ] Obtain authorized tenant/admin consent, provision identity, and verify live
  Foundry authorization and Entra sign-in evidence.

### Scope

One blueprint, one sponsored child agent identity, one one-day local-development
credential, one model-backed tutor. No Graph data access, agent user account,
user delegation, broad resource grants, or production credential framework.
Graph tenant objects are intentionally provisioned separately from ARM/Bicep.

## F013 - Microsoft Agent Framework: Four Orchestration Patterns

**Type:** Feature

**Status:** Implemented; historical live pass for all four patterns after startup retry, not rerun during the documentation review

**Demo:** [Agent orchestrations](./13-agent-orchestrations/README.md)

### Goal

As a presenter, I can run one tiny, separate example for Sequential, Concurrent,
Group Chat, and Magentic orchestration using Microsoft Agent Framework and Foundry.

### Acceptance criteria

- [x] Standalone keyless Bicep deploys one account/project/model and presenter RBAC.
- [x] Sequential sample passes an invitation from writer to editor.
- [x] Concurrent sample independently requests a title and snack suggestion.
- [x] Group Chat sample shares a planner/reviewer discussion with bounded turns.
- [x] Magentic sample uses a model-driven manager with two workshop-card specialists.
- [x] Keep each builder/scenario in its own simple file and offer individual/all
  execution through the demo entrypoint.
- [x] Print participant/final outputs; reject empty output and bound workflow runtime.
- [x] Execute real framework graphs in offline tests, proving handoff, overlapping
  concurrent calls, shared chat, and manager coordination of both specialists.
- [x] Include pinned framework dependencies, Learn links, setup/run/cost/cleanup
  documentation; compile/lint Bicep and pass local tests.
- [x] Deploy and run each of the four patterns against the target Foundry project (historical baseline).

### Scope

Local orchestration with Foundry inference, one deployment shared by all four
samples. No tools, web research, hosted workflows, database, or UI.
Manager decisions are model-driven and not guaranteed to follow a fixed order.

## F014 - Microsoft Foundry: Project and Deployment Fundamentals

**Type:** Feature

**Status:** Fixed and fully live-tested on 2026-10-26; real RBAC denial verified

**Demo:** [Foundry fundamentals](./14-foundry-fundamentals/README.md)

### Goal

As a student, I can explain account/project/model deployment relationships,
authenticate without keys, and obtain my first streamed model response.

### Acceptance criteria

- [x] Provide standalone Bicep, local Python, dependencies, offline tests, and instructions.
- [x] Distinguish project endpoints, inference endpoints, model IDs, and deployment names.
- [x] Stream a real response and display actual usage when supplied by the service.
- [x] Demonstrate explicit wrong-deployment and missing-permission errors safely.
- [x] Document quota, RBAC propagation, costs, deployment, and cleanup.
- [x] Deploy and verify real streaming, usage, and wrong-deployment HTTP 404.
- [x] Verify a real missing-inference-permission denial with an identity without inherited inference rights; preserve the actual HTTP status (live service returned 401 `PermissionDenied`, not 403).

### Live verification

The original partial run could not deny the presenter, who inherits subscription
Foundry Owner. On 2026-10-26, a fresh certificate-authenticated service principal
received only Cognitive Services OpenAI User on the primary account through Bicep.
The complete real run exited 0: normal inference (83 tokens), streaming (71
tokens), HTTP 404 `DeploymentNotFound`, and HTTP 401 `PermissionDenied` on the
second account. Independent versioned inference returned the explicit missing
chat-completions data action. Azure returned 401 for authorization failure, so
the corrected probe validates the permission error code/message instead of
assuming 403 or accepting arbitrary authentication failures.

Role readback confirmed exactly one primary-account assignment, no inherited
inference rights for the restricted identity, and unchanged presenter rights.
The denied account's model deployment was confirmed healthy. Nine offline tests
also passed. Evidence is retained under `14-foundry-fundamentals/.azure/` in
`live-restricted-identity.log`, `live-validation.json`,
`versioned-denial-probe.json`, and `restricted-roles.json`.
The local, ignored development certificate expires 2026-10-27 10:00:19 UTC;
reproduction and tenant-identity cleanup are documented in the lab README.

### Scope

One small model-backed tutor. No UI or API-key authentication. Error demonstrations
must not silently fall back to another deployment or identity.

## F015 - Microsoft Foundry: Repeatable Model Benchmark

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-25

**Demo:** [Model benchmark](./15-model-benchmark/README.md)

### Goal

As a student, I can compare two models and Model Router on the same labeled tasks
and justify a quality/latency/cost trade-off.

### Acceptance criteria

- [x] Provide standalone model deployment Bicep and a repeatable synthetic dataset.
- [x] Use consistent prompts/scoring and display per-item failures and serving model evidence.
- [x] Compare accuracy, schema compliance, repeated-call latency, token usage, and estimated cost.
- [x] State pricing assumptions and limitations of a tiny benchmark explicitly.
- [x] Include offline scoring tests, setup, prerequisites, and cleanup.
- [x] Run all three deployment paths and inspect actual benchmark results.

### Live verification

Ran 54 actual model calls in East US 2. Mini/full/router task accuracy was
83.3%/83.3%/50%, respectively. Genuine schema and truncation failures remain
visible and count against the benchmark; a successful experiment does not mean
every model answer passed. Request pacing was verified for the observed 10-RPM
limit. Resources remain deployed. Evidence is retained in
`14-foundry-fundamentals/.azure/live-validation-summary.json`.

### Scope

A small instructional benchmark, not a statistically representative production
model ranking or a guarantee of router choices.

## F016 - Microsoft Foundry: Prompt Design and Structured Outputs

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-25

**Demo:** [Structured outputs](./16-structured-outputs/README.md)

### Goal

As a student, I can compare prompt strategies and consume schema-validated model
output without confusing structural validity with factual correctness.

### Acceptance criteria

- [x] Compare free-form instructions, few-shot examples, and supported strict JSON Schema.
- [x] Extract category, priority, and summary from synthetic support tickets.
- [x] Validate structure separately from labeled field correctness.
- [x] Handle missing information, refusal, and incomplete output explicitly.
- [x] Include Bicep, Python, offline tests, costs, prerequisites, and cleanup.
- [x] Verify the supported model's live responses for each prompting strategy.

### Live verification

All 12 real prompt-strategy comparisons were correct in East US 2. Additional
live probes observed actual `length` truncation and HTTP 400 `content_filter`;
service-side filtering is labeled separately from an assistant-message refusal.
Probe false-success handling was corrected without claiming a refused/truncated
response was a valid answer. Evidence is retained in
`14-foundry-fundamentals/.azure/live-validation-summary.json`.

### Scope

One extraction task and one model deployment. No training job or production
accuracy claim; simulated failure tests are distinguished from live observations.

## F017 - Microsoft Foundry: RAG Fundamentals

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-25; full live rerun passed 2026-10-26

**Demo:** [RAG fundamentals](./17-rag-fundamentals/README.md)

### Goal

As a student, I can follow ingestion, chunking, embeddings, retrieval, and cited
generation before using a managed knowledge abstraction.

### Acceptance criteria

- [x] Deploy Foundry inference/embeddings and Azure AI Search with keyless access.
- [x] Index a synthetic corpus and compare keyword, vector, and hybrid retrieval.
- [x] Compare a chunking change on fixed questions and inspect retrieved evidence.
- [x] Generate cited answers and explicitly abstain when evidence is insufficient.
- [x] Include meaningful offline tests and setup/run/cost/cleanup guidance.
- [x] Verify indexing, retrieval comparisons, and grounded generation against Azure.

### Live verification

East US 2 Search provisioning returned `InsufficientResourcesAvailable`.
The documented `searchLocation=westus2` alternative succeeded, keeping Foundry
in East US 2 in `rg-foundry-rag-fundamentals-demo`. Tested 24 real retrieval/
generation cases across keyword/vector/hybrid search and two chunk sizes
(11 versus 3 chunks): 18 validated cited answers and six abstentions.
Recall@3 was 1 throughout; MRR was 0.833 for 12-word keyword chunks and 1 for
the remaining configurations. Six offline tests pass. Actual errors, commands,
and outcomes are retained in `17-rag-fundamentals/.azure/live-result.json`.

On 2026-10-26, reran the full real Azure pipeline against the same deployment.
All 24 cases passed: 18 independently parsed cited/quoted answers and six
abstentions with empty citations/quotes. Retrieval metrics matched the original
run. Independent index listing confirmed no new indexes remained after cleanup.
Six offline tests passed. Fresh evidence is retained in
`17-rag-fundamentals/.azure/live-2026-10-26.log` and
`17-rag-fundamentals/.azure/live-2026-10-26.json`.

### Scope

A tiny application-managed RAG pipeline. Search has ongoing provisioning charges;
this is not a replacement implementation of managed Foundry IQ.

## F018 - Microsoft Foundry: Agent Tracing

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-25

**Demo:** [Agent tracing](./18-agent-tracing/README.md)

### Goal

As a student, I can diagnose agent latency and failures using correlated model
and tool spans rather than inspecting only the final answer.

### Acceptance criteria

- [x] Provide Foundry and monitoring infrastructure plus supported OpenTelemetry wiring.
- [x] Capture correlated model/tool spans, status, durations, and available usage.
- [x] Introduce controlled slow and failed synthetic tool calls.
- [x] Print trace correlation evidence and explain inspection in monitoring tools.
- [x] Avoid sensitive prompt/token logging and include offline tests and cleanup.
- [x] Verify exported spans and diagnose the slow/failing operations in Azure.

### Live verification

Deployed in East US 2 to `rg-foundry-agent-tracing-demo`. Queried nine actually
ingested correlated spans, including a 2005-ms slow tool and a failed tool's
failure flag and safe error label. Fixed exporter-provider wiring and preserved
`workshop.error_type` because the exporter discarded reserved `error.type`.
Three offline tests pass. Full trace IDs and ingestion evidence are retained in
`18-agent-tracing/.azure/live-testing-summary.json`.

### Scope

A small locally executed instrumented agent, not a production observability stack.
Exporter submission alone is not proof of ingestion or dashboard visibility.

## F019 - Microsoft Foundry: Conversation State and Context

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-25

**Demo:** [Conversation state](./19-conversation-state/README.md)

### Goal

As a student, I can resume an isolated multi-turn conversation and explain how
explicit context management differs from model knowledge and durable memory.

### Acceptance criteria

- [x] Persist and resume model-backed conversation state using an explicit mechanism.
- [x] Isolate two sessions/users and demonstrate reset/deletion.
- [x] Bound growing history and make information loss visible.
- [x] Document the local trust boundary and limitations of the history budget.
- [x] Include Bicep, offline state tests, prerequisites, costs, and cleanup.
- [x] Verify multi-turn behavior, persistence, and context-management effects live.

### Live verification

Deployed in East US 2 to `rg-foundry-conversation-state-demo`. Eighteen real
model calls exercised the default scenario and separate-process CLI persistence,
session isolation, bounded-history truncation, reset, and deletion. Five offline
tests pass. Evidence is retained in
`19-conversation-state/.azure/live-result.json`; resources remain deployed.

### Scope

Small synthetic tutor sessions with application-managed state, not a production
multi-tenant memory service or a claim that user labels authenticate callers.

## F020 - Microsoft Foundry: Human-Approved Tool Actions

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-25

**Demo:** [Human approval](./20-human-approval/README.md)

### Goal

As a student, I can enforce exact-action approval outside the model before a
synthetic booking changes application state.

### Acceptance criteria

- [x] Use a real model function call to propose, never automatically execute, a booking.
- [x] Validate operation, attendee policy, authenticated actor, and exact-action digest.
- [x] Require explicit approval/rejection and expire pending proposals.
- [x] Bind execution to immutable reviewed arguments and prevent duplicate replay.
- [x] Test approval, rejection, wrong actor, mutation, expiry, and idempotency.
- [x] Include standalone Bicep, dependencies, deployment, limitations, and cleanup.
- [x] Generate and approve/reject proposals with an actual Entra-authenticated user.

### Live verification

Deployed in East US 2 to `rg-foundry-human-approval-demo`. Verified real model
proposals, explicit rejection and approval, duplicate replay, wrong-digest
rejection, and changed-payload rejection. Exactly one synthetic booking was
created across the approval-path test. Fixed an observed Foundry response
compatibility issue: a complete tool call can have finish reason `stop`.
Seven offline tests pass, including this regression. Evidence is retained in
`20-human-approval/.azure/live-approval-paths.log`; resources remain deployed.

### Scope

Local SQLite synthetic bookings only; no payments or real reservations. The
local OS/database are trusted, and a digest is not an administrator-proof signature.

## F021 - Microsoft Foundry: Permission-Aware Retrieval

**Type:** Feature

**Status:** Live testing blocked on 2026-10-26: test-user creation denied (Entra Global Reader; HTTP 403)

**Demo:** [Permission-aware retrieval](./21-permission-aware-retrieval/README.md)

### Goal

As a student, I can show that authenticated callers with different permissions
retrieve different evidence, without asking the model to enforce access.

### Acceptance criteria

- [x] Index synthetic public/restricted documents with explicit access metadata.
- [x] Derive trusted filtering from authenticated identity rather than prompt-supplied claims.
- [x] Support two genuinely authenticated test identities with documented setup.
- [x] Verify unauthorized content is excluded from passages, answers, and citations.
- [x] Test filter enforcement and document cache isolation requirements.
- [x] Include infrastructure, costs, tenant prerequisites, and cleanup.
- [ ] Provision the authorized identities and verify both access paths live.

### Live verification attempt

Ran the actual preflight with the selected subscription's tenant. It exited 2
because `SECOND_AZURE_CONFIG_DIR` is missing. A separate Azure CLI configuration
must be authenticated as a different real user in that tenant; no identities
were invented and no tenant consent or identity provisioning was performed.
Ten offline tests pass, but they do not prove live permission isolation.
Evidence is retained in
`21-permission-aware-retrieval/.azure/live-result.json`.

On 2026-10-26, independently authenticated the presenter through actual Graph
profile and transitive-membership requests; both succeeded. Ten offline tests
passed again. `SECOND_AZURE_CONFIG_DIR` is unset and the documented `.identity-b`
directory does not exist. First-user evidence is retained in
`21-permission-aware-retrieval/.azure/first-user-preflight.json`.

The remaining action is a different real same-tenant user's interactive CLI login
in an isolated directory, followed by two-user preflight and the complete live
exercise. That user needs no Azure subscription or Search/model role, and the
default ACL needs no custom group. An administrator is needed only if an account
must be provisioned/invited or tenant policy blocks its Graph access. A service
principal is not a substitute for this user-delegated Graph test. No new billable
lab resources were deployed. The README includes copy-paste setup commands.

After explicit authorization to provision a test user, actual Graph
`POST /v1.0/users` returned HTTP 403 `Authorization_RequestDenied`:
`Insufficient privileges to complete the operation.` Directory-role readback
confirmed **Global Reader** for the presenter. No user was created; generated
password/request files were removed. No tenant privileges, MFA/Conditional Access,
or consent settings were changed. Nonsecret evidence is retained in
`21-permission-aware-retrieval/.azure/user-provisioning-result.json`.
An authorized Entra administrator must create the user or authorize user creation;
then a real second-user login and full retrieval validation are still required.

### Scope

A trusted application-side access-control teaching example over Azure AI Search,
not automatic SharePoint permission synchronization or a synthetic identity proof.

## F022 - Microsoft Foundry: Prompt-Injection Defenses

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-25

**Demo:** [Prompt-injection defenses](./22-prompt-injection/README.md)

### Goal

As a student, I can distinguish untrusted source data from instructions and
compare raw model behavior with constrained, validated processing.

### Acceptance criteria

- [x] Use harmless instruction overrides in synthetic document and tool-result text.
- [x] Compare real baseline responses with factual projection and strict-schema output.
- [x] Integrate real Prompt Shields classification with explicit optional omission.
- [x] Validate the exact allowed fact and expose no executable side-effect tools.
- [x] Test malformed facts, extra output, injection markers, and classifier errors.
- [x] Explain residual risks, category filtering differences, costs, and cleanup.
- [x] Observe actual model/classifier behavior and verify protected output on Azure.

### Live verification

Deployed in East US 2 to `rg-foundry-prompt-injection-demo`. Prompt Shields
detected the document override but did not flag the synthetic tool-output
override. Azure inference content filtering blocked both raw baselines.
Both independently tested protected paths returned the validated `09:00` fact.
The application now explicitly reports a service-filtered baseline rather than
crashing or claiming an answer; other API errors still propagate. Seven offline
tests pass. Evidence is in `22-prompt-injection/.azure/live-test-after-fix.log`.
These observations are not a claim of perfect attack detection.

### Scope

Two tiny fixed-format sources, not a general sanitizer or complete injection
protection claim. Baseline attack success is not guaranteed or fabricated.

## F023 - Microsoft Foundry: Evaluation-Driven CI/CD

**Type:** Feature

**Status:** Implemented; cloud-tested on 2026-10-25 and GitHub OIDC verified on 2026-10-26

**Demo:** [Evaluation CI/CD](./23-evaluation-cicd/README.md)

### Goal

As a student, I can block a prompt regression using repeatable correctness checks
and Foundry evaluation rather than accepting better-looking prose.

### Acceptance criteria

- [x] Version a synthetic dataset and prompt variants with reproducible metadata.
- [x] Combine deterministic task checks with actual supported Foundry evaluation.
- [x] Apply explicit release thresholds and report per-item failures.
- [x] Provide an installable GitHub Actions workflow using federated Azure authentication.
- [x] Fail visibly on evaluation infrastructure errors and regression thresholds.
- [x] Include infrastructure, offline tests, OIDC permissions, costs, and cleanup.
- [x] Verify a passing baseline and failing regression against real cloud evaluation.
- [x] Configure repository federation and observe a passing and deliberately failing CI run.

### Live verification

Deployed in East US 2 to `rg-foundry-evaluation-cicd-demo`. The baseline exited
0 at 100% correctness and 100% coherence; the deliberate regression exited 1
at 0% correctness despite 100% coherence. Thresholds were not lowered.
Response-ID evaluation returned HTTP 404, so the documented inline-JSONL route
now evaluates the exact generated responses using real `builtin.coherence`.
Result status/count/source validation remains strict. Eight offline tests pass,
including installed/example workflow consistency.
Run IDs and evidence are retained in
`23-evaluation-cicd/.azure/live-testing-summary.json`.

The missing GitHub configuration was completed on 2026-10-26. The installed
workflow uses a dedicated managed identity with only lab-account Foundry User
access and exact immutable repository/environment OIDC trust. The protected
environment requires owner approval, disables administrator bypass, restricts
branch refs, and holds five non-secret variables; no client secret was created.

[Baseline Actions run](https://github.com/mifurm/FoundryWorkshopDEMOS/actions/runs/40281692169)
passed with 100% correctness/coherence.
[Regression Actions run](https://github.com/mifurm/FoundryWorkshopDEMOS/actions/runs/40281830195)
failed at the intended correctness gate (0% correctness, 100% coherence).
Both runs passed real OIDC login and eight unit tests and uploaded their cloud
report. Downloaded artifacts verified completed cloud evaluation and three
actual responses per run. An initial `AADSTS700213` mismatch was fixed by using
GitHub's exact immutable-ID subject rather than disabling that protection.

Repository-wide branch protection was not changed: the path-filtered workflow
must be made always-reporting before requiring its check for unrelated PRs.

### Scope

One small regression gate, not a production-quality evaluation dataset.
The workflow is installed and configured in this repository; other repositories
must configure their own environment, identity trust, and variables.

## F024 - Microsoft Foundry: Agent Application Lifecycle

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-25

**Demo:** [Agent lifecycle](./24-agent-lifecycle/README.md)

### Goal

As a student, I can publish an agent application, invoke its authenticated
endpoint, update its serving version, and restore known-good behavior.

### Acceptance criteria

- [x] Use a documented Foundry agent application publishing/deployment route.
- [x] Create distinguishable versions and invoke the actual published endpoint.
- [x] Inspect version/correlation evidence and restore the previous version.
- [x] Verify restored behavior with the same client requests.
- [x] Include infrastructure, API-contract tests, permissions, costs, and cleanup.
- [x] Publish, update, monitor, and roll back against the target Azure region.

### Live verification

Deployed in East US 2 to `rg-foundry-agent-lifecycle-demo`. Published the actual
agent application and verified six endpoint responses across version 1 -> 2 -> 1.
Three successful Activity Log writes and final ARM readback confirm rollback
to version 1. Application `workshop-app-82af60e1` and deployment `active` remain
deployed. Five offline tests pass. Evidence and IDs are retained in
`24-agent-lifecycle/.azure/live-testing-summary.json`.

Live ARM `RequestConflict` failures exposed concurrent model/project writes;
the templates in labs 18/23/24 now serialize these writes.

### Scope

A tiny published prompt agent, not a local-only script presented as a hosted
application. Uses the documented legacy Agent Application ARM publishing route;
requires Contributor and subscription Activity Log read access. Preview/API and
regional prerequisites must be checked before use.

## F025 - Microsoft Foundry: Resilience, Quotas, and Cost Controls

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-25

**Demo:** [Resilience and cost](./25-resilience-cost/README.md)

### Goal

As a student, I can bound retries, runtime, concurrency, and request costs while
handling transient failures without duplicating write operations.

### Acceptance criteria

- [x] Inject throttling/timeouts locally without exhausting service quota.
- [x] Disable hidden SDK retries and enforce attempt/deadline/request/tool budgets.
- [x] Respect Retry-After and propagate permanent failures explicitly.
- [x] Demonstrate bounded concurrency and idempotent recovery after a committed write.
- [x] Report real successful token usage and clearly labeled illustrative costs.
- [x] Test limits, outages, retry headers, idempotency, and deadline handling.
- [x] Include Bicep and explain quota/capacity and budget-alert limitations.
- [x] Run the two real model calls and inspect the bounded recovery demonstration.

### Live verification

Deployed in East US 2 to `rg-foundry-resilience-cost-demo`. Both actual inference
requests succeeded after explicitly local injected faults, reporting 30 input
and 37 output tokens in total. Model attempts were bounded at 5/6 and tool
attempts at 4/4; the retried committed write created one synthetic booking.
Permanent-error, outage-budget, and deadline stops were all observed explicitly.
Eight offline tests pass. Evidence is in `25-resilience-cost/.azure/demo.log`;
resources remain deployed. The printed USD estimate uses illustrative rates,
not a current price quotation or billing record.

### Scope

Two inference calls and local synthetic tools; no real payments/reservations or
deliberate quota exhaustion. Local timeout does not prove server-side cancellation.

## F026 - Microsoft Foundry: Multimodal Understanding

**Type:** Feature

**Status:** Implemented and live-tested on 2026-10-25

**Demo:** [Multimodal understanding](./26-multimodal/README.md)

### Goal

As a student, I can extract structured data from synthetic images, compare it
with ground truth, and represent unreadable information without guessing.

### Acceptance criteria

- [x] Provide clear and ambiguous synthetic image examples without personal data.
- [x] Invoke a supported image-capable Foundry model with structured extraction.
- [x] Validate schema separately from ground-truth field correctness.
- [x] Explicitly mark missing/unreadable values and report observed errors.
- [x] Include image prerequisites, infrastructure, offline tests, usage/cost caveats, and cleanup.
- [x] Verify extraction and uncertainty behavior with the actual model deployment.

### Live verification

Both real image-inference cases in East US 2 matched every ground-truth field,
including the ambiguous image's explicit unreadable/missing values. Resources
remain deployed. Evidence for this run is retained in
`14-foundry-fundamentals/.azure/live-validation-summary.json`; these two images
do not establish general visual accuracy or uncertainty calibration.

### Scope

A few instructional images, not OCR certification, financial automation, or a
guarantee of model-calibrated uncertainty.

## F027 - Microsoft Foundry: Foundry IQ with a Storage-backed Knowledge Base

**Type:** Feature

**Status:** Implemented and API-live-tested on 2026-10-26; authenticated portal visual check requires sign-in

**Demo:** [Foundry IQ with KB](./27-FoundryIQwithKB/README.md)

### Goal

As a presenter, I can open an agent in the new Foundry portal and ask grounded
questions over synthetic computer support cases and Surface Laptop runbooks
stored in Azure Blob Storage, using the actual Foundry IQ knowledge layer.

### Acceptance criteria

- [x] Provide standalone Bicep for Storage, Search, Foundry/project, chat/embedding models, and scoped RBAC.
- [x] Upload and read back eight labeled synthetic documents in two private containers.
- [x] Use managed Blob knowledge sources, automated ingestion/embeddings, and hourly refresh.
- [x] Connect two sources to one extractive Foundry IQ knowledge base with LLM planning.
- [x] Persist an agent with a project-identity knowledge-base MCP connection and Search portal connection.
- [x] Document New Foundry Knowledge/Agents playground steps, prompts, refresh, and cleanup.
- [x] Separate demonstrated capabilities from ACL/Purview/end-user identity/Copilot Studio scope.
- [x] Pass focused offline tests, root runner tests, and Bicep build/lint.
- [x] Verify real Azure ingestion, retrieval, persistent agent, and grounded/unknown answers.

### Live verification

Deployed `rg-foundry-FoundryIQwithKB-demo`: Foundry/Storage in East US 2,
Basic Search in West US 2. All eight indexed snippets matched the source files
exactly. The compound prompt generated six retrieval subqueries; the persistent
agent called the real KB MCP tool and cited both case and runbook evidence.
Arm64 and unknown warranty/future-spec prompts passed. Incremental refresh
processed one changed runbook and zero unchanged cases, changed the agent's
answer from two business days to one, and was restored/verified back to two.
Both keyless project connections and the saved agent were independently read back.
Six live scenarios, 16 lab tests, 32 root runner tests, and Bicep build/lint passed.

The browser required interactive sign-in, so authenticated portal UI rendering
was not verified. The new-portal walkthrough and actual project/agent connections
are provided. Resources remain deployed and billable; ignored evidence is in
`27-FoundryIQwithKB/.azure/live-verification.json` and adjacent live reports.

### Scope

A synthetic text-only support corpus with shared project access. No real support
records, official Surface repair advice, production ACL configuration, Copilot
Studio deployment, or guarantee about subquery counts or model factuality.

## F028 - Microsoft Foundry: Agent Using a Reusable Skill

**Type:** Feature

**Status:** Implemented; live Azure deployment and Skills API smoke test pending

**Demo:** [Agent skills](./28-agent-skills/README.md)

### Goal

As a presenter, I can store a reusable workshop procedure centrally in Foundry,
pin its version, and show the effect of its instructions on a Foundry agent.

### Acceptance criteria

- [x] Provide standalone Bicep for Foundry, project, model, and presenter RBAC.
- [x] Include a reusable skill with discovery metadata and an exact output contract.
- [x] Upload a uniquely named skill and download its immutable version.
- [x] Verify downloaded content before explicitly injecting it into an agent.
- [x] Compare independent requests with and without the skill using the same input/model.
- [x] Validate the skill-enabled answer and expose incomplete/service failures.
- [x] Clean up temporary agent versions and the skill on success or failure.
- [x] Document preview support, direct-injection scope, costs, deployment, and cleanup.
- [x] Pass 13 focused offline tests, 32 root runner tests, syntax checks, and Bicep build/lint.
- [ ] Deploy and verify real skill upload/download, both agent responses, and deletion.

### Scope

Application-managed direct injection of a version-pinned `SKILL.md` into a
temporary Foundry prompt agent. No autonomous skill discovery, Toolbox/MCP,
script execution, or hosted-container deployment. Offline tests and Bicep
compilation do not establish live preview access.

## F029 - Microsoft Foundry: Toolbox Skill Discovery

**Type:** Feature
**Status:** Implemented; offline validation only, live preview smoke test pending
**Demo:** [Toolbox skill discovery](./29-toolbox-skill-discovery/README.md)

### Goal

Discover three version-pinned skills over the native Toolbox MCP endpoint, let
the model choose a relevant skill, and load only that instruction body.

### Acceptance criteria

- [x] Standalone Bicep, Entra authentication, dependencies, tests and instructions.
- [x] Upload three skills and attach immutable references to a temporary Toolbox.
- [x] Discover metadata without loading bodies; require relevant skill and on-demand reference reads.
- [x] Validate all three independent output contracts and bounded error paths.
- [x] Delete owned Toolbox/skills after success, partial creation, or failure.
- [ ] Verify real MCP discovery and model behavior against Azure.

### Scope

Local Python read-only provider with Foundry inference and native MCP resources.
Instructions and a bundled checklist reference; no hosted containers or scripts.

## F030 - Microsoft Foundry: Skill-powered Data Analyst

**Type:** Feature
**Status:** Implemented; offline validation only, live smoke test pending
**Demo:** [Skill-powered analyst](./30-skill-data-analyst/README.md)

### Goal

Use a version-pinned procedure and native Code Interpreter to analyze synthetic
workshop data and produce verifiable downloadable artifacts.

### Acceptance criteria

- [x] Standalone infrastructure, authenticated demo, tests and instructions.
- [x] Require actual managed execution and generated-file evidence.
- [x] Download report/chart and verify fixed report ground truth and artifact shape.
- [x] Surface failures and clean up temporary service objects.
- [ ] Verify managed execution and artifact downloads against Azure.

### Scope

Skill instructions guide native sandbox execution; no local analysis fallback,
production data, or general statistical quality claims.

## F031 - Microsoft Foundry: Native Agent Memory

**Type:** Feature
**Status:** Implemented; offline validation only, live preview smoke test pending
**Demo:** [Native agent memory](./31-native-agent-memory/README.md)

### Goal

Demonstrate real managed memory retrieval across independent conversations,
scope isolation and forgetting without replaying local history.

### Acceptance criteria

- [x] Standalone infrastructure, authenticated demo, tests and instructions.
- [x] Use real Foundry memory operations, not local persistent-state substitutes.
- [x] Check fresh-conversation retrieval, distinct scopes, deletion and failures.
- [x] Document scope ownership, identity boundaries and preview prerequisites.
- [ ] Verify extraction, recall, isolation and deletion against Azure.

### Scope

Synthetic preferences only. Scope partitioning is distinct from authentication;
the lab README defines the supported identity and authorization boundary.

## F032 - Microsoft Foundry: Browser Runbook with Approval

**Type:** Feature
**Status:** Implemented; offline validation only, live browser smoke test pending
**Demo:** [Browser runbook](./32-browser-runbook/README.md)

### Goal

Use a reusable procedure and managed remote browser to prepare a synthetic
registration, with explicit approval before any submission.

### Acceptance criteria

- [x] Standalone infrastructure, authenticated demo, tests and instructions.
- [x] Use remote Playwright Workspaces MCP, not a local browser substitute.
- [x] Require browser evidence and exact-action approval before submission.
- [x] Keep default execution non-approving; document controlled-site setup.
- [ ] Verify remote browser state and approved submission against Azure.

### Scope

Controlled synthetic website and explicit browser prerequisites. No real
registration, personal data, or automatic consequential-action approval.

## F033 - Microsoft Foundry: Skill Release Governance

**Type:** Feature
**Status:** Implemented; offline validation only, live preview smoke test pending
**Demo:** [Skill release governance](./33-skill-release-governance/README.md)

### Goal

Evaluate a skill candidate before default promotion, demonstrate pinned consumer
isolation, and verify rollback without changing consumer code.

### Acceptance criteria

- [x] Standalone Bicep, Entra authentication, dependencies, tests and instructions.
- [x] Create immutable versions and gate real answers on fixed factual expectations.
- [x] Read back promotion and restore the original default in a finally block.
- [x] Compare fresh default-resolving and pinned consumers before/after rollback.
- [x] Reject an incorrect candidate without promotion and clean up after failures.
- [ ] Verify promotion, pinning and rollback against Azure.

### Scope

Local deterministic gate and explicit snapshot injection into temporary Foundry
prompt agents. No hot reload, hosted app deployment, or cloud evaluation claim.

## F034 - Microsoft Foundry: Document Intelligence Agent

**Type:** Feature
**Status:** Implemented and live-tested on 2026-10-27
**Demo:** [Document Intelligence agent](./34-document-intelligence/README.md)

### Goal

Present a persistent Foundry project agent that calls Document Intelligence in
Azure, showing extracted invoice fields, source evidence and missing-value review.

### Acceptance criteria

- [x] Standalone Bicep, dependencies, tests, catalog entry and lab instructions.
- [x] Cloud OpenAPI tool backed by a key-protected Function and managed-identity
  access to Document Intelligence, with no local function-call runner.
- [x] Synthetic complete and missing-total PDFs; bounded polling and visible errors.
- [x] Persistent agent, project connection and read-back verification.
- [x] Exact field, confidence, grounding and missing-value quality gates.
- [x] Deploy and pass repeated independent live agent tests against Azure.
- [x] Keep the original PDFs plus ten additional samples in the repository;
  deploy their exact bytes and verify file hashes in every live extraction.

### Live verification

The expanded [twelve-PDF corpus](./34-document-intelligence/samples/) passed
`python3 deploy.py --demo 34` on 2026-10-27: 12 direct analyses, 24 independent
agent requests and 96 exact field comparisons. All 36 service operation IDs
were distinct and every submitted PDF hash matched the repository file.
Three missing-total documents, explicit zero, EUR and varied vendors/amounts
passed without lowering the 0.8 confidence gate. Saved agent version 2 exposes
all twelve IDs. Throttling is handled with bounded retries; final execution
exited 0. Thirteen Python tests, including seven Node subtests, passed.

Initial two-document baseline:

The shared `python3 deploy.py --demo 34` workflow passed end to end in
`rg-foundry-document-intelligence-demo`: two direct extractions and four
agent requests. A separate six-request ask-only run also passed. All ten
agent requests used completed cloud OpenAPI calls with matching real Document
Intelligence operation IDs; all 40 ground-truth field comparisons passed.
Missing totals/currencies remained null and required review. The persisted
`document-intelligence-agent` version 1 was independently listed afterward.
Resources remain deployed and billable for the Foundry portal demonstration.

### Scope

Saved prompt agent and approved synthetic invoices, not custom model training,
arbitrary document uploads, a production review workflow or Agent Application
publication. Keep resources for the portal demonstration and document cleanup.

## F035 - Microsoft Foundry: Work IQ in the calling user's context

**Type:** Feature
**Status:** Implemented; live Work IQ authorization blocked
**Demo:** [Per-user Work IQ agent](./35-work-iq/README.md)

### Goal

Present a persistent native Foundry Work IQ agent without substituting the
developer, project identity, or a shared service account for the calling user.

### Acceptance criteria

- [x] Standalone Bicep, pinned dependencies, tests, catalog and instructions.
- [x] Native Work IQ A2A tool and OAuth2 connection setup with no logged secrets.
- [x] Individual consent continuation bound to authenticated caller and version.
- [x] Independent Graph caller ground truth, absent from the model's prompt.
- [x] Repeated identity checks and two-user report comparison with no shared history.
- [x] Deploy Azure resources and persist/read back native agent version 1.
- [ ] Complete administrator setup and per-user authorization.
- [ ] Verify actual Work IQ retrieval and two-user behavior live.

### Scope

Identity-only smoke test with approved real Microsoft 365 users. No mailbox/file
enumeration, writes, synthetic substitutes for Work IQ, or production ACL-isolation
claim. OAuth is a separate per-user sign-in, not silent incoming-token OBO.

### Verification

On 2026-10-27, Bicep deployment and native agent version 1 read-back succeeded in
`rg-foundry-work-iq-demo`. All 13 lab tests passed. The real saved-agent request
returned HTTP 400 `tool_user_error` because the OAuth connection has no valid
target; the connection is absent and the documented Work IQ service principal
was not found in the tenant. User/admin authorization was unavailable. No Work IQ
retrieval or two-user live pass is claimed. See [the test log](./tests.md).

## F036 - Microsoft Foundry: Custom-Code Hosted Agent

**Type:** Feature
**Status:** Implemented, deployed and live-tested
**Demo:** [Hosted agent and UI](./36-hosted-agent/README.md)

### Acceptance criteria

- [x] Deploy a real custom-code hosted agent and its container registry.
- [x] Use cloud model tool calls and validate the resulting structured quote.
- [x] Demonstrate native session persistence and independent session isolation.
- [x] Provide a simple UI, infrastructure, tests and documented cleanup.
- [x] Pass live pricing/state checks and correlate all invocations to Azure traces.

## F037 - Microsoft Foundry: Production Quality Monitoring

**Type:** Feature
**Status:** Deployed; blocked by absent native automatic evaluation runs
**Demo:** [Production monitoring and UI](./37-production-monitoring/README.md)

### Acceptance criteria

- [x] Deploy two persistent agents, managed endpoints and Azure monitoring.
- [x] Configure native response-completion evaluation rules and a cloud grader.
- [x] Provide a simple UI with truthful pending/pass/fail states.
- [ ] Observe automatic evaluations of actual baseline, regression and recovery traffic.
- [ ] Pass the complete cloud evaluation and response-correlated telemetry gate.

Manual evaluation diagnostics are not evidence of automatic monitoring success.
All 14 local tests pass. Real UI baseline/regression/recovery execution and exact
response/agent/version trace correlation pass for all six requests. The strict
automatic-evaluation gate timed out after 20 minutes; both good routes were
restored. See [verification results](./tests.md).

## F038 - Microsoft Foundry: Native Runtime Guardrails

**Type:** Feature
**Status:** Implemented, deployed and live-tested on 2026-11-03
**Demo:** [Native guardrails](./38-native-guardrails/README.md)

### Acceptance criteria

- [x] Standalone infrastructure, native guardrail attachment and explicit intervention evidence.
- [x] Offline tests, Azure deployment and strict live verification with separate status.

Fourteen lab tests pass. A real prompt agent returns an allowed response and
the native user-input policy blocks the synthetic marker with
`400/ContentFiltered`. The model-attached baseline also passes. Tool
intervention points are not configured. The shared runner passed in
`rg-foundry-native-guardrails-demo`, East US 2.

## F039 - Microsoft Foundry: AI Gateway Token Limits

**Type:** Feature
**Status:** Implemented, deployed and live-tested on 2026-11-03
**Demo:** [Gateway token limits](./39-gateway-token-limits/README.md)

### Acceptance criteria

- [x] Real gateway-enforced token rate limits and quotas, not local fault injection.
- [x] Offline tests, Azure deployment and strict live verification with separate status.

Nineteen lab tests pass. Actual APIM responses show rate-limit `429`, quota
`403` and independent Project B `200`. Three same-hour executions, including
the shared runner, pass with fresh central demonstration counters and unchanged
limits. Resources remain in `rg-foundry-gateway-token-limits-demo`, East US 2.

## F040 - Microsoft Foundry: Managed Voice Agent

**Type:** Feature
**Status:** Implemented, deployed and live-tested on 2026-11-03
**Demo:** [Voice agent](./40-voice-agent/README.md)

### Acceptance criteria

- [x] Native voice-based prompt agent and actual audio-session/conversation evidence.
- [x] Offline tests, Azure deployment and strict live verification with separate status.

Sixteen tests and the complete shared-runner replay passed in
`rg-foundry-voice-agent-demo`, East US 2. Real WebSocket input/output, audio,
transcripts and persisted conversation read-back satisfy the native live gate.

## F041 - Microsoft Foundry: Content Understanding

**Type:** Feature
**Status:** Implemented, deployed and live-tested on 2026-11-03
**Demo:** [Content Understanding](./41-content-understanding/README.md)

### Acceptance criteria

- [x] Actual schema-based extraction with ground truth, confidence and source evidence.
- [x] Offline tests, Azure deployment and strict live verification with separate status.

Thirty-two tests and the complete shared-runner replay passed in
`rg-foundry-content-understanding-demo`, East US 2. Document field confidence
and grounding are checked separately from generated audio facts and transcript
timestamps. See [verification results](./tests.md).

## F042 - Microsoft Foundry: Managed Harness and Tool Search

**Type:** Feature
**Status:** Implemented, deployed in Sweden Central and native live-tested through the shared runner (12 tests passed)
**Demo:** [Managed harness and native Tool Search](./42-managed-harness-tool-search/README.md)

### Acceptance criteria

- [x] Native Copilot harness selection and Toolbox Tool Search, not local skill injection.
- [x] Offline tests, Azure deployment and strict live verification with separate status.

## F043 - Microsoft Foundry: Agent Routines

**Type:** Feature
**Status:** Implemented and deployed; 24 tests passed; latest strict shared-runner live gate BLOCKED by native agentic-token issuance
**Demo:** [Native routines](./43-agent-routines/README.md)

### Acceptance criteria

- [ ] Native timer dispatch, run-history evidence and temporary-trigger cleanup.
- [x] Offline tests, Azure deployment and strict live verification with separate status.

An earlier direct native timer passed, but repeated root replays failed HTTP
400 token issuance despite persisted creator identity, ARM readiness and
enabled identity/blueprint principals. Tenant/service cause remains unknown.
Cleanup passed. Current repeatable native live acceptance remains open.

## F044 - Microsoft Foundry: Evaluation and Optimization

**Type:** Feature
**Status:** Implemented, deployed and native live-tested through the shared runner (27 tests passed)
**Demo:** [Evaluation and optimizer](./44-evaluation-optimizer/README.md)

### Acceptance criteria

- [x] Cloud evaluator breadth and actual native optimizer candidate results.
- [x] Offline tests, Azure deployment and strict live verification with separate status.

Final generated-dataset tags were verified with native PUT/GET. A completed
service-generated candidate improved independent heldout score from 0.4 to
1.0: APPROVE without automatic promotion. An earlier tied candidate was
correctly rejected.

## F045 - Microsoft Foundry: Governed IQ Integrations

**Type:** Feature
**Status:** Implemented and deployed; 18 tests passed, source-native permission checks passed; full native live gate BLOCKED by second-user and Fabric tenant/capacity prerequisites
**Demo:** [Permission-native IQ and Fabric IQ](./45-governed-iq/README.md)

### Acceptance criteria

- [ ] Source-native permission enforcement and actual Fabric IQ connection.
- [x] Offline tests, Azure deployment and honest tenant/identity prerequisite verification.

Permission synchronization, authorized retrieval and omitted-identity denial
passed. Two-real-user verification and actual Fabric execution remain blocked:
no second-user login, disabled Fabric/Power BI token issuance (`AADSTS500014`)
and zero Fabric capacity. Local substitutes do not satisfy this criterion.

## F046 - Microsoft Foundry: Fleet Governance

**Type:** Feature
**Status:** Implemented, deployed and API live-tested through the shared runner (16 tests passed); portal Control Plane NOT_VERIFIED, optional Defender/Purview not configured
**Demo:** [Fleet governance](./46-fleet-governance/README.md)

### Acceptance criteria

- [x] Cross-project assets, real telemetry and supported governance/registration evidence.
- [x] Offline tests, Azure deployment and clearly separated native/portal live verification.
