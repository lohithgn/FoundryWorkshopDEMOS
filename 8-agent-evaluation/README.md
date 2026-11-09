# Microsoft Foundry - Agent evaluation

A small real **Foundry cloud evaluation**: create a temporary workshop tutor agent,
ask two questions, retain its actual answers and stored response IDs, and evaluate
the question/answer pairs using `builtin.coherence`. Print run metadata, result counts, and per-item
scores. This is not a local string comparison presented as a Foundry evaluation.

## Deploy

Requires Python 3.10+, Azure CLI with Bicep, resource creation and role-assignment
permissions, and Foundry Agent Service/cloud evaluation support and model quota
in the selected region. Cloud response evaluation uses preview Foundry APIs.
The template creates a Foundry account, project, `gpt-4.1-mini` deployment
(`2025-04-14`, Global Standard), presenter RBAC, and project identity RBAC
for evaluator inference. API keys are disabled; endpoints are public.

Run from this folder:

```bash
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
RESOURCE_GROUP="rg-foundry-agent-evaluation-demo"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group "$RESOURCE_GROUP" --name demo \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location=eastus2
export FOUNDRY_PROJECT_ENDPOINT="$(az deployment group show -g "$RESOURCE_GROUP" \
  -n demo --query properties.outputs.projectEndpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show -g "$RESOURCE_GROUP" \
  -n demo --query properties.outputs.deployment.value -o tsv)"
```

## Run and present

```bash
python3 -m venv .venv
source .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py
```

1. Show the agent's two answers and their stored response IDs.
2. Explain that the script sends the exact generated question/answer pairs as
   inline JSONL to Foundry, retaining response IDs for traceability. Foundry uses
   the deployed model as a coherence judge; this is not a local score.
3. Compare the per-item results and open the reported run/report URL when present.

The first question supplies product context to avoid an ambiguous product name.
The second specifies supervised training with labeled data rather than implying
that all training uses labels. These prompts are not factuality checks of the
generated answers.

The script polls for up to ten minutes. A failed run, errored item, incomplete
counts, mismatched source rows, missing grader results, or timeout exits nonzero.
The script checks that output items are `completed` and contain actual completed
coherence results; their pass/fail values are separate from processing status.
A completed low-scoring evaluation is a valid
result, not a transport failure. The temporary agent version is deleted even
when evaluation fails; stored responses/evaluation runs remain for inspection.
Timed-out cloud runs may continue and incur charges.

### Why response-ID evaluation previously returned HTTP 404

The original script created responses through an **agent-scoped client**, then
asked the **project-scoped cloud evaluator** to retrieve those IDs using
`azure_ai_responses` / `response_retrieval`. Live testing on 2026-10-26 showed
the same newly stored response was retrievable through the agent client
(HTTP 200, identical text), but not through the project client (HTTP 404).
`store=True` did not make the response available in that other scope.

The fix uses the documented custom-dataset route: send the exact `query` and
`response`, alongside `response_id`, using inline `jsonl` / `file_content`,
with explicit evaluator field mappings. IDs are correlation metadata rather
than lookup instructions. This still runs the real cloud `builtin.coherence`
evaluator; it does not substitute generated answers or local scoring.

### Live verification on 2026-10-26

- Deployed to `rg-foundry-agent-evaluation-demo` in East US 2.
- Reproduced the old response-ID failure in cloud run
  `evalrun_1a2899c0709440e8ab01b6f241ec9ec4`.
- Fixed run `evalrun_a87a335f1e5943f79ea9dbaa1522db58` completed with two actual
  output items: **2 passed, 0 failed, 0 errored**, coherence **5/5** each.
- Independently read the run/items back from Azure and checked both response IDs.
- Confirmed temporary agent deletion with a separate HTTP 404 readback.
- Serialized project/model deployment after an actual ARM `RequestConflict`.
- Eight regression tests and Bicep lint passed.

Evidence is retained locally in ignored `.azure/live-before-fix.log`,
`.azure/response-scope-probe.json`, `.azure/live-after-fix.log`, and
`.azure/live-testing-summary.json`. Resources remain deployed.
Coherence is not factual correctness: a coherent explanation can still contain
incorrect product claims. This small lab does not verify groundedness or accuracy.

## Validate, costs, and cleanup

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
python -m unittest discover -s . -p 'test_*.py' -v
az group delete --name "$RESOURCE_GROUP" --yes
```

Generation and judge requests are billed. Both project and presenter RBAC may
need time to propagate after deployment. Use **projectEndpoint**, not the
account inference URL. A coherence score on two benign questions is neither a
safety certification nor a comprehensive agent-quality benchmark.

- [Evaluate precomputed datasets, including inline data](https://learn.microsoft.com/azure/foundry/observability/how-to/cloud-evaluation-datasets)
- [Feature F008](../BACKLOG.md#f008---microsoft-foundry-agent-evaluation)
