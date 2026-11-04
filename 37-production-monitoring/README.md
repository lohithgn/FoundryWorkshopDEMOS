# Lab 37 - Detect, diagnose, and recover a live agent regression

> **Live verification on 2026-10-27: blocked, not a completed demo.**
> Infrastructure, both agents, all three UI scenarios and response-correlated
> traces work. However, the enabled native continuous-evaluation rules produced
> no automatic runs, and the strict live gate timed out after 20 minutes.
> A separate manual cloud-grader diagnostic worked but is explicitly excluded
> from success. Missing verdicts remain PENDING. See the [verification results](../tests.md).

## Intended live gate — currently blocked

Two persistent Foundry agents expose authenticated Responses endpoints. Both use
GPT-5.4 mini and initially answer **USD 25**. The first agent also has an immutable
bad version answering **USD 40**. The stable agent acts as a control.

**Native Foundry continuous evaluation rules** are configured to observe completed
live responses and trigger cloud evaluation automatically. They use 100% sampling,
bounded to 30 runs per hour per agent. A service-side exact-match grader tests price
correctness, not coherence or stylistic quality. The client never submits an
on-demand evaluation run or replaces a missing cloud score with a local score.

The live scenario switches the actual endpoint version, requires a cloud quality
failure only for the regressed agent, restores the good version, and requires
new passing evaluations after recovery. Every accepted verdict must match the
actual response ID and agent. Application Insights must contain real server-side
traces for both agents.

This targets the production-monitoring foundation behind **Foundry Control
Plane / Operate**. It is intentionally a small two-agent, single-project sample:
it does not claim cross-project fleet governance, Defender/Purview integration,
automatic remediation, or a full replacement for the Foundry portal.

The automatic verdict/recovery sequence has **not** passed live; the working
manual cloud-grader diagnostic is not evidence that continuous evaluation fired.

## Prerequisites

- Python 3.10+, Azure CLI/Bicep, and `az login` as a user with resource creation
  and role-assignment permissions in the selected subscription.
- `eastus2` availability for native continuous evaluation, current agent endpoints,
  and GPT-5.4 mini Global Standard quota (10 capacity units).
- Allow time for asynchronous cloud evaluation and trace ingestion. Preview
  availability and API behavior can vary by region and subscription.

The template creates a dedicated Foundry account/project, model deployment,
workspace, and Entra-authenticated Application Insights connection. It grants the
project identity Foundry User for continuous evaluation and Monitoring Metrics
Publisher for trace ingestion, plus Reader on Application Insights and Log
Analytics Reader on its workspace for service-side trace access. No Azure credentials are stored in the repository
or sent to the browser.

## Deploy and test

From this directory:

```bash
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
.venv/bin/python deploy_lab.py --prefix rg-foundry
```

Only **rg-foundry-production-monitoring-demo** is deployed. The command provisions
the infrastructure, runs unit tests, creates the persistent agents/evaluations/
rules, then exercises the complete live scenario.
The shared standalone-runner default is `rg-foundry-student`; use the explicit
prefix above to reproduce the presenter's deployment.

The standalone wrapper requires its `.azure/lab-deployment.json` ownership record
and matching `managedBy`/`lab` group tags for reuse. Do not pre-create the reference
group below or use an unrecorded group created by another deployment path.

The equivalent tagged resource-group creation command is:

```bash
az group create --name rg-foundry-production-monitoring-demo --location eastus2 --tags managedBy=foundry-lab-script lab=37-production-monitoring SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
```

To repeat without reprovisioning:

```bash
.venv/bin/python -m unittest -v
.venv/bin/python demo.py --live-test
```

For a complete pass, the six live responses must produce this matrix. It is the
required outcome, **not an observed successful run**:

| Phase | Monitored agent | Stable control |
|---|---|---|
| Baseline | USD 25 / native PASS | USD 25 / native PASS |
| Regression | USD 40 / native FAIL | USD 25 / native PASS |
| Recovery | USD 25 / native PASS | USD 25 / native PASS |

The failing regression verdict is an **expected demonstration outcome**, not a
failed evaluator service or failed lab. Evaluation infrastructure errors fail the
lab explicitly. The known-good route is restored in a `finally` block even when
regression verification fails.

## Simple operations UI

```bash
.venv/bin/python ui.py
```

Open **http://127.0.0.1:8037**:

1. Select **Healthy baseline** to invoke both real deployed endpoints.
2. **Refresh cloud evaluations** until the response-linked verdicts arrive.
   Missing evaluations remain **PENDING**, never PASS.
3. Select **Introduce price regression**, refresh, and observe only the monitored
   agent fail its native price check.
4. Select **Restore good version**, refresh, and observe recovery.
5. Query actual ingested traces and inspect native evaluation run/report IDs.

Each phase generates two paid model responses and up to two evaluation runs.
Avoid repeatedly clicking while waiting; reaching the hourly rule limit prevents
further evaluations until capacity resets. Stopping the UI normally restores the
good routes; forcibly terminating its process cannot guarantee cleanup. The CLI
live test always attempts rollback on errors.

The UI is a local presenter controller, not an internet-facing app. It binds to
loopback, checks host/origin, keeps Azure credentials server-side, and serializes
scenario changes.

## Foundry portal walkthrough

In [Foundry](https://ai.azure.com), select project `production-monitoring`:

- Inspect both agents and their active endpoint version.
- Open each agent's **Monitor** view and continuous evaluation configuration.
- Inspect the live evaluation results and drill into traces.
- Use **Operate** to explore the available agent inventory/monitoring experience.

The local UI shows the same cloud evaluation evidence through APIs; it does not
fabricate portal screenshots, invent alerts, or assert that Azure budget alerts
are hard caps.

## Evidence, cost, and cleanup

Ignored `.azure/agents.json` records actual version, evaluation and rule IDs.
`.azure/live-report.json` retains the six responses, their matching cloud verdicts,
report URLs, and queried Azure Monitor telemetry. `passed` becomes true only when
all phases and telemetry checks pass. Evaluation waits are bounded to 20 minutes
per phase; trace ingestion waits at most 15 minutes.

Model inference, evaluation, and Azure Monitor ingestion/retention are chargeable
according to the selected services' pricing. Enabled rules continue evaluating
new agent traffic; the hourly limit is not a subscription spending cap.
Agents, endpoints and rules remain deployed for presentation.

Stop the UI, then remove **only this lab's** resources when finished:

```bash
az group delete --name rg-foundry-production-monitoring-demo --yes --no-wait
```

References:
[Control Plane](https://learn.microsoft.com/azure/foundry/control-plane/overview),
[native continuous evaluation](https://learn.microsoft.com/azure/foundry/observability/how-to/how-to-monitor-agents-dashboard),
[server-side tracing](https://learn.microsoft.com/azure/foundry/observability/how-to/trace-agent-setup),
[Entra-authenticated trace ingestion](https://learn.microsoft.com/azure/foundry/observability/how-to/trace-ingestion-entra-authentication).
