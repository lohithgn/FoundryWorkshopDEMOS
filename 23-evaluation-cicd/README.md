# 23 · Evaluation-driven CI/CD

[Feature backlog](../BACKLOG.md#f023---microsoft-foundry-evaluation-driven-cicd)

Versioned synthetic support facts + a JSON-answer prompt + a real Foundry cloud
`builtin.coherence` evaluation. A deterministic exact-answer gate catches factual
regressions a polished/coherent response can hide. Default thresholds are **100%
exact correctness AND 100% coherence pass rate**. This is a release gate, not a
claim of statistical coverage or a production deployment pipeline.
Both prompts explicitly request raw JSON without Markdown fences; the gate still
rejects fenced or malformed responses rather than silently normalizing them.

## .NET 10 file-based runner

`demo.cs` is the primary standalone runner. It uses Azure CLI authentication and
Foundry REST APIs for agent versions, Responses, and cloud evaluations; no NuGet
libraries are required. The retained Python files and `requirements.txt` are transitional
and unchanged. The scoped `workflow.yml` sample now uses the .NET runner. The shared
`.github/workflows/foundry-evaluation.yml` remains Python and is owned by issue #1; it is
intentionally not changed here.

From this directory:

```bash
mkdir -p .dotnet-home
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet restore demo.cs --configfile NuGet.Config
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet build demo.cs --no-restore
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet restore test_demo.cs --configfile NuGet.Config
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet build test_demo.cs --no-restore
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file demo.cs --no-build --no-restore -- --help
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file demo.cs --no-build --no-restore -- --prompt baseline
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file demo.cs --no-build --no-restore -- --prompt regression
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file test_demo.cs --no-build --no-restore
```

The two evaluation commands make billable live Foundry calls. The test harness is
offline-only and never authenticates or creates cloud resources.

## Deploy / run

Azure CLI with Bicep, `az login`, quota for `gpt-4.1-mini` in the deployment region,
resource/role assignment privileges, and Foundry cloud evaluation availability
are required for the C# runner. Python 3.10+ and `requirements.txt` are retained only
for the transitional Python path below. Commands run from this directory:

The template creates the model deployment before the project to avoid concurrent
account writes failing with ARM `RequestConflict`.

```bash
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
RG=lab23-evaluation
az group create -n "$RG" -l eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create -g "$RG" -n lab23 -f infra/main.bicep \
  -p principalId="$(az ad signed-in-user show --query id -o tsv)"
export FOUNDRY_PROJECT_ENDPOINT="$(az deployment group show -g "$RG" -n lab23 --query properties.outputs.projectEndpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show -g "$RG" -n lab23 --query properties.outputs.deployment.value -o tsv)"
mkdir -p .dotnet-home
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet restore demo.cs --configfile NuGet.Config
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet build demo.cs --no-restore
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file demo.cs --no-build --no-restore -- --prompt baseline
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file demo.cs --no-build --no-restore -- --prompt regression
```

For runner-created infrastructure, set `RG` to its actual recorded resource
group (normally `rg-foundry-evaluation-cicd-demo`) instead. Keep that value for
OIDC provisioning and cleanup below. The standalone commands above create
`lab23-evaluation`; the historical GitHub setup used the runner's group.

The second command should exit **1**: the friendlier prompt returns 30-day refunds,
7-day delivery and a different support address instead of the versioned dataset's
14/3/help values. Actual generated responses determine the result; no scores are
fabricated. If the model ignores instructions, the per-item report exposes that.
Coherence is a model judge and can vary; it is not a factual correctness metric.

Both responses and evaluations are real stored Foundry artifacts. The demo creates
a temporary versioned prompt agent, stores three responses, submits their actual
query/response text and response IDs as a `jsonl` / `file_content` dataset to the
cloud `builtin.coherence` evaluator, then deletes its agent version.
Explicit column mappings bind the judge to the exact generated text.
Agent-endpoint response IDs are not currently retrievable from the project-level
`azure_ai_responses` evaluation source: live runs returned `404 Response ... not
found`. Inline precomputed responses use the documented query-response dataset
API without substituting responses or scores. It polls with a timeout, rejects failed/cancelled,
errored, missing or incomplete results, and returns nonzero. It never converts an
infrastructure failure into a passing score.
Cloud output items report execution status `completed`; pass/fail is in each
item's coherence result. The demo verifies that every completed item has the exact
submitted query/response/ID, a completed coherence result with a boolean `passed`,
and per-item totals matching the aggregate counts. `completed` alone cannot pass.

`evaluation-report.json` records dataset and prompt contents/hashes, commit,
timestamps, real deployment **model name/version metadata read from Foundry**
(also the judge deployment), agent/version, evaluator configuration, thresholds,
response IDs, per-item exact answers, evaluation/run IDs, cloud result items and
run status/errors, plus the exact cloud evaluation input dataset.
A cloud exception still writes a failed report. Only synthetic
content belongs in this report. The cloud judge uses the deployment's supported
default coherence grading threshold; the release threshold gates its pass rate.
`--judge-deployment` can select a separate supported judge deployment.

## GitHub Actions and secretless Azure access

The installed
[.github/workflows/foundry-evaluation.yml](../.github/workflows/foundry-evaluation.yml)
remains the legacy Python workflow, owned by issue #1. The lab's [workflow.yml](./workflow.yml)
is the updated .NET 10 sample: it retains the same fork guard, protected environment,
OIDC permissions, triggers, strict thresholds, and always-uploaded report. The C# offline
harness checks those contracts. The files intentionally differ until issue #1 migrates
the shared workflow; do not treat the installed workflow as running this C# app.

The repository environment `foundry-evaluation` was configured on 2026-10-26:

- Required reviewer: `mifurm`; administrator bypass is disabled.
- Allowed branch refs: `main` and `refs/pull/*/merge`. The workflow separately
  skips fork PRs, so only same-repository PRs can reach environment approval.
- Self-review is allowed for this single-maintainer workshop, so its owner can
  approve their own manual test runs. Use independent reviewers and prevent
  self-review for production separation of duties.
- Five **environment variables**, not secrets: `AZURE_CLIENT_ID`,
  `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `FOUNDRY_PROJECT_ENDPOINT`,
  and `AZURE_OPENAI_DEPLOYMENT`.

A dedicated user-assigned managed identity, `foundry-evaluation-github`, lives
in `rg-foundry-evaluation-cicd-demo`. Its only Azure role assignment is
**Foundry User** (role ID `53ca6127-db72-4b80-b1b0-d745d6d5456d`, previously named
Azure AI User), scoped to this lab's Foundry account. It has no subscription
Contributor or Owner permissions. The existing project managed identity retains
its evaluator inference role.

The federated credential trusts only:

```text
issuer:   https://token.actions.githubusercontent.com
audience: api://AzureADTokenExchange
subject:  repo:mifurm@16135464/FoundryWorkshopDEMOS@1433238170:environment:foundry-evaluation
```

This repository enables GitHub's immutable subject format. A name-only subject
fails Azure login with `AADSTS700213`; do not disable immutable subjects to make
an older example work. Inspect `gh api repos/OWNER/REPO/actions/oidc/customization/sub`
for the actual `sub_claim_prefix`, and append the exact environment context.
Custom subject templates may differ; the credential must match the actual
issued token's subject, issuer, and audience.

Azure Login uses `id-token: write` OIDC; DefaultAzureCredential then uses the
short-lived Azure CLI session. No Azure client secret or stored credential is
created. Identity provisioning is separate from model/project provisioning:

```bash
# From the repository root, after deploying this lab; keep RG from setup:
az deployment group create \
  -g "$RG" -n github-oidc \
  -f 23-evaluation-cicd/infra/github-oidc.bicep \
  -p accountName="<existing-Foundry-account-name>" \
     githubSubject="repo:mifurm@16135464/FoundryWorkshopDEMOS@1433238170:environment:foundry-evaluation" \
  --query properties.outputs -o json
```

This deploys only the identity, environment-bound federation, and account-scoped
role; it does not redeploy the model/project or remove the presenter's access.
It requires Managed Identity resource creation and role-assignment permissions,
not permission to create an Entra app registration. The returned client, tenant,
and subscription IDs populate the first three GitHub environment variables.
Use this lab's `projectEndpoint` and `deployment` outputs for the remaining two.
For another repository, supply its exact `githubSubject` and configure its
GitHub environment with the corresponding name, review rules, branch
policies, and variables. Allow Azure role/federation propagation before testing.

### Run the real GitHub gate

The following commands target the installed legacy Python workflow; they are transitional
until issue #1 updates that shared asset:

From the repository root:

```bash
gh workflow run foundry-evaluation.yml --ref main -f prompt=baseline
gh run list --workflow foundry-evaluation.yml --limit 5
# Approve the pending foundry-evaluation deployment as the required reviewer.
# After the baseline finishes:
gh workflow run foundry-evaluation.yml --ref main -f prompt=regression
```

Approve each pending deployment through its run page. Baseline should be green;
regression should be red, with a downloadable evaluation report despite failure.
For a true regression PR, replace `prompts/baseline.txt` with the checked-in
`prompts/regression.txt` content on a branch and open a same-repository PR.
The PR workflow evaluates the changed baseline file from that commit, after
environment approval.

Repository-wide branch protection is not changed automatically. A red workflow
does not prevent merging unless its check is required. This workflow is
path-filtered: making it required for every PR without handling unrelated paths
can leave unrelated PRs permanently pending. Deliberately configure an
always-reported gate before making it a repository-wide required check.

### Verified GitHub runs on 2026-10-26

Both runs used the reviewed main-branch configuration at commit `a9b4da5`,
waited for required environment approval, authenticated through the scoped
managed identity, passed eight unit tests, and uploaded their real cloud report.

| Prompt | Actions outcome | Correctness | Coherence |
| --- | --- | --- | --- |
| [Baseline](https://github.com/mifurm/FoundryWorkshopDEMOS/actions/runs/40281692169) | Success | 100% | 100% |
| [Regression](https://github.com/mifurm/FoundryWorkshopDEMOS/actions/runs/40281830195) | Expected failure at the release gate | 0% | 100% |

Downloaded artifacts were checked: three real responses per run, completed
cloud evaluation, unchanged 100% thresholds, and regression error
`Release blocked: correctness=0.000, coherence=1.000`. The red regression run
is the intended demonstration, not a broken OIDC login or evaluation service.
Initial run `40281468905` failed with the old name-only federation subject;
the successful runs use the corrected immutable-ID subject above.

Fork PRs are skipped; they are not claimed to have passed cloud evaluation.
The workflow never uses `pull_request_target`, `continue-on-error`, a stored Azure
secret, or `|| true` on the gate. Artifact upload uses `always()` and doesn't
override the failed step. Environment approval is essential for same-repo PRs too.

## Offline validation / limitations / costs

```bash
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file test_demo.cs --no-build --no-restore
az bicep build --file infra/main.bicep --stdout > /dev/null
```

The C# test harness is the current offline test command. It verifies the Foundry auth
scope, agent/Responses/evaluation REST request shapes, result matching, failure/timeout
gates, release regression behavior and failed-report artifact serialization using mocked
HTTP only. It makes no cloud calls. The unchanged transitional Python test suite is not
an active validation command: its byte-for-byte workflow parity assertion fails while
the scoped sample uses .NET and the issue #1-owned shared workflow remains Python. That
assertion is temporarily blocked pending issue #1's workflow integration. Root runner needs only standard
`endpoint`/`deployment`/`projectEndpoint` mappings.

The transitional Python path uses the Foundry `azure-ai-projects==2.7.0` evaluation API.
The C# runner uses Azure CLI OAuth and the supported Foundry REST endpoints. Cloud
evaluation availability is regional and preview behavior may change. Public
Azure endpoints and public networking only. GPT-4.1-mini `2025-04-14` uses
GlobalStandard capacity 10 in East US 2 by default; processing may leave that region.
Project operations in the Python path use `DefaultAzureCredential`; ensure it selects
the principal granted Foundry User rather than an unrelated configured
environment/workload identity. The C# path uses the current Azure CLI login. A three-item dataset is illustrative,
not a comprehensive release certification. No retry masks judge/infra errors.
Each run incurs model generation and cloud judge token/evaluation costs; no hosted
container or private endpoint is provisioned. GitHub Actions minutes/artifact
storage may also be charged. Run baseline/regression once, not on every commit
until permissions, budget and required checks are deliberately configured.

## Cleanup

Delete cloud evaluation/run and stored response artifacts in the Foundry project
if retaining the project; IDs are in the report. For the disposable lab:

```bash
az group delete -n "$RG" --yes --no-wait
```

The CI managed identity and its federation are inside whichever group received
the separate `github-oidc.bicep` deployment. Deleting only the standalone
`lab23-evaluation` group does not remove a historical identity in
`rg-foundry-evaluation-cicd-demo`. Delete only the dedicated groups you created
and intend to retire. For a retained group, revoke
the identity's account role and delete its federation/identity explicitly.
Also remove the installed workflow and GitHub environment/variables if retiring
CI. A separately created Entra application/service principal, if using that
alternative, requires separate tenant cleanup. Remove or retain the synthetic
report as appropriate.

## Official API grounding

- [Evaluate query-response datasets (inline source and explicit column mappings)](https://learn.microsoft.com/azure/foundry/observability/how-to/cloud-evaluation-datasets)
- [Evaluate stored responses (project response-store limitation applies)](https://learn.microsoft.com/azure/foundry/observability/how-to/cloud-evaluation-deployed-interactions)
- [Evaluate model and agent targets](https://learn.microsoft.com/azure/foundry/observability/how-to/cloud-evaluation-targets)
- [Azure Login with OpenID Connect](https://learn.microsoft.com/azure/developer/github/connect-from-azure-openid-connect)

The implementation follows the current lab 8 cloud-evaluation pattern, with
stricter release checks and reproducibility metadata. No Azure mutation or billable
deployment is performed during offline tests.
