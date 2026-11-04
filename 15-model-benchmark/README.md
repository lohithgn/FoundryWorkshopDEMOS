# 15 — Repeatable model selection

Run the **same six labeled synthetic support tickets** against GPT-4.1-mini,
GPT-4.1, and actual Azure **Model Router**, three times each. There is no local
router stand-in and no fabricated live success. Default run: **54 requests**.

## Prerequisites and deployment

Python 3.10+, Azure CLI/Bicep, `az login`, model access and sufficient quota.
The deploying identity needs resource creation and role-assignment rights
(Contributor plus User Access Administrator, or equivalent). Inference uses
`DefaultAzureCredential` and scoped **Cognitive Services OpenAI User**; no keys
and no interactive browser prompt. For applications pass
`principalType=ServicePrincipal` with the **service principal object ID**
(`az ad sp show --id "<client-id>" --query id -o tsv`), not the application
registration object ID or client ID. Ensure `DefaultAzureCredential` actually
selects that workload identity; changing the Bicep principal does not configure
local authentication.

From this directory, check availability **before** deployment:

```bash
az login
az account set --subscription YOUR_SUBSCRIPTION_ID
az cognitiveservices model list --location eastus2 -o table
az cognitiveservices usage list --location eastus2 -o table
```

Check all three model/version pairs and GlobalStandard capacity: `gpt-4.1-mini`
and `gpt-4.1`, both `2025-04-14`, plus `model-router` `2025-11-18`.
Router has its own region/access/quota restrictions; change `location` and
`routerVersion` only to an available combination. Do not silently substitute a
different model if a deployment fails.

```bash
RG=foundry-lab15
LOCATION=eastus2
PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv)
az group create --name "$RG" --location "$LOCATION" --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --name lab15 -g "$RG" --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location="$LOCATION" \
  --query properties.outputs -o json > deployment.json
export AZURE_OPENAI_ENDPOINT=$(az deployment group show -g "$RG" -n lab15 --query properties.outputs.endpoint.value -o tsv)
export AZURE_OPENAI_DEPLOYMENT=$(az deployment group show -g "$RG" -n lab15 --query properties.outputs.deployment.value -o tsv)
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py
python -m unittest discover -s . -p 'test_demo.py' -v
az bicep build --file infra/main.bicep --stdout >/dev/null
```

Tests are offline; they check labeled scoring, invalid schemas, refusal,
truncation, latency quantiles, pricing, and transport errors. Allow time for RBAC
propagation before live execution. A 403 is a role issue; a 404 can be a deployment
alias/region issue; a 429 indicates quota/rate limits.

## Task and measurements

Each ticket requires exactly `category` (billing/access/outage/null) and `priority`
(low/high/null). **Current unresolved** problems take precedence over historical
incidents. High means multiple users currently blocked; low means explicitly only
one affected or nobody blocked; unstated impact is null. Embedded instructions
are untrusted data. `CASES` in `demo.py` includes all ground truth and difficulty.

All slots receive identical rules, messages, temperature 0, and token budget.
We use instruction-based JSON rather than strict response format so every
slot, including Router, receives the same broadly supported request. This also
makes schema failures measurable rather than guaranteed away.

`results.json` preserves every item, repeat, deployment, serving `response.model`
when exposed, schema compliance, exact category+priority correctness, errors,
wall-clock latency, raw token usage, and estimated USD. Router's returned model
identifier is **not guaranteed** to identify the underlying billable model.
Errors/refusals/incomplete output count as wrong, not dropped samples.
For completed benchmark execution, the CLI exits nonzero if **all** samples have
`transport_error`; mixed failures and low accuracy can still exit 0.
Inspect the report, not only the exit code:
this is a measurement tool, not a release-quality gate.

Overall and easy/hard summaries report p50 (median), nearest-rank p95, min/max for completed HTTP calls,
tokens, and failures separately. Transport failures are excluded from success
latency but included in accuracy and missing-usage counts. There is no retry
inflation: SDK retries are disabled. Calls are sequential; deployment order
rotates each repeat. Three repetitions illustrate noise, not statistical confidence.
Use `--repeats 5` for 90 requests; at least two repetitions are required.

## Pricing assumptions and tradeoffs

Default **illustrative, not current quoted** USD/million tokens:

| Slot | Input | Output |
|---|---:|---:|
| mini | 0.40 | 1.60 |
| full | 2.00 | 8.00 |
| router | unknown | unknown |

Cost = `(prompt_tokens * input_rate + completion_tokens * output_rate) / 1e6`.
Missing usage/rates produce **null**, never zero or a fabricated router price.
Uncached input rates deliberately ignore cache discounts, taxes, negotiated
pricing, and unsuccessful-call charges. Router cost depends on selected models;
do not claim savings from an unknown estimate.

For your own explicit assumptions, save `rates.json` locally:

```json
{"mini": [0.40, 1.60], "full": [2.00, 8.00], "router": [1.00, 4.00]}
```

```bash
python demo.py --rates rates.json --repeats 3 --output results.json
```

The router numbers above are **hypothetical blended assumptions**, not published
prices. Replace them using your deployment's actual billing information and
[current Azure pricing](https://azure.microsoft.com/pricing/details/cognitive-services/openai-service/).
The fastest/cheapest slot may miss high-impact tickets; choose against an accuracy
threshold as well as latency/cost. Six synthetic tickets cannot establish
production quality, robustness, or a universal ranking. Cached prompts,
deployment load, model updates, and router decisions affect results.

## Contracts, limitations, cleanup

Inputs: `principalId`, `location`. Outputs: `endpoint`, `deployment`
(`benchmark-mini`), `alternateDeployment` (`benchmark-full`), `routerDeployment`
(`model-router`). The root runner only needs `endpoint` and `deployment`; the
other names are CLI defaults (`--full`, `--router` override them), also configurable
with `AZURE_OPENAI_ALTERNATE_DEPLOYMENT` and `AZURE_OPENAI_ROUTER_DEPLOYMENT`.
All three deployments consume quota; GlobalStandard is not a regional-processing
guarantee. Public networking is enabled. No Azure work is done by offline tests.

```bash
az group delete --name "$RG" --yes --no-wait
```

## Official sources

* [Use model router](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/model-router)
* [Model router concepts and limitations](https://learn.microsoft.com/azure/ai-foundry/openai/concepts/model-router)
* [OpenAI v1 and keyless authentication](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/switching-endpoints)
