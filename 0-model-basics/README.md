# 0 - Model basics: temperature, top_p, chat, batch and reasoning

Start here before the routing and agent labs. Use a local probability exercise,
then real Microsoft Foundry model calls to understand sampling and execution
choices. Authentication uses **Microsoft Entra ID, never an API key**.

## Learning goals

- Explain what `temperature` and `top_p` change, and vary one at a time.
- Distinguish a typical chat model from a reasoning model without assuming that
  either always gives better answers.
- Submit and resume a native asynchronous Batch API job, correlating individual
  results by `custom_id` rather than their order in the output file.

## The two sampling parameters

Models assign probabilities to possible **next tokens**, not whole answers.
Sampling repeats token by token.

| Parameter | Meaning | Try in this lab |
| --- | --- | --- |
| `temperature` | Reshapes the probability distribution: lower values favor already-likely tokens; higher values flatten it and allow more variation. Conceptually, `p_i(T) = p_i^(1/T) / sum(p_j^(1/T))`. Zero is the greedy limit. | 0, 0.7, 1.4 with `top_p=1` |
| `top_p` | Nucleus sampling: retain the smallest set of highest-probability tokens whose cumulative probability reaches the threshold, renormalize, then sample. It is **not** a percentage of the vocabulary or an accuracy score. | 0.2, 0.8, 1 with `temperature=1` |

For illustrative probabilities A=0.50, B=0.30, C=0.15, D=0.05, `top_p=0.8`
retains A and B, including the token that reaches the boundary. These are
**synthetic teaching probabilities**, not model log probabilities. The local
exercise prints exactly how temperature changes them.

GPT-4.1-mini accepts temperature from 0 to 2 and top_p from 0 to 1.
The local nucleus exercise uses `(0, 1]` to avoid a zero-mass threshold.
Change one parameter while holding the other at its neutral value of 1.
Low temperature does not guarantee deterministic output, correct facts or
safety. Higher temperature does not add knowledge. Identical short names can
appear at every setting; three repeats illustrate variability, not statistical
significance. Prompt, model version and service behavior also affect results.

## Chat model, batch processing and reasoning model

These are **two axes**, not three mutually exclusive model families:

| Choice | This lab | When useful | Important trade-off |
| --- | --- | --- | --- |
| Typical chat model, online | GPT-4.1-mini, `GlobalStandard`, Chat Completions | Interactive assistants, writing, extraction and simple questions | Responds to each request directly; sampling controls supported |
| Batch processing | Same GPT-4.1-mini version, separate `GlobalBatch` deployment | Offline classification, evaluation or processing many independent records | JSONL upload -> job -> later download; separate enqueued-token quota, 24-hour target turnaround, no interactive streaming |
| Reasoning model, online | o4-mini, `GlobalStandard`, Chat Completions | Planning, math, coding and constraint-heavy tasks | Allocates internal reasoning tokens; potentially more latency and billed output tokens |

Batch is not a Python loop, concurrent online calls, fine-tuning or a model
that "thinks about the entire file." Each row is an independent request; rows
do not share conversation state. Supported reasoning models can also use
batch deployments, although this lab batches only the ordinary chat model.
Microsoft documents Global Batch at 50% lower token cost than Global Standard;
check current pricing. This three-record example does not measure savings or
throughput, and the 24-hour target is not an immediate completion guarantee.
Global deployment processing can occur outside the resource's region; check
data-residency requirements before using real data.

**o4-mini-specific request differences:** do not send `temperature`, `top_p` or
`max_tokens`. Send `reasoning_effort=low|medium|high` and
`max_completion_tokens` instead. The 4,096-token limit covers hidden reasoning
**and** visible answer tokens; reaching it fails this demonstration rather
than presenting a truncated/empty answer as success. Usage can include
`completion_tokens_details.reasoning_tokens`; these are billed output tokens.
An answer's explanation is not a transcript of hidden reasoning. Other model
families have different parameter support: do not generalize o4-mini's
restrictions to every new reasoning model.

## Prerequisites and availability

The local concepts section only needs Python 3.10+. Cloud sections additionally
need Azure CLI/Bicep, `az login`, a subscription, and availability/quota for
GPT-4.1-mini `2025-04-14` in **GlobalStandard and GlobalBatch**, plus o4-mini
`2025-04-16` in GlobalStandard. Default region: `eastus2`.

Check your subscription before deployment:

```bash
az cognitiveservices model list --location eastus2 -o table
az cognitiveservices usage list --location eastus2 -o table
```

The deployer needs resource creation and role-assignment rights (for example,
Contributor plus User Access Administrator). The inference identity receives
**Cognitive Services OpenAI User** scoped to the new account, including its
batch/file operations. Management Contributor alone is not inference access.
The CLI signed-in user is used by the commands below; other identities must
pass their object ID and matching `principalType` to Bicep. Allow new RBAC
assignments several minutes to propagate.

The template creates one account and three deployments, not an agent runtime.
It serializes deployment writes to avoid account-child write conflicts.
Capacity defaults are teaching allocations, not a promise of quota or a
cost limit; GlobalBatch uses enqueued-token capacity rather than online TPM.

## Deploy, run, test

No-cloud walkthrough, from this directory:

```bash
python3 demo.py --section concepts
```

Cloud deployment and environment setup, also from this directory:

```bash
az login
az account set --subscription YOUR_SUBSCRIPTION_ID
RG=foundry-lab0
LOCATION=eastus2
PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv)
az group create --name "$RG" --location "$LOCATION" --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
mkdir -p .azure
az deployment group create --name lab0 --resource-group "$RG" \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location="$LOCATION" \
  --query properties.outputs -o json > .azure/deployment-outputs.json
export AZURE_OPENAI_ENDPOINT=$(az deployment group show -g "$RG" -n lab0 --query properties.outputs.endpoint.value -o tsv)
export AZURE_OPENAI_DEPLOYMENT=$(az deployment group show -g "$RG" -n lab0 --query properties.outputs.deployment.value -o tsv)
export AZURE_OPENAI_REASONING_DEPLOYMENT=$(az deployment group show -g "$RG" -n lab0 --query properties.outputs.reasoningDeployment.value -o tsv)
export AZURE_OPENAI_BATCH_DEPLOYMENT=$(az deployment group show -g "$RG" -n lab0 --query properties.outputs.batchDeployment.value -o tsv)
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
python -m unittest discover -s . -p 'test_demo.py' -v
az bicep build --file infra/main.bicep --stdout >/dev/null
python demo.py --section sampling --samples 3
python demo.py --section compare --reasoning-effort medium
python demo.py --section batch --wait-seconds 120
```

`python demo.py` runs all sections: 18 sampling requests, two comparison
requests and one three-request native batch. All cloud operations are paid;
the code does not fabricate service responses or silently skip failures.
Equivalent model flags are `--deployment`, `--reasoning-deployment` and
`--batch-deployment`; these are deployment aliases, not catalog model names.
`DefaultAzureCredential` disables interactive browser fallback; check which
CLI/managed identity is active.

From the repository root, the shared runner discovers number **0**:

```bash
python3 deploy.py --subscription YOUR_SUBSCRIPTION_ID --demo 0
```

It deploys to `rg-foundry-model-basics-demo`, maps all three deployment outputs
and runs every section. The default batch polling limit is two minutes.
If the job is still pending, the process exits nonzero and the runner correctly
does **not** report a complete live pass. Resume from this directory using the
saved endpoint/deployment exports (or values in the root runner's ignored
deployment record):

```bash
python demo.py --section batch --wait-seconds 120
python demo.py --section batch --batch-id YOUR_EXISTING_BATCH_ID --wait-seconds 0
```

The first form reads `.azure/batch-state.json` and never submits a second job
while that state exists, even if the job is completed, failed or expired.
The explicit ID form also does not submit; keep supplying it on future runs
if it is not already the saved ID. Only use IDs from this lab's three-record
workload. A saved endpoint/deployment mismatch fails explicitly.

The input file ID is printed as soon as upload finishes; the job/input IDs are
saved immediately after batch creation. If creation fails after upload, use the
printed file ID to remove the orphan through Foundry file management. Do not
blindly retry an ambiguous submission/network failure: check the portal's batch
list first. A crash between service creation and local persistence cannot
guarantee exactly-once submission.

To intentionally submit a fresh paid batch, pass a fresh `--state-dir`, for
example `--state-dir .azure/second-run`; do not delete old state until cleanup.
Pending, failed, expired, cancelled, incomplete and per-record error results
fail visibly. Completed output is saved in `.azure/batch-output.jsonl`;
per-record error files, when returned, go to `.azure/batch-errors.jsonl`.
Output validation requires each of the three IDs exactly once, HTTP 200,
nonempty text and a `stop` finish reason. Output order can differ from input.

## Presenter walkthrough and exercises

1. Run concepts without Azure. Predict the retained nucleus at 0.5 and 0.8.
2. Use the same workshop-name prompt at every sampling setting. Compare actual
   answers and distinct-answer counts; do not promise more variation every run.
3. Run the same scheduling question on both model deployments. The reference
   answer is **11 minutes**: A 0-4, B 4-7 and C 4-9, D 9-11.
   Print exact-answer agreement, observed latency, finish reason, serving model
   and actual usage. Both can be correct; one question is not a benchmark.
4. Try low versus high reasoning effort and compare usage/latency across
   repeated runs. Do not request hidden chain-of-thought.
5. Submit the batch before a break. Show its ID/status now, then resume later,
   inspect each real response and explain why file order is not correlation.
6. Choose an approach for live customer chat, overnight tagging and a difficult
   scheduling problem. Explain why reasoning and batching can be combined.

Offline tests use explicitly synthetic SDK fixtures. They check probability
math, parameter isolation, unsupported reasoning parameters, JSONL shape,
keyless/environment wiring, persisted resumption and strict result/failure
handling. They are not evidence of current Azure access or model quality.
See the [verification report](../tests.md) for recorded evidence.

## Cleanup

First inspect the saved batch ID in Foundry's batch management UI and cancel a
still-running job if no longer needed. Cancellation can take time and already
processed requests can be billed. Delete uploaded input/output/error files
through file management if retaining the account.

For this standalone resource group:

```bash
az group delete --name foundry-lab0 --yes --no-wait
```

For the root runner's default resource group:

```bash
az group delete --name rg-foundry-model-basics-demo --yes --no-wait
```

Use the actual group from the saved deployment record if you changed prefixes.
Deleting the dedicated group removes the account and all three deployments;
it does not remove your local ignored state/results. Do not delete a shared
group. Local state contains identifiers and synthetic responses, never tokens
or API keys.

## References and next labs

- [Microsoft: sampling controls](https://learn.microsoft.com/en-us/azure/ai-foundry/openai/concepts/prompt-engineering#temperature-and-top_p-parameters)
- [Microsoft: reasoning model API support](https://learn.microsoft.com/en-us/azure/ai-foundry/openai/how-to/reasoning)
- [Microsoft: native Batch API, quota, pricing and availability](https://learn.microsoft.com/en-us/azure/ai-foundry/openai/how-to/batch)
- [Lab 14: project/deployment fundamentals, streaming and real error probes](../14-foundry-fundamentals/README.md)
- [Lab 1: Model Router](../1-model-router/README.md)
- [Lab 15: a broader model benchmark](../15-model-benchmark/README.md)
