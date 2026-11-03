# 44 — Cloud evaluator breadth and native agent optimizer

Three independently reported **native cloud** stages:

1. Generate exactly two synthetic pricing Q&A samples through `datasets.begin_create_generation_job`; read back the registered immutable dataset version. If generation changes the required security-tag keys' casing, update the same version through native `datasets.create_or_update` and require an independent GET to retain the exact mandatory tags, ID, version, and content URI.
2. Invoke the actual deployed model through native Responses with a read-only synthetic pricing function, return actual function outputs, and evaluate two resulting tool traces with cloud `builtin.groundedness`, `builtin.relevance`, and `builtin.tool_call_accuracy`. Each evaluator must return a real score on both rows.
3. Submit **comparative native agent optimization**, not one-shot prompt rewriting or deterministic local edits. The intentionally unoptimized baseline contains all real catalog facts but instructs the agent to give the unhelpful answer “Please consult the catalog.” Only the native optimizer creates candidate instructions. Pin the actual baseline prompt-agent version, generated training dataset version, separate refund-question validation set, and supported optimization model. Set `max_candidates=1`, `max_concurrent_agent_runs=1`, target only `instructions`. Require baseline and generated candidate scores linked to native cloud evaluation IDs, plus an actual service-generated instruction mutation. Independently invoke both returned configurations on the same disjoint refund question through the deployed model's native Responses API, then score both with a separate native cloud relevance evaluation.

The held-out gate approves only an improved candidate with score ≥0.8; ties/regressions are **REJECTED**. No candidate is automatically promoted. A completed native comparison with a rejection is a valid demonstration of the gate, not a claim of improved quality.

If generation is unavailable, the optimizer still attempts the documented two-row inline training alternative to collect independent native optimizer evidence; **overall status remains BLOCKED** because the requested generated dataset did not succeed. No offline fallback can produce live PASS.

## Run

```bash
python3 deploy.py --demo 44 --location eastus2
```

Manual path:

```bash
az group create --name rg-foundry-evaluation-optimizer-demo --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group rg-foundry-evaluation-optimizer-demo --template-file 44-evaluation-optimizer/infra/main.bicep --parameters location=eastus2 principalId="$(az ad signed-in-user show --query id -o tsv)"
cd 44-evaluation-optimizer
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
.venv/bin/python -m unittest -q
.venv/bin/python demo.py --preflight
.venv/bin/python demo.py
```

Root invocation needs no manual runtime exports: the root supplies existing outputs, and the script reads its own `.azure/deployment.json` `{subscription, resourceGroup, outputs}` for the optimizer deployment. Manual runs can supply the same state or:

| Output | Environment |
|---|---|
| `endpoint` | `AZURE_OPENAI_ENDPOINT` |
| `projectEndpoint` | `FOUNDRY_PROJECT_ENDPOINT` |
| `deployment` | `AZURE_OPENAI_DEPLOYMENT` |
| `projectResourceId` | `FOUNDRY_PROJECT_RESOURCE_ID` (informational) |
| `optimizationDeployment` | `FOUNDRY_OPTIMIZATION_DEPLOYMENT` |

Required ARM parameters: `location`, `principalId` (User). Optional: `accountName`, `projectName`, `deploymentName`, `optimizationDeploymentName`, `capacity` 1–10. Baseline/evaluation/data generator: `gpt-4.1-mini` `2025-04-14`; supported optimizer: `gpt-5` `2025-08-07`; both GlobalStandard capacity 10. Account-child model/project writes are serialized. Azure CLI Entra authentication, no keys or persisted tokens.

## Verification and evidence

27 offline tests pass for native payload serialization, immutable dataset reference and metadata tag repair/read-back, rejecting changed content or nonpersisted tags, explicit unoptimized baseline, reporting actual failed-candidate errors, budget/disjoint holdout, actual-model tool-call lineage, diverse evaluators and required native grounding/tool mapping, rejecting missing/nonfinite evaluator scores, score/link/mutation requirements, rejecting training-screen scores as heldout evidence, approval/rejection, and timeout cancellation. Bicep and Python syntax compiled. Pylance was unavailable; actual SDK signatures/runtime serialization and focused unittest were used.

Actual ARM deployment: **succeeded**, `rg-foundry-evaluation-optimizer-demo`, East US 2, subscription `1217c203-6c76-47b4-b61c-9bba62dc8e02`. Both model deployments and Entra project preflight succeeded. **Historical standalone verification:** native dataset generation passed for `catalog-qna-fe0799cbfa`, version `1.0`, exactly two generated samples. Native optimizer job `opt_2ea02df647694c88af7064d88b93894c` succeeded and generated a real instruction mutation. Generation used 11,320 tokens; optimizer stage reported 2,178 optimizer-model tokens plus agent/evaluator usage.

**Important native API observation:** candidate evaluation links pointed to two training-screen pricing rows, while the baseline link pointed to the refund holdout. The native service did not expose a candidate refund-holdout run for this screened candidate. Comparing those scores would be invalid. The implementation therefore requires an explicit additional native-cloud heldout evaluation of both configurations, with matching query, candidate IDs, response IDs, and independent evaluation/run IDs. It never treats training-screen scores as heldout evidence. Exact final heldout decision and live status are in `.azure/live-report.json`.

The historical explicit native heldout evaluation passed, run `evalrun_8c1be55694f24b0b98913b0c80637a64`. Both configurations returned real refund answers and scored **5/5** on the same question (normalized 1.0 each), so that heldout gate correctly **REJECTED** promotion. Historical standalone live status was PASS; it did not establish root replay success.

Native evaluator breadth: **PASS**, run `evalrun_15901f904d41452ea0ecc66e25872445`; both actual tool traces scored **5/5 for groundedness, relevance, and tool-call accuracy** (six native scores, zero errors). Overall **PASS**, exit 0. Agent-reference function-tool requests encountered connection failures; the documented alternative measures actual model Responses tool calls directly, while optimization continues to target a native prompt agent. Native validation also required mapping `tool_definitions` into groundedness when that column is present; this is implemented and regression-tested. Saved native responses were read back before evaluating, not regenerated during the mapping fix.

**Root replay failures and verified producer changes:** native generation returned `securityControl`/`securityExemption`, not the mandatory `SecurityControl`/`SecurityExemption`, even though the request contained the correct keys. The documented DatasetVersion metadata update supports adding/updating tags; native PUT plus GET successfully repaired the existing generated version without changing its ID, version, or content URI. That repair is now in the producer for every new generation.

The root optimizer job `opt_0c96c7916fed469ca3d0908dd59d340b` reported `succeeded`, but its only nonbaseline candidate actually **failed** with `candidate_screening_stalled`: “Candidate screening stopped without an accepted improvement before this candidate completed full validation.” Its baseline already scored 1.0, leaving no higher bounded score to accept. This was **BLOCKED**, not a completed comparison or a cancellation. The current baseline deliberately has instruction-quality headroom; its facts are unchanged and no candidate is fabricated locally. Native job status, termination reason, candidate summary, and actual candidate failures are persisted and included in blocker messages. A service-level `succeeded` with no completed generated candidate cannot pass the feature gate.

**Final exact root replay: PASS, exit 0.** One `python3 deploy.py --demo 44` invocation deployed infrastructure, passed 27 offline tests/preflight, and completed all native stages:

- Generated dataset `catalog-qna-c2326d0c54`, version `1.0`, job `datagen-c52f05e7284b41ffbc203ddd6df58897`. Native metadata PUT/GET preserved the dataset identity/content URI and persisted both exact mandatory tags. All three retained generated dataset versions were independently verified tagged.
- Cloud evaluator breadth run `evalrun_f9db715eca1e4cfda422229877f13832` passed all required scores.
- Native optimizer `opt_1c8e015c36e74e55b7ab0ec45d44d3ad` terminated `completed`/`succeeded`, with **one completed nonbaseline candidate**, `cand_opt_1c8e015c36e74e55b7ab0ec45d44d3ad_0001`, and one actual service-generated instruction mutation. Native comparison scores were baseline 0.25 and candidate 1.0; no candidate failed.
- The separate same-question cloud heldout run `evalrun_548f46aed7cf4164a5243f12aec8b628` scored actual returned configurations **0.4 baseline versus 1.0 candidate**: **APPROVE**, `promoted=false`. These independent heldout scores—not training-screen scores—control the gate. Ties still reject, as covered by regression tests and the historical tie run.
- The temporary optimizer agent was deleted; project listing confirmed no remaining agents. No jobs were resubmitted. Exact root transcript and native evidence are `.azure/root-replay-terminal.log` and `.azure/live-report.json`.

Any missing core stage results in `BLOCKED`, exit 1. `demo.py --stage evaluator` retries only that stage while preserving native dataset/optimizer evidence; `demo.py --stage holdout` evaluates existing native optimizer output without resubmitting optimization. SDK/Pydantic can warn when serializing Foundry's Azure-specific grader extension into OpenAI's base grader schema; the real service read-back and all six scores were verified, and report serialization suppresses only that known serialization warning.

Ignored `.azure/` contains root-compatible deployment state, ARM/live transcripts, generation job and dataset IDs, real agent response/tool-call IDs, native evaluator outputs, exact optimizer request/job/candidates, held-out decision, and cleanup evidence. No access tokens or authentication secrets are stored.

## Bounded cost and cleanup

At most one generation job (two samples), one breadth evaluation run (two rows × three evaluators), one optimizer job (one nonbaseline candidate; two training samples and one holdout), and one explicit heldout gate run (two Responses completions × one relevance evaluator) per full invocation. Model transport retries are limited to one. Every long-running job stage has a 600-second deadline and requests native cancellation on timeout. Jobs are never resubmitted automatically. The temporary optimizer agent is deleted; dataset/evaluation/job evidence and infrastructure remain deployed. There is no routine or unattended trigger.

The generated training source excludes refunds; the refund question is reserved for native `validation_set`. The full catalog belongs in baseline agent instructions so heldout measures instruction quality, not unavailable knowledge. The optimizer evaluates prompt-agent configuration entirely in cloud. Client function execution is measured by the separate actual-model tool-trace evaluation; it is not falsely claimed to execute inside prompt-agent optimization or through a persisted prompt-agent tool endpoint.

## Current official facts

- [Agent optimizer overview](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/agent-optimizer-overview): comparative cloud baseline/candidates, prompt/hosted support, and supported optimization models.
- [Optimize a prompt agent](https://learn.microsoft.com/en-us/azure/foundry/agents/quickstarts/quickstart-optimize-prompt-agent).
- [Current official SDK optimization sample](https://github.com/Azure/azure-sdk-for-python/blob/main/sdk/ai/azure-ai-projects/samples/agents/optimization/sample_optimization_job_basic.py): native comparative API.
- [Native generated Q&A dataset sample](https://github.com/Azure/azure-sdk-for-python/blob/main/sdk/ai/azure-ai-projects/samples/datasets/sample_dataset_generation_job_simpleqna_with_prompt_source.py).
- [Dataset version create/update API](https://learn.microsoft.com/en-us/python/api/azure-ai-projects/azure.ai.projects.operations.datasetsoperations?view=azure-python-preview) and [mutable tags](https://learn.microsoft.com/en-us/python/api/azure-ai-projects/azure.ai.projects.models.datasetversion?view=azure-python-preview).
- [Cloud builtin evaluator example](https://github.com/Azure/azure-sdk-for-python/blob/main/sdk/ai/azure-ai-projects/samples/evaluations/sample_evaluations_builtin_with_inline_data.py).
- [Native tool-call accuracy evaluator](https://github.com/Azure/azure-sdk-for-python/blob/main/sdk/ai/azure-ai-projects/samples/evaluations/agentic_evaluators/sample_tool_call_accuracy.py).
- [Native model Responses and Entra authentication](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses).

Verified `azure-ai-projects==2.9.0` exposes the comparative prompt-agent API, unlike older SDKs or portal-only documentation. No retired Foundry workflow is created.
