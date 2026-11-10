# Verification results

This report contains **dated observations**, not a guarantee of current tenant
access, model availability or resource health. Later feature-specific entries
supersede earlier results for the same implementation. Offline tests, ARM
deployment, actual native execution and portal verification are separate gates.
The [catalog](./demos.md) describes all 47 labs (numbered 0-46); the
[backlog](./BACKLOG.md) tracks acceptance criteria.

The documentation review on 2026-11-03 reconciled existing evidence; it did not
redeploy resources or repeat paid live tests. The later recovery checks below
include new live executions and supersede older results for those runs.

## Shared runner .NET migration, 2026-11-10

- **Offline passed:** .NET SDK 10.0.403 compiled `deploy.cs` and
  `test_deploy.cs`; all 16 shared regression checks passed, and the runner's
  `--help` smoke check passed. The checks use mocked Azure CLI responses and
  temporary lab folders; they do not require Azure credentials.
- **Infrastructure unchanged:** no Bicep, deployment, cleanup, resource, or
  hosted-runtime infrastructure files were changed.
- **Live not run:** no Azure login, resource deployment/deletion, paid model
  request, or hosted workload was attempted.
- **Integration blocked:** the seven lab conversion issues (#2, #3, #4, #5,
  #8, #9, and #11) have not yet been integrated into this checkout. Until their
  C# apps land, the runner's all-lab file-app validation and final full offline
  regression pass cannot be completed. The evaluation workflow now uses the
  lab 23 file-app commands and cannot run until issue #5 supplies those files.
- **Packaging blocker:** lab 36's unchanged `runtime/Dockerfile` is based on
  `python:3.12-slim` and starts `agent.py`. Running its converted hosted agent
  requires maintainer authorization for a packaging exception to the
  unchanged-infrastructure constraint. The migration is not complete while
  that Python runtime remains.
- **Review and merge pending:** an independent review of the final integrated
  commit and human readiness/approval/merge actions remain required.

### Independent review handoff

An independent read-only code-review agent reviewed commit
`8b4f8ef3ce511f585c0f62f9ac733a9a653cb6e1`. Its first-pass stale README
invocations were changed to the .NET runner, then the review was repeated on
that commit. The re-review found no other actionable issues and confirmed the
documented lab-integration blocker. This is not a review of the eventual
post-integration commit; request another independent review after rebasing all
seven lab conversions.

## Recovery verification, 2026-11-09

- **Lab 0:** both comparison models returned the reference answer of 11;
  all three native batch requests completed. The model's top-p explanation
  overstates coherence: restricting token selection does not guarantee it.
- **Lab 7:** all three live gateway answers were `4`. In a separate traced
  session, the first request reached the backend and the paraphrase reused
  the response without backend forwarding.
- **Lab 8:** cloud coherence evaluation passed 2/2 and the answers were
  factually reviewed. **Lab 17:** all 18 answerable retrieval queries were
  supported and all six unknown queries abstained.
- **Lab 30:** the native sandbox produced the correct six-row report and a
  visually matching chart after correcting its initial file path and integer
  serialization. **Lab 33:** the live promotion, pinning and rollback passed;
  the intentionally incorrect USD 40 candidate was rejected.
- Additional recovery runs passed for labs 2, 9, 14, 18, 23, 24, 38, 40
  and 41. Lab 14's 401 was an expected authorization denial; lab 24's
  immediate Activity Log query returned no records. Lab 38 verified model
  and agent guardrails, not tool boundaries.

These are dated observations, not guarantees of future availability. Each
deployed recovery run used a separate owned resource group and verified its
deletion. Labs 10, 12, 20, 21, 31, 32, 35, 37, 43 and 45 did not have a
complete verified demo in this recording run.

## Lab 0: introductory model basics, 2026-11-07

Added [model basics](./0-model-basics/README.md) using the standard standalone
lab structure. **Offline verification passed; no Azure resources were deployed
and no paid inference or native batch completion was tested.**

- **17/17 new-lab unit tests passed**, covering synthetic temperature/nucleus
  math, one-knob sampling, same-prompt reasoning parameter isolation, keyless
  environment wiring, batch JSONL/correlation, persisted resumption, bounded
  polling and explicit pending/error/incomplete-result failures.
- **38/38 shared runner/catalog tests passed** after updating the expected
  lab sequence to 0-46 and adding tested reasoning/batch output mappings.
  The repository-wide tag-position test was excluded from that passing run:
  it fails on the existing lab 13 deployment declaration, which places a
  comment/`dependsOn` before `tags`. The test assumes `tags` is the first line.
  The same declaration is present in Git HEAD; lab 13 was not changed.
  The tag-position test passes when scoped to the new lab's template.
- Bicep compiled successfully. Compiled ARM checks confirmed the two online
  models, same-version GPT-4.1-mini GlobalBatch deployment, disabled local
  authentication and the four expected outputs.
- Pylance reported no syntax errors in the new demo/tests and no diagnostics
  for the new demo. The no-cloud concepts entrypoint ran successfully.
- Relative links in the five directly related README/root documents resolve,
  code fences balance, and `git diff --check` passed.

As of this 2026-11-07 check, the native batch implementation required all
three downloaded responses to validate; submission or a pending job is not a
live pass. Azure availability, account/RBAC creation, real sampling/reasoning
responses and completed batch results had not yet been verified.

## Repository documentation review, 2026-11-03

Reviewed all **46 lab READMEs** against their entrypoints, CLI options,
infrastructure/dependency contracts and existing verification evidence.
Updated **39 documentation files**: 32 lab READMEs, four root documents and
three presenter references. Accurately documented labs were left unchanged;
no runtime, infrastructure, dependency or test implementation was changed.

The review corrected missing lab-34 navigation, proposal-like catalog titles,
platform/region restrictions, RBAC role and principal-ID guidance, standalone
output wiring, expired credential warnings, cleanup targets, exit semantics
and misleading historical-status wording. It also replaced an incomplete web
comparison and private-network placeholder with scoped, sourced reference notes.
Private networking remains a coverage gap, not a newly implemented lab.

Validation includes all-lab catalog/navigation checks, repository Markdown
links/heading anchors/code fences, 110 documented CLI examples across the
two lab groups, 23 early-lab CLI help checks, 47 early-lab Bicep-output
references, 89 later-lab shell blocks, existing documentation regressions and
Git whitespace checks. The four focused root documentation/catalog tests pass.
These checks do not prove current Azure availability or resolve native blockers.

## Status guide across the collection

| Labs | Latest recorded evidence and limitations |
| --- | --- |
| 0 | Sampling, chat/reasoning comparison and all three native batch requests live-verified on 2026-11-09; see the factual caveat above. |
| 1-13 | Recent live recording passes for 1-9, 11 and 13; 10 lacked verified inference and 12 was identity-blocked. |
| 14-26 | Live recordings passed for 14-19 and 22-26; 20 lacked a completed human approval and 21 required two real identities. |
| 27 | Managed ingestion and agent API gates passed; authenticated portal visual check remains unverified. |
| 28-33 | Live recordings passed for 28, 29, 30 and 33; 31 and 32 remained blocked by identity or browser/approval prerequisites. |
| 34 | Document Intelligence twelve-PDF corpus live-verified. |
| 35 | Deployed; native Work IQ authorization remains blocked. |
| 36 | Hosted runtime, session behavior and telemetry live-verified. |
| 37 | UI/agents/telemetry verified; complete native automatic-evaluation gate blocked. |
| 38-46 | All deployed; seven native/API live passes, with 43 and 45 blocked; 46's portal remains unverified. |

## Labs 1-13: historical baseline and later follow-ups

Undated baseline entries are preserved as historical observations, not fresh
2026-11-03 tests.

| Lab | Recorded result |
| --- | --- |
| [1 - Model Router](./1-model-router/README.md) | Passed; dated evidence in backlog F001 (2026-10-25). |
| [2 - Workflow Agent](./2-workflow-agent/README.md) | Historical pass after retry. |
| [3 - Web search](./3-webiq-agent/README.md) | The 2026-11-08 live comparison made one search with three citations; the tool-free run made none. |
| [4 - Agent tools](./4-agent-tools/README.md) | Passed 2026-10-26: Windows P0v3 in West US 2; real Function -> Toolbox -> agent. |
| [5 - Decision Model](./5-decision-model/README.md) | Passed 2026-10-26: DataZoneStandard; 3/3 tickets plus custom request, bounded throttling retries. |
| [6 - Content Safety](./6-content-safety/README.md) | Historical pass. |
| [7 - Semantic cache](./7-semantic-cache/README.md) | Passed 2026-11-09: correct arithmetic responses and a separately traced gateway miss followed by a paraphrase hit. Earlier local-cache evidence does not cover this stack. |
| [8 - Agent evaluation](./8-agent-evaluation/README.md) | Passed 2026-10-26: real inline-data cloud evaluation, 2/2 coherence passes. |
| [9 - Red teaming](./9-agent-red-teaming/README.md) | Historical pass after retry. |
| [10 - Fine-tuning](./10-fine-tuning/README.md) | Managed classification training succeeded 2026-10-26; its inference not run. Local Phi culture/cuisine training verified 2026-11-05: whole-answer audit 1/16 to 11/16, concept rubric 2/16 to 15/16, six controls maintained. |
| [11 - Foundry IQ](./11-foundry-iq/README.md) | The project-enabled live recording on 2026-11-08 answered workshop schedule and prerequisite questions with retrieved references. |
| [12 - Entra Agent ID](./12-entra-agent-id/README.md) | Blocked by interactive identity setup/credentials; fail-fast and mocked cleanup checks are not live inference evidence. |
| [13 - Orchestrations](./13-agent-orchestrations/README.md) | Historical pass for all four patterns after startup retry. |

## Labs 38-46: selected coverage additions, 2026-11-03

The repeated selection of review priority 6 is implemented once. Only priorities
1, 3, 4, 6, 7, 8, 9, 10 and 11 are included, as labs 38-46. The active Azure
subscription is `1217c203-6c76-47b4-b61c-9bba62dc8e02`. Offline tests, ARM
deployment and native feature verification are separate gates.
Final offline verification passed **178/178 new-lab tests**. All nine Azure
deployments succeeded. Current strict native/API live gates passed for seven
labs; labs 43 and 45 remain blocked as detailed below.

| Lab | Offline tests | Azure deployment | Native live verification |
| --- | --- | --- | --- |
| [38 - Guardrails](./38-native-guardrails/README.md) | 14 passed | Passed, East US 2 | Passed: native prompt-agent attachment, allowed 200 and user-input 400/ContentFiltered; model baseline also passed |
| [39 - Gateway limits](./39-gateway-token-limits/README.md) | 19 passed | Passed, East US 2 | Passed: real APIM 429/403 and independent project 200; three same-hour runs including shared-runner replay |
| [40 - Voice agent](./40-voice-agent/README.md) | 16 passed | Passed, East US 2 | Passed: native spoken WebSocket turn, audio, transcripts and persisted conversation |
| [41 - Content Understanding](./41-content-understanding/README.md) | 32 passed | Passed, East US 2 | Passed: GA document fields/confidence/grounding and native audio facts/timestamps |
| [42 - Harness and Tool Search](./42-managed-harness-tool-search/README.md) | 12 passed | Passed, Sweden Central | Passed: managed Copilot harness with pinned skill; separate native Toolbox tool_search/call_tool |
| [43 - Routines](./43-agent-routines/README.md) | 24 passed | Passed, East US 2 | **Blocked on latest root replays**: native agentic-token issuance HTTP 400 despite creator dispatch identity; earlier direct timer passed; cleanup verified |
| [44 - Evaluation and optimizer](./44-evaluation-optimizer/README.md) | 27 passed | Passed, East US 2 | Passed final root replay: generated/tagged dataset, cloud evaluators, completed native optimizer candidate, independent heldout 0.4 to 1.0; APPROVE without promotion |
| [45 - Governed IQ](./45-governed-iq/README.md) | 18 passed | Passed, Foundry East US 2 / Search West US 2 | **Blocked overall**: native source-permission checks passed; missing second real user and enabled Fabric tenant/capacity prevent complete verification |
| [46 - Fleet governance](./46-fleet-governance/README.md) | 16 passed | Passed, East US 2 | Passed API gates: two projects, three agents including external registration, correlated telemetry and Compliant policy; portal **NOT_VERIFIED**, Defender/Purview optional |

### Verified shared-runner replay

Both `python3 deploy.py --demo 38` and `python3 deploy.py --demo 39` passed.
Guardrail coverage is the native user-input boundary; tool-call/tool-response
controls are explicitly not configured. Gateway replay creates fresh
centrally configured demonstration counters without raising the configured
quotas; exhausted counters from an earlier run do not silently invalidate the
next presentation. This is an isolated workshop reset, not a production
quota-bypass mechanism.

`python3 deploy.py --demo 40 --demo 41` completed with exit 0: both local test
suites, location/authentication preflights, ARM redeployments and native live
demos passed. Both scripts load root-shaped deployment records and require no
additional runtime output mappings. Live runs are noninteractive and use
repository-stored synthetic WAV/PDF inputs.

Content Understanding uses `gpt-5.2`, version `2025-12-11`, capacity 10. Document
extraction requires actual service field confidence and source grounding.
GA audio fields use the supported generation mode and real transcript
timestamps; absent field-confidence values remain null rather than being
invented or borrowing word confidence.

`python3 deploy.py --demo 42 --location swedencentral` completed with exit 0.
East US 2 is not supported for the selected managed harness; the lab must be
deployed separately in a supported region. Its read-only native preflight
runs after ARM outputs are available, before agent operations.

Lab 43's earlier direct native run
`85b52baa-72cf-4bf6-836f-3a3d4698525c` verified timer dispatch, persisted
creator identity, lifecycle and linked response/trace. However, repeated exact
`python3 deploy.py --demo 43` replays returned HTTP 400: the service could not
acquire the target agent's agentic identity token. The final request ID is
`ac74630a-db58-44b3-9c5c-df51c46a1ac5`. Creator dispatch identity readback,
ARM readiness and enabled identity/blueprint principals were verified; these
do not establish the underlying tenant/service cause. It remains unresolved,
not a proven default-identity bug. Agent identity is explicit opt-in and
never silently falls back. Temporary routines, conversations, agents and
roles were deleted. Current strict live status is **BLOCKED**, not PASS.

The final exact `python3 deploy.py --demo 44` replay passed. An earlier replay
found that generated dataset tags were not retained and had no completed
optimizer candidate; that attempt is not counted as success. The corrected
native dataset path applies and verifies tags through supported PUT/GET.
The final native candidate
`cand_opt_1c8e015c36e74e55b7ab0ec45d44d3ad_0001` completed; independent
heldout score improved from **0.4 to 1.0**, yielding **APPROVE without
promotion**. An earlier candidate tied the baseline at 5/5 and was correctly
REJECTED without promotion. Defaults remain bounded to two generated training
rows, one heldout question and one optimizer candidate; this is a workshop
release-gate example, not statistically sufficient production certification.

`python3 deploy.py --demo 45 --search-location westus2` deployed successfully
and passed all 18 offline tests, but correctly returned nonzero on its strict
live gate. Blob ingestion synchronized `rbacScope` permissions; a real source
GET returned 200, authorized protected retrieval produced a reference, and
omitting `x-ms-query-source-authorization` denied retrieval. That omission is
not a second real identity. Fabric and Power BI token requests returned
`AADSTS500014`; the subscription has zero Fabric capacities and no configured
Fabric workspace/data-agent identifiers. No CSV/local retrieval is used as
a replacement for this blocked native gate. Completion requires a real
second-user login, an enabled/licensed Fabric tenant and a real supported
Fabric connection.

`python3 deploy.py --demo 46` completed with exit 0. All three actual
agent executions have ingested correlated traces, including the natively
registered external agent. The audit-only keyless-access Azure Policy is
scoped to this lab's resource group and reports Compliant. This verifies API
fleet gates, not the portal Control Plane UI or optional Defender/Purview
integration.

### Deployed resource groups

| Lab | Resource group |
| --- | --- |
| 38 | `rg-foundry-native-guardrails-demo` |
| 39 | `rg-foundry-gateway-token-limits-demo` |
| 40 | `rg-foundry-voice-agent-demo` |
| 41 | `rg-foundry-content-understanding-demo` |
| 42 | `rg-foundry-managed-harness-tool-search-demo` |
| 43 | `rg-foundry-agent-routines-demo` |
| 44 | `rg-foundry-evaluation-optimizer-demo` |
| 45 | `rg-foundry-governed-iq-demo` |
| 46 | `rg-foundry-fleet-governance-demo` |

Resources remain deployed and billable, including the APIM Developer instance
in lab 39. Temporary routine triggers must be removed after tests. Sanitized
deployment records and native evidence are retained in each lab's ignored
`.azure/` directory; labs 40 and 41 retain `verification.json` and shared-runner
`demo.log`. Temporary lab 43 triggers/assets were verified deleted, including
after failed live attempts.

The shared-runner baseline before these additions had 34/35 passing tests.
Its lab 13 model-resource tag-layout assertion already failed and remains
unrelated to these additions. The current expanded suite has **37/38 passing
tests**, with only that same pre-existing assertion failing. New catalog,
ownership, tag-schema exceptions, region gates and preflight regressions pass.
Shell syntax, whitespace and all 26 new/root Python source syntax checks pass.
Pylance MCP was unavailable because its local
transport failed initialization; executable tests and Python syntax checks
are used instead.

## Labs 36-37: deployed agent demos and live verification, 2026-10-27

Only the two new samples were deployed, in East US 2 in subscription
`1217c203-6c76-47b4-b61c-9bba62dc8e02`. Both ARM deployments succeeded and were
successfully repeated through their standalone infrastructure-only runners.

| Verification | Lab 36: hosted agent | Lab 37: production monitoring |
| --- | --- | --- |
| Resource group | `rg-foundry-hosted-agent-demo` | `rg-foundry-production-monitoring-demo` |
| Final local tests | 10 passed | 14 passed |
| Deployed agents | Custom-code hosted version 2, native Invocations 2.0.0 | Monitored versions 1/2 and stable version 1, native Responses endpoints |
| Real cloud execution | USD 75/100/50; repeated session retained its marker and incremented its counter; independent session remained isolated | UI baseline USD 25/25, regression USD 40/25, recovery USD 25/25 |
| Azure telemetry | All three exact invocation trace IDs matched named spans, including model and tool spans | All six browser response IDs matched actual server-side agent names and served versions |
| Browser UI | Latest hosted version, repeated and fresh sessions passed | All three actual cloud scenarios passed; missing native verdicts correctly stayed PENDING |
| Complete native live gate | **Passed** | **Blocked, not passed:** zero automatic evaluation runs; baseline wait timed out after 20 minutes |

Lab 37's native rules read back enabled with 100% sampling and a 30-run/hour
limit per agent. Project-identity evaluation and monitoring roles were deployed.
Both managed-endpoint requests and the documented project/conversation request
flow returned actual responses, but neither yielded automatic evaluation runs.
A separate manual cloud exact-match evaluation succeeded; it was diagnostic
only, was deleted, and cannot satisfy the native monitoring gate. The supported
native schedule alternative was also probed, but the service rejected the SDK
schedule requests; no temporary schedules remain. The precise event-triggering
failure is unresolved. Both good agent routes are restored.

Ignored local evidence:
[hosted live report](./36-hosted-agent/.azure/live-report.json),
[hosted unit tests](./36-hosted-agent/.azure/unit-tests.log),
[monitoring live gate](./37-production-monitoring/.azure/live-report.json),
[monitoring timeout log](./37-production-monitoring/.azure/live-test-final.log),
[native rule/run diagnostics](./37-production-monitoring/.azure/native-monitoring-diagnostics.json),
[six-request browser/trace report](./37-production-monitoring/.azure/ui-live-report.json),
and [monitoring unit tests](./37-production-monitoring/.azure/unit-tests.log).

The shared deployment suite has **34/35 passing tests**. Its only failure is the
previously documented lab 13 model-resource tag-layout assertion; neither new
lab fails that check. Catalog/discovery, backlog, manual tags and standalone
runner integration checks pass. No existing demo implementation was changed.
Both UIs remain available on loopback ports 8036 and 8037, and resources remain
deployed and billable. Lab 37 must not be presented as fully live-validated until
automatic evaluation triggering is resolved and its complete gate passes.

## Lab 35: per-user Work IQ deployment and blocked live validation, 2026-10-27

[Lab 35](./35-work-iq/README.md) is implemented and deployed to
`rg-foundry-work-iq-demo` in East US 2. It is **not live-validated for Work IQ**.

| Verification | Observed result |
| --- | --- |
| Bicep build/lint and ARM deployment | Passed; Foundry account, model, project and developer RBAC deployed successfully |
| Final offline validation | 13 lab tests passed; Python compilation and Pylance syntax checks passed; Pylance reported no diagnostics for the client and connection script |
| Persistent native agent | `work-iq-user-agent` version 1 created and read back with native `work_iq_preview` and the expected connection resource ID |
| Authenticated caller | Real Graph `/me` returned the Azure CLI user's object ID and principal name; no profile values were supplied to the model |
| Shared deployment workflow | Local tests passed, infrastructure deployed, then live phase exited nonzero on the missing OAuth connection |
| Direct saved-agent diagnostic | Actual Foundry Responses request returned HTTP 400, `tool_user_error`: the connection does not have a valid target URL |
| Work IQ tenant setup | Service-principal lookup for documented Work IQ app ID `fdcc1f02-fc51-4226-8753-f668596af7f7` returned an empty list |
| Connected Work IQ MCP | Requires authentication; no user data was retrieved |
| Work IQ lookup / two-user behavior | **Blocked, not passed:** administrator setup, billing confirmation and individual user sign-in were unavailable |

Ignored local evidence:
[deployment outputs](./35-work-iq/.azure/deployment.json),
[root live transcript](./35-work-iq/.azure/demo.log) and
[native service diagnostic](./35-work-iq/.azure/native-diagnostic.json).
The diagnostic explicitly records `work_iq_live_pass: false`.
Caller-specific reports are private mode-0600 files and remain `complete: false`.
No OAuth secret, access token or mailbox/file content is saved.

The lab's instructions explain administrator consent, secure OAuth connection
creation, redirect registration, caller-bound authorization continuation, and
two-user comparison. The script must not be presented as a completed live Work IQ
demonstration until those steps and real retrieval succeed. Azure resources and
the native agent remain deployed for that continuation.

Targeted shared tests found the existing lab 13 model-resource tag-layout
assertion failure, already recorded below; lab 35's template passed that check.
Final shared-runner regression result: **34/35 passed**, with only that same
pre-existing lab 13 assertion failing. Catalog/discovery, the new tenant-output
mapping, documentation and cleanup regressions passed.

## Lab 34: Document Intelligence agent live validation on 2026-10-27

Implemented [lab 34](./34-document-intelligence/README.md) and deployed
`rg-foundry-document-intelligence-demo`. Foundry/Document Intelligence are in
East US 2; the Windows P0v3 Function plan and Storage are in West US 2.
The agent, project and all resources remain deployed and billable.

### Expanded twelve-PDF corpus

The original two documents and ten additional synthetic invoices now live in
[samples](./34-document-intelligence/samples/), with a deterministic generator
and a separate ground-truth manifest. Bicep packages the exact PDF bytes rather
than generating documents inside the Function. The submitted PDF hash is
returned by the tool and verified against the repository on every analysis.

| Verification | Observed result |
| --- | --- |
| Full rerun | `python3 deploy.py --demo 34` exited 0 after deployment and all quality gates |
| Offline validation | 13 Python tests, including 7 Node subtests, passed; Bicep build/lint and final Python compilation passed |
| File preservation | 12 distinct, reproducible one-page PDFs; compiled Bicep base64 payloads independently matched all repository files |
| Direct extraction | 12/12 successful real Document Intelligence analyses |
| Agent extraction | 24/24 requests passed: two independent requests for every sample |
| Ground truth | 96/96 agent field comparisons correct; all direct values also correct |
| Provenance | All 36 successful analyses had distinct operation IDs and matching repository PDF SHA-256 |
| Missing values | All 6 agent cases across 3 missing-total PDFs returned null total/currency and required review |
| Edge cases | Explicit USD zero stayed zero; EUR 315.20 retained EUR; varied vendors and amounts matched exactly |
| Confidence | Present fields in complete invoices ranged from 0.819 to 0.988, passing the unchanged 0.8 gate |
| Persistent agent | Version 2 read back independently; all twelve document IDs present in its OpenAPI schema |
| Endpoint guards | Unauthenticated request rejected with 401; authenticated unknown document rejected with 400 |

Two live issues were corrected before the successful full rerun:

- Windows ARM file deployment added a UTF-8 BOM to the bundled JSON. The Function
  now handles that encoding explicitly; a regression test exercises BOM input.
- The larger corpus hit the model's HTTP 429 request limit. Requests now bound
  output to 512 tokens and retry throttling at most five times, honoring numeric
  service retry headers with a 120-second maximum delay. Exhaustion still fails.
  Final execution encountered handled throttling, but no unhandled application
  errors or failed quality gates.

The current [quality report](./34-document-intelligence/.azure/quality-report.json)
contains the twelve direct results and all twenty-four cloud responses.
The [live transcript](./34-document-intelligence/.azure/demo.log) retains earlier
failed attempts as well as the final successful run. No secrets are recorded.
Pylance's syntax call timed out; executed tests and final Python compilation
passed. Later repository-wide contract checks encountered an unrelated
concurrently created, incomplete lab 35 (`35-work-iq` lacked its entrypoint at
that time); that separate work was not modified.

These are twelve controlled PDFs sharing a simple layout, not a representative
production dataset. Success establishes exact sample-file processing and
grounded responses for this corpus, not general invoice accuracy.

### Initial two-document baseline

| Verification | Observed result |
| --- | --- |
| Documented deployment workflow | `python3 deploy.py --demo 34` exited 0 after Bicep build/lint, dependency setup, local tests, deployment and live checks |
| Offline tests | 10 Python tests, including 6 Node subtests, passed; syntax/compilation checks passed |
| Direct Document Intelligence | Both synthetic one-page PDFs analyzed successfully; exact fields and source/page evidence verified |
| Persistent cloud agent | `document-intelligence-agent`, version 1, in project `document-intelligence`; definition read back and saved versions independently listed |
| Cloud tool execution | Native OpenAPI -> key-protected Function -> managed-identity Document Intelligence; no local tool execution |
| Repeated agent quality | Final deployment: 4/4 requests; separate ask-only run: 6/6; 40/40 field comparisons across the ten requests |
| Complete invoice | Vendor Synthetic Workshop Supplies, invoice LAB-3401, USD 125.50; confidence 0.929 / 0.988 / 0.929 |
| Missing-total invoice | Vendor and LAB-3402 preserved; total/currency null and review required in all 5 agent cases |
| Grounding | Every answer matched its actual tool output and unique real operation ID; completed call/output IDs correlated |
| Endpoint guards | No key: HTTP 401; authenticated unknown document: HTTP 400, both expected |

Initial live testing exposed a Function host startup response (HTTP 400 from
the host-key ARM operation) and an incorrect assumption about the OpenAPI
response shape. Startup retry now narrowly recognizes the host-runtime error.
Cloud results are correlated through `openapi_call` / `openapi_call_output`;
the latter contains a JSON `response` envelope. Raw service JSON preserves
Foundry-specific response items without OpenAI/Pydantic union serialization
warnings. Regression tests cover both fixes. The final live deployment reported
no application errors.

Local ignored evidence:
[final quality report](./34-document-intelligence/.azure/quality-report.json),
[six-request repeat report](./34-document-intelligence/.azure/repeated-quality-report.json)
and [live transcript](./34-document-intelligence/.azure/demo.log). The transcript
also retains the initial unsuccessful attempt for diagnosis; reports mark only
the verified runs complete. No Function keys are saved in these files.

Shared-runner regression tests: **33/34 passed**. The remaining failure is the
pre-existing tag-layout assertion for lab 13's model resource, in an already
modified file unrelated to this lab; it was not changed. Lab 34's template
passed that tag check, catalog/backlog/discovery checks and its own end-to-end
deployment. A final Pylance recheck timed out; Python compilation and executed
tests passed, and earlier Pylance syntax checks passed.

Quality assessment: exact extraction/grounding and appropriate abstention passed
for these two controlled PDFs. This does not establish accuracy on real-world
invoices or automate a production human-review process. The fixed Function plan
has ongoing hourly cost; follow the lab cleanup instructions after presenting.

## Labs 29-33: offline validation on 2026-10-26

All five additions have standalone Bicep, dependency manifests, runnable examples,
offline tests, deployment instructions, cost/preview notes and cleanup guidance.
At that validation point the catalog and backlog covered 33 demos; the
collection now has 46. This historical validation did not deploy Azure.

| Lab | Offline Python tests | Evidence checked |
| --- | --- | --- |
| [29: Toolbox skill discovery](./29-toolbox-skill-discovery/README.md) | 11 | Real SDK/MCP model shapes; metadata pagination; relevant skill selection; on-demand checklist reads; contract checks; URI bounds and cleanup |
| [30: Skill-powered analyst](./30-skill-data-analyst/README.md) | 16 | Pinned skill bytes; Code Interpreter execution/citations; downloaded report/PNG validation; independent totals and cleanup |
| [31: Native memory](./31-native-agent-memory/README.md) | 20 | Real API wire serialization/polling; two-user authentication; scoped extraction/search/deletion; fresh recall conversations and cleanup |
| [32: Browser runbook](./32-browser-runbook/README.md) | 21 | Native browser SDK shapes; remote tool evidence; fixture state; exact-action approval/replay rejection; configuration preflight and cleanup |
| [33: Skill release governance](./33-skill-release-governance/README.md) | 9 | Candidate gate; unchanged default before promotion; pinned isolation; read-back promotion; regression failure and rollback |

All **77 lab tests** passed. Lab 32's suite also runs the Node fixture checks.
The root runner passed **34 tests**, including discovery/selection of all 33 labs,
preflight failures before Azure changes, supported/unsupported browser regions,
and tag rules for taggable resources (management policies cannot carry ARM tags).
All five Bicep templates built and linted successfully; Python sources compiled
and parsed using Python 3.10 syntax rules. Pylance was unavailable.

Validation used isolated environments: Projects SDK 2.7.0 for labs 29/30/31/33,
and 2.8.0 for lab 32's native Browser Automation Toolbox model. Failure-path tests
intentionally print some rejected-action/cleanup/CLI errors; their assertions
pass and no real service calls are made.

No Azure resources, paid inference, real MCP browser sessions, skill operations,
or memory calls were run. Live preview support, regional quota and actual model
behavior remain unverified. Lab 31 requires two real user logins; lab 32 requires
a supported region such as `eastus`, remote MCP preview access and a manually
created project-managed-identity Browser Automation connection. Its default run
never approves submission.

## Lab 28: Agent skills offline validation on 2026-10-26

[Agent skills](./28-agent-skills/README.md) passed 13 focused offline tests,
32 root runner tests, Pylance syntax checks, and Bicep build/lint.
Tests cover SDK multipart ZIP upload, pinned-version download and byte comparison,
with/without-skill requests, exact output validation, and cleanup after failures.
The SDK upload uses a binary stream to preserve ZIP bytes during model
serialization. The new folder is discovered and selectable as `--demo 28`.

No Azure deployment or live Skills API/model calls were performed for this lab.
Preview availability, actual agent instruction following, and data-plane deletion
still require a live smoke test; mocked responses are not service evidence.

## Lab 27: Storage-backed Foundry IQ and persistent portal agent on 2026-10-26

Implemented [27-FoundryIQwithKB](./27-FoundryIQwithKB/README.md) and deployed
`rg-foundry-FoundryIQwithKB-demo`. Foundry and Storage are in East US 2;
keyless Basic Search is in West US 2. Resources remain running and billable.

| Verification | Actual result |
| --- | --- |
| Synthetic Blob corpus | Eight private blobs, independently read back; four case and four Surface runbook documents |
| Managed ingestion | Two real `azureBlob` knowledge sources; four chunks each; exact indexed text matched every local document |
| Managed embeddings/refresh | Generated vector fields and Azure OpenAI vectorizers; hourly schedules |
| Foundry IQ planning | Compound question produced actual LLM planning and six Blob subqueries across both sources |
| Persistent agent | `surface-support-agent`, version 1, in project `surface-support`; definition independently read back |
| New-portal integration | AAD `CognitiveSearch` connection plus `RemoteTool` KB MCP connection using `ProjectManagedIdentity`; both independently read back |
| Compound answer | Real successful MCP retrieval; cited CASE-1001/1002 and KB-SURFACE-001/002 |
| Arm64 follow-up | Cited CASE-1003/KB-SURFACE-003 and the approved Arm64 VPN client |
| Unknown question | Explicitly abstained on exact warranty pricing and future Surface specifications after real retrieval |
| Incremental change | One changed runbook processed, zero case documents; same agent's cited answer changed from 2 business days to 1 |
| Restore | Local/deployed text restored; fresh agent response confirmed 2 business days |
| Offline validation | 16 lab tests, 32 root runner tests, Python compilation, and Bicep build/lint passed |
| Authenticated portal visual test | Not performed: browser required interactive sign-in; APIs verified actual project/agent/connections |

Six live scenarios were independently asserted. Evidence is retained in ignored
`27-FoundryIQwithKB/.azure/live-verification.json`, `live-final.json`,
`live-arm64.json`, `live-unknown.json`, and `live-refresh-*.json`, with transcripts.
The lab does not claim ACL/Purview synchronization, end-user authorization,
Copilot Studio deployment, or general production answer quality.

## Labs 3 and 17: fresh live validation on 2026-10-26

Both labs passed actual Azure calls in subscription
`1217c203-6c76-47b4-b61c-9bba62dc8e02`; offline fixtures are not counted as live evidence.

### Lab 3: WebIQ Agent

The recorded previous group had been deleted. Created the dedicated
`rg-foundry-webiq-agent-live-20261026` group in East US 2 and deployed the current
`gpt-5-mini` template. The first deployment failed with `RequestConflict` while
project/model child writes overlapped. Added the same explicit project-after-model
dependency used by other labs; redeployment succeeded.

| Scenario | Actual response | Completed web-search calls | Structured citations | Independent cleanup check |
| --- | --- | --- | --- | --- |
| Default model-router question | Completed; exit 0 | 1 | 3 | Agent version GET returned 404 |
| Custom Agent Service question | Completed; exit 0 | 1 | 5 | Agent version GET returned 404 |

Verified actual response JSON, including `web_search_call` items with status
`completed` and message `url_citation` annotations. The default sources were
Microsoft Learn model-router documentation; the custom question returned Agent
Service sources. No grounding assertion was weakened and no answer was substituted.
The old missing-grounding failure was not reproduced with this current deployment;
this run does not establish its historical cause.

Nine offline tests and Bicep build/lint passed separately. Local ignored evidence:
`3-webiq-agent/.azure/live-default-2026-10-26.log`,
`3-webiq-agent/.azure/live-custom-2026-10-26.log`, and
`3-webiq-agent/.azure/live-2026-10-26.json`.

### Lab 17: RAG fundamentals

Reused `rg-foundry-rag-fundamentals-demo`: Foundry remained in East US 2 and
Basic Search in West US 2. Real embedding, indexing, retrieval and generation
completed with exit 0 for all **24 cases**. Independently parsed **18 cited/quoted
answers and six abstentions**, with empty citations/quotes for every abstention.
Runtime checks validated every answer's citations/quotes against retrieved evidence.

| Chunk words | Indexed chunks | Retrieval mode | Recall@3 | MRR@3 |
| --- | --- | --- | --- | --- |
| 12 | 11 | Keyword | 1.000 | 0.833 |
| 12 | 11 | Vector | 1.000 | 1.000 |
| 12 | 11 | Hybrid | 1.000 | 1.000 |
| 48 | 3 | Keyword | 1.000 | 1.000 |
| 48 | 3 | Vector | 1.000 | 1.000 |
| 48 | 3 | Hybrid | 1.000 | 1.000 |

Metrics matched the original run. Index-name listing before and after confirmed
**no new indexes remained**. Six offline tests passed separately. Ignored evidence:
`17-rag-fundamentals/.azure/live-2026-10-26.log` and
`17-rag-fundamentals/.azure/live-2026-10-26.json`.

Both labs' infrastructure remains deployed. Lab 17's Basic Search continues
charging while provisioned, even though the temporary indexes were removed.

## Lab 10: completed paid fine-tuning on 2026-10-26

Reproduced HTTP 400 `invalidPayload` with
`The fineTuningJob field is required.` The request used an invalid training type:
`"Global"` instead of the documented v1 value `"GlobalStandard"`.
Changing that value alone produced an accepted job; no wrapper field was added.

Deployed the dedicated account and base model in East US 2 under
`rg-foundry-fine-tuning-demo`, then submitted exactly one real paid job using
the original 12 synthetic examples and one supervised epoch.

| Verified property | Actual result |
| --- | --- |
| Job ID | `ftjob-1f5660c4acff48438874163995f91d99` |
| Base model | `gpt-4.1-mini-2025-04-14` |
| Final status | `succeeded`; no error |
| Training type / epochs | `globalStandard` / 1 supervised epoch |
| Trained tokens | 663 |
| Fine-tuned model ID | `gpt-4.1-mini-2025-04-14.ft-1f5660c4acff48438874163995f91d99-workshop` |
| Results file | `file-d594c3b94d3e4ecbbb0cda03301db818`; downloaded, 742 bytes |
| Independent readback | Retrieved final job, checked epoch/model/token count and exactly one account job, downloaded nonempty results |
| Local validation | Seven regression tests, Bicep build/lint |
| Optional trained-model deployment / held-out inference | Not performed; no accuracy improvement claimed |

The service took approximately 71 minutes from submission to success, including
GPU queueing and finalization. The local 30-minute wait expired twice; resumed
the same job using `--job-id`, without another paid submission.
The script now persists the initial job ID and each polled status to ignored
`.azure/fine-tuning-job.json`, retaining the final model ID on success.

Evidence is retained locally in ignored `10-fine-tuning/.azure/`:
`live-before-fix.log`, `live-submit-fixed.log`, `fine-tuning-job.json`,
`live-training-wait.log`, `live-training-resume.log`,
`live-training-finalization.log`, `live-testing-summary.json`, the downloaded
results CSV, and `tests-after-fix.log`. Resources and trained model remain
available; deleting the account/resource group deletes the retained model.
Training was paid; optional trained-model hosting was not provisioned.

## Lab 10: local Phi domain adaptation on 2026-11-05

Added an independent Apple-silicon MLX example; the original Azure support-ticket
classification code/data and infrastructure are unchanged. Real 4-bit
Phi-3.5-mini inference, QLoRA training and adapted inference ran locally.

The first 120-step experiment improved its concept rubric from 4/16 to 13/16
but regressed on one of four controls and was rejected. The revised experiment
uses 64 domain and 16 general-task replay training rows, 20 validation rows,
and a newly frozen confirmation set of 16 domain questions plus six controls.
The fixed final 160-step adapter was not selected using test performance.

| Verified property | Result |
| --- | --- |
| Base | `mlx-community/Phi-3.5-mini-instruct-4bit`, revision `7b2052fd882fe017300d4d42a4eb06a27b816af4` |
| Adaptation | Rank 8, eight layers, batch 2, 160 steps, learning rate `5e-5`, assistant-only loss |
| Trainable parameters | 3.146M / 3821.080M (0.082%) |
| Final training / memory | About 22 seconds after model loading / 3.11 GB peak MLX memory on M5 Pro |
| Confirmation concept rubric | 2/16 to 15/16; gain 81.25 percentage points |
| Stricter whole-answer audit | 1/16 to 11/16; assistant qualitative review, not independent human evaluation |
| Confirmation controls | 6/6 before and after |
| Reproducibility | Fresh `--stage all` repeat produced identical adapted answers to staged run |
| Offline validation | 21 classification/local workflow/evidence tests; Python compilation; dependency check |
| Azure training / deployment | None for the new example |

The rubric is intentionally transparent but narrow: some incorrect answers
pass because they mention expected concepts. The
[whole-answer audit](./10-fine-tuning/polish-results/answer-audit.json) rejects
five adapted answers for factual mistakes or unsupported/contradictory claims.
The splits hold out question wording, **not facts or entities**. This does not
establish broad knowledge, Polish-language fluency, or general non-regression.
The final adapter also passed a diagnostic recheck of all four original
development controls, repairing the earlier cat-to-Spanish translation failure.

[Raw baseline](./10-fine-tuning/polish-results/baseline.json),
[adapted answers](./10-fine-tuning/polish-results/tuned.json),
[comparison](./10-fine-tuning/polish-results/comparison.json), and training
metadata/log are retained in the repository. The roughly 12 MB adapter and
input snapshots remain locally in ignored `10-fine-tuning/.azure/polish-final/`.
No model weights were added to source control.

## Lab 11: Search capacity fix and managed retrieval on 2026-10-26

The original deployments were blocked by Search capacity in East US 2 and
Sweden Central. Added an independent `searchLocation` parameter, defaulting to
West US 2, while keeping Foundry/model deployment in East US 2.
West US 2 supports agentic retrieval and semantic ranker. Actual deployment to
`rg-foundry-foundry-iq-demo` succeeded: Basic Search is running with one
replica/partition, managed identity, and API keys disabled.

| Live check | Actual result |
| --- | --- |
| Index/source/base | Three exact synthetic documents indexed; managed knowledge source and answer-synthesizing knowledge base configured |
| Default question | Friday at 10:00, 45 minutes, laptop, Python 3.11 and Azure CLI; schedule/equipment citations |
| Custom question | Free attendance, registration before Thursday, no prior AI experience; cost/equipment citations |
| Managed activity | Real query planning, three/two Search subqueries respectively, answer synthesis; no reported activity errors |
| Grounding validation | Every source document matched the exact input; answer citation IDs resolved to the required documents |
| Independent API readback | Document count 3, correct source/base, and cross-region Foundry model endpoint |
| Offline validation | Six lab tests and all 27 deployment-runner tests passed; Bicep build/lint passed |

The root runner already supports the new parameter:

```bash
python3 deploy.py --demo 11 --location eastus2 --search-location westus2
```

Search cannot be moved between regions in place; use a new dedicated group
when changing the Search region. Capacity varies and is not reserved by this
successful deployment.

Evidence is retained locally in ignored `11-foundry-iq/.azure/`:
`deployment.json`, `live-default.log`, `live-custom.log`,
`live-testing-summary.json`, and `tests-after-fix.log`.
Resources remain deployed; Basic Search incurs continuous provisioning charges
and planning/synthesis incur usage charges.

## Lab 8: response-ID evaluation fix on 2026-10-26

Reproduced the original HTTP 404 against a fresh East US 2 deployment in
`rg-foundry-agent-evaluation-demo`. Failed cloud run
`evalrun_1a2899c0709440e8ab01b6f241ec9ec4` reported
`Response with id ... not found`. A same-ID probe confirmed **agent-scoped
retrieval HTTP 200 with identical text, project-scoped retrieval HTTP 404**.
The script had created agent-scoped responses then asked the project evaluator
to retrieve them; `store=True` did not bridge those scopes.

Changed the evaluation source to documented inline JSONL containing the exact
generated questions/answers and response IDs, with explicit coherence mappings.
This is still real cloud `builtin.coherence`, not a local surrogate or mock.
Corrected output-item validation to require processing status `completed` and
separate completed grader results, source matching, and aggregate consistency.
Serialized model/project deployment after a reproduced ARM `RequestConflict`.

Fixed run `evalrun_a87a335f1e5943f79ea9dbaa1522db58` completed with two
actual output items: **2 passed, 0 failed, 0 errored**, coherence **5/5** each.
Independently retrieved the cloud run/items and verified the two actual response
IDs. Temporary agent deletion was separately confirmed by HTTP 404.
Eight regression tests and Bicep lint passed, covering exact source mapping,
infrastructure failure, timeout, missing/mismatched/duplicate results,
valid low scores, and cleanup on evaluation failure.
Coherence does not prove factual accuracy or groundedness.

Evidence is retained locally in ignored `8-agent-evaluation/.azure/`:
`live-before-fix.log`, `response-scope-probe.json`, `live-after-fix.log`,
`live-testing-summary.json`, and deployment outputs. Resources remain deployed;
generation and judge inference are billable.

## Lab 5: Data Zone Standard verification on 2026-10-26

Changed `Microsoft-Decision-1` version `1` from `GlobalStandard` to
`DataZoneStandard` and deployed to `rg-foundry-decision-model-demo` in East US 2.
The former SKU failed with `SpecialFeatureOrQuotaIdRequired`; the Data Zone SKU
has subscription quota and deployed successfully. Capacity defaults to 10
instead of 1 after observed HTTP 429 responses.

All three default tickets returned the expected billing/technical/account
decisions, and a custom invoice request returned billing. Actual runtime
throttling remained stricter than ARM's reported 10 requests/minute. Explicit
bounded retries followed observed `retry-after-ms` delays of about 29-58 seconds.
Each ticket permits at most two retries, each no longer than 65 seconds.
Missing/excessive retry delays, exhausted retries, and other HTTP errors fail.
Allow several minutes for the live sequence under these observed limits.

Five offline tests and Bicep lint passed. Deployment outputs and successful
transcripts are retained locally in ignored
`5-decision-model/.azure/deployment.json`, `live-default-verified.log`, and
`live-custom-verified.log` in the same directory. Resources remain deployed;
actual inference is billable.

## Lab 4: non-Y1 hosting and live verification on 2026-10-26

Resolved the earlier Y1 quota blocker with a single-instance Windows **P0v3
dedicated App Service plan** in **West US 2**, with Always On. Foundry stays in
East US 2; `functionLocation=westus2` is now the template default for Function
hosting and storage independently of the Foundry region.

East US 2 ARM preflight rejected P0v3, P1v3, P0v4, and S1 with effective zero
quota. The quota-list API's reported P0v3 capacity did not match that preflight.
West US 2 P0v3 passed preflight and the actual deployment. A subsequent storage
shared-key error was fixed by using the Function's system-assigned identity
and storage-scoped Storage Blob Data Owner role. Shared-key access remains
disabled; no storage keys or Azure Files content connection string are needed.

| Check | Actual result |
| --- | --- |
| Resource group | `rg-foundry-agent-tools-demo` |
| Function host | Running, West US 2, P0v3, one instance, Always On |
| Direct valid API requests | 1/12/20/999 attendees returned USD 25/300/500/24975 |
| Direct invalid API requests | 0/1000/1.5/abc returned HTTP 400 and explicit errors |
| Agent, 12 attendees | Real Toolbox call returned `workshop-price-tool-v1`, USD 300, and an agent answer |
| Agent, 20 attendees | Real Toolbox call returned `workshop-price-tool-v1`, USD 500, and an agent answer |
| Temporary toolboxes | Both versions deleted after execution; independent Azure readback returned HTTP 404 |
| Offline validation | Six unit tests passed, including compiled ARM hosting/storage checks; Bicep lint passed |

Evidence is retained locally in ignored `4-agent-tools/.azure/deployment.json`,
`live-function-results.json`, `live-agent-12.log`, and `live-agent-20.log`.
The dedicated plan remains deployed and incurs continuous charges even when
idle or the app is stopped; delete the dedicated resource group after use.

## Added labs: live validation on 2026-10-25, follow-ups on 2026-10-26

The results above for labs 1-13 are historical and were not rerun in this pass.
All added labs 14-26 were attempted using the current Azure CLI subscription.
Provisioned resources remain deployed. Foundry resources are in East US 2;
lab 17's Search service is in West US 2 after an East US 2 capacity failure.

**Current summary: 12 passed, one blocked.** Lab 23's
GitHub Actions/OIDC coverage was completed on 2026-10-26; it had been unverified
during the original 2026-10-25 pass. Lab 14's real permission denial was also
completed on 2026-10-26 with a restricted certificate-authenticated identity.
Mocked tests and successful ARM deployment alone are not counted as live passes.

| Lab | Result | Live evidence |
| --- | --- | --- |
| [14 - Foundry fundamentals](./14-foundry-fundamentals/README.md) | Passed after identity/status fix on 2026-10-26 | The same restricted service principal passed real inference (83 tokens), streaming/usage (71 tokens), HTTP 404, and actual HTTP 401 `PermissionDenied`; full exit 0. The service uses 401, not the originally assumed 403, for the verified missing inference data action. Presenter roles were not changed. |
| [15 - Model benchmark](./15-model-benchmark/README.md) | Passed | 54 real requests across mini/full/router; task accuracy 83.3%/83.3%/50%. Genuine schema and truncation failures remain visible in the measurements. Benchmark execution passed, not every model prediction. |
| [16 - Structured outputs](./16-structured-outputs/README.md) | Passed | All 12 live strategy comparisons correct. Additional probes observed actual `length` truncation and HTTP 400 `content_filter`, distinguished from an assistant-message refusal. |
| [17 - RAG fundamentals](./17-rag-fundamentals/README.md) | Passed; full live rerun 2026-10-26 | Search in West US 2, Foundry in East US 2. Both chunk sizes and all retrieval modes passed 24 real cases: 18 cited answers and six abstentions. Recall@3=1; MRR=0.833 for 12-word keyword chunks and 1 otherwise. Independent readback confirmed no new indexes remained. |
| [18 - Agent tracing](./18-agent-tracing/README.md) | Passed | Queried nine actually ingested correlated spans. Observed the slow tool at 2005 ms and verified the failed tool's failure flag and safe error label. |
| [19 - Conversation state](./19-conversation-state/README.md) | Passed | 18 real calls verified the default scenario, separate-process persistence/resume, session isolation, history truncation, reset, and deletion. |
| [20 - Human approval](./20-human-approval/README.md) | Passed after fix | Real model proposals exercised rejection, exact approval, replay, wrong-digest rejection, and changed-payload rejection. Exactly one synthetic booking was created. |
| [21 - Permission-aware retrieval](./21-permission-aware-retrieval/README.md) | Blocked; provisioning attempted 2026-10-26 | Presenter Graph profile/memberships succeeded and ten offline tests passed. Authorized test-user creation returned HTTP 403 `Authorization_RequestDenied`; presenter has Entra Global Reader. No user was created and temporary credentials were removed. An administrator must create/authorize the second user before login, deployment and six live question/user checks. No live permission-isolation pass is claimed. |
| [22 - Prompt injection](./22-prompt-injection/README.md) | Passed after fix | Prompt Shields flagged the document override but not the tool-output override. Azure inference filtering blocked both raw baselines. Both separately tested protected paths returned the validated `09:00` fact. |
| [23 - Evaluation CI/CD](./23-evaluation-cicd/README.md) | Passed, including GitHub on 2026-10-26 | [Baseline Actions run](https://github.com/mifurm/FoundryWorkshopDEMOS/actions/runs/40281692169) succeeded at 100% correctness/100% coherence. [Regression Actions run](https://github.com/mifurm/FoundryWorkshopDEMOS/actions/runs/40281830195) failed at the intended gate with 0% correctness/100% coherence. Both used real OIDC login, environment approval, and uploaded verified cloud reports. |
| [24 - Agent lifecycle](./24-agent-lifecycle/README.md) | Passed | Published the actual application, verified six endpoint responses across versions 1 -> 2 -> 1, confirmed three successful Activity Log writes and final ARM version-1 readback. |
| [25 - Resilience and cost](./25-resilience-cost/README.md) | Passed | Two real model calls returned 30 input/37 output tokens after local injected faults. Observed model budget 5/6, tool budget 4/4, permanent-error/deadline stops, and one idempotent synthetic booking. |
| [26 - Multimodal](./26-multimodal/README.md) | Passed | Both synthetic images matched every expected truth field, including unreadable/missing values. |

### Defects fixed and verified

- Serialized affected model/project ARM writes after real `RequestConflict` failures.
- Accepted lab 20's observed complete tool call with finish reason `stop`, while
  retaining exact tool, argument, refusal, and truncation validation.
- Reported lab 22's real `content_filter` block explicitly instead of crashing or
  fabricating a baseline answer; unrelated API errors still fail.
- Corrected tracing exporter-provider wiring and preserved `workshop.error_type`
  when the exporter discarded the reserved `error.type` attribute.
- Replaced lab 23's failing response-ID evaluation route (HTTP 404) with the
  documented inline-JSONL route over the exact generated responses and real
  `builtin.coherence`; result/count/source checks and thresholds remain strict.
- Added a Search-region override for lab 17 and exposed `--search-location` in
  the root deployment runner so the successful regional deployment is reproducible.
- Corrected live probe classification/false-success handling and benchmark pacing
  for the observed 10-RPM limit.

### Reproduce and inspect

Regression validation passed: 90 lab unit tests and 27 deployment-runner tests
(117 total). Live-fix syntax, report coverage, local documentation links, and
diff whitespace were also checked. These offline checks are separate from the
live outcomes above.

Run labs individually so a blocked prerequisite does not stop unrelated labs:

```bash
python3 deploy.py --demo 20
python3 deploy.py --demo 17 --location eastus2 --search-location westus2
```

Keep the same Search-region override when redeploying lab 17: an existing Azure
Search service cannot be moved to a different region in place.

Full commands, service errors, measurements, trace/evaluation IDs, and version
readbacks are retained locally in ignored evidence files:

- Labs 14/15/16/26: `14-foundry-fundamentals/.azure/live-validation-summary.json`.
- Labs 17/19/21: each lab's `.azure/live-result.json`.
- Labs 18/23/24: each lab's `.azure/live-testing-summary.json`.
- Lab 20: `20-human-approval/.azure/live-approval-paths.log`.
- Lab 22: `22-prompt-injection/.azure/live-test-after-fix.log`.
- Lab 25: `25-resilience-cost/.azure/demo.log`.

### Lab 23 GitHub configuration and verification on 2026-10-26

- Installed `.github/workflows/foundry-evaluation.yml` and kept the lab's example
  identical, enforced by an eighth unit test.
- Created dedicated user-assigned managed identity `foundry-evaluation-github`
  in the existing lab resource group, with only Foundry User access to the lab's
  Foundry account. No subscription Contributor/Owner or stored Azure secret.
- Configured environment `foundry-evaluation`: required reviewer `mifurm`,
  administrator bypass disabled, allowed `main` and `refs/pull/*/merge` refs,
  and all five non-secret environment variables. Fork PRs remain excluded.
  Self-review is allowed for this single-maintainer workshop.
- Verified GitHub uses immutable subjects. Initial run `40281468905` failed
  `AADSTS700213` because a name-only example did not match the actual claim.
  Federation now exactly matches
  `repo:mifurm@16135464/FoundryWorkshopDEMOS@1433238170:environment:foundry-evaluation`;
  immutable subject protection was not disabled.
- Approved both reviewed main-branch verification runs through the required
  environment gate. Azure login, eight unit tests, and artifact upload succeeded
  in both. Downloaded reports confirmed three actual responses per run and
  completed cloud evaluation, with unchanged 100% release thresholds.
- Baseline cloud evaluation: `eval_375e42d5feb2486bb9e439f61013a8bc` /
  `evalrun_1443c330f96b4289b8ea467b90e7235c`.
- Regression cloud evaluation: `eval_e645335034c54fd9bfa5a1cdd8ec2a0c` /
  `evalrun_84d576532ded41abb03dd6ae7ac1ae02`. Its failure was
  `Release blocked: correctness=0.000, coherence=1.000`, not infrastructure.
- Exact run metadata and downloaded artifacts are retained locally under
  `23-evaluation-cicd/.azure/github-baseline*` and
  `23-evaluation-cicd/.azure/github-regression*`.

Repository-wide branch protection was left unchanged: requiring a path-filtered
workflow for every PR could block unrelated changes indefinitely. Environment
approval and the workflow's failing exit status are enforced, but a failed check
does not itself create a repository-wide merge restriction.

To complete the remaining live coverage:

1. Lab 21 needs a genuine second user session in an isolated Azure CLI
   configuration, supplied through `SECOND_AZURE_CONFIG_DIR`, with the documented
   tenant/Graph prerequisites. The presenter's Graph profile and membership
   requests passed on 2026-10-26; the second user needs no Azure subscription or
   Search/model role. See the lab README's second-user unblock instructions.
   A subsequent real user-creation request returned HTTP 403
   `Authorization_RequestDenied`; directory-role readback showed Global Reader.
   No user was created, no security policies were changed, and temporary password
   files were removed. An authorized Entra administrator must provision the user
   or authorize creation. Evidence:
   `21-permission-aware-retrieval/.azure/user-provisioning-result.json`.
The original 2026-10-25 pass created no tenant identities or federated credentials.
The 2026-10-26 follow-up created the dedicated lab 23 managed identity and its
environment-bound federation; no client secrets or tenant consent grants were
created.
Search and monitoring resources may continue charging while provisioned.

### Lab 14 restricted-identity follow-up (2026-10-26)

Two causes were reproduced: the presenter's inherited subscription Foundry Owner
allows inference on the deliberately unassigned account, and the real inference
service returns HTTP 401 `PermissionDenied` for missing RBAC rather than the
lab's assumed 403. An independent call to the versioned `2024-10-21` inference
API also returned 401 and explicitly named the principal's missing
`Microsoft.CognitiveServices/accounts/OpenAI/deployments/chat/completions/action`.
This was an authorization denial, not an invalid-token response.

Created a dedicated one-day certificate service principal without default roles.
Deployed the existing Bicep with `principalType=ServicePrincipal`; role readback
confirmed exactly one Cognitive Services OpenAI User assignment on the primary
account, no inherited assignments, and none on the denied account. The second
account's `tutor-mini` deployment is healthy. Presenter permissions are unchanged.

The complete demo under that same identity exited **0**: normal answer/usage,
streamed answer/usage, real HTTP 404 `DeploymentNotFound`, and real HTTP 401
`PermissionDenied`. The denial message was `Principal does not have access to
API/Operation.`; another observed response named the exact missing data action.
The corrected permission check requires 401/403 plus that specific code and an
explicit authorization message. Arbitrary authentication errors, network blocks,
and unrelated statuses remain failures; no status is rewritten as 403.

Nine focused offline tests passed, including all-or-nothing credential settings,
same-identity use on both accounts, no default-credential fallback after certificate
failure, and rejection of unrelated error bodies/statuses. Live evidence remains
ignored under `14-foundry-fundamentals/.azure/`: `live-restricted-identity.log`,
`live-validation.json`, `versioned-denial-probe.json`, and `restricted-roles.json`.
The private PEM has mode 600; Entra credential expiry is **2026-10-27 10:00:19 UTC**.
The lab README documents reuse, expiry, and separate tenant/local cleanup.