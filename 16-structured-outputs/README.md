# 16 — Prompt design and structured outputs

Extract category, priority, and a short summary from four labeled synthetic
support tickets. Compare instructions alone, few-shot examples, and real
**strict JSON Schema** using GPT-4.1-mini `2025-04-14`. The default CLI is
noninteractive and makes **14 real requests** (12 comparisons plus two probes).

## Prerequisites, deploy, run, test

Python 3.10+, Azure CLI/Bicep, `az login`, model access/quota. Deployment requires
resource-creation and role-assignment privileges, such as Contributor plus User
Access Administrator. Inference uses `DefaultAzureCredential` and **Cognitive
Services OpenAI User**, never API keys or an interactive browser.
For an application identity, use its **service principal object ID**, not the
application registration object ID/client ID, and `principalType=ServicePrincipal`.
Configure `DefaultAzureCredential` for that same identity separately.
Allow several minutes for RBAC propagation after deployment.

Run from this directory:

```bash
az login
az account set --subscription YOUR_SUBSCRIPTION_ID
az cognitiveservices model list --location eastus2 -o table
az cognitiveservices usage list --location eastus2 -o table
# Verify gpt-4.1-mini, version 2025-04-14, GlobalStandard, and sufficient quota.
RG=foundry-lab16
LOCATION=eastus2
PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv)
az group create --name "$RG" --location "$LOCATION" --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --name lab16 -g "$RG" --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location="$LOCATION" \
  --query properties.outputs -o json > deployment.json
export AZURE_OPENAI_ENDPOINT=$(az deployment group show -g "$RG" -n lab16 --query properties.outputs.endpoint.value -o tsv)
export AZURE_OPENAI_DEPLOYMENT=$(az deployment group show -g "$RG" -n lab16 --query properties.outputs.deployment.value -o tsv)
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py --offline-errors
python demo.py
python -m unittest discover -s . -p 'test_demo.py' -v
az bicep build --file infra/main.bicep --stdout >/dev/null
```

No Azure provisioning or inference is performed by the offline tests.
Model availability varies by subscription/region; do not deploy an arbitrary
replacement model without verifying its strict-output support. HTTP 403 usually
requires role/propagation checks; HTTP 400 may indicate unsupported schema/model;
429 indicates limits. Results classified as `request_error`, including either
probe, cause exit 1. The recognized HTTP 400 `content_filter` refusal-attempt
observation is recorded separately and does not by itself fail the run.
Requests pause 6.1 seconds between calls by default:
the live default capacity-10 deployment reported 10 requests per 60 seconds.
Inspect deployment `properties.rateLimits` rather than assuming a model's RPM/TPM
ratio; use `--request-interval SECONDS` to tune pacing.
This pause is outside inference and does not retry or hide failed requests.

## Schema and independent labels

The schema in `demo.py` uses only the supported subset: a root object, required
properties, nullable strings, enums, and `additionalProperties: false`.
All three fields are required; **missing information is represented by null**,
not omitted or invented. Priorities follow an explicit rule: multiple users
currently blocked -> high; explicitly one affected/nobody blocked -> low;
unstated impact -> null. Historical resolved incidents do not control classification.

For reproducible **summary field** scoring this tiny task specifies canonical
short summaries: `Billing problem`, `Access problem`, `Service unavailable`,
or null. This is intentionally simpler than subjective natural-language summary
evaluation. The summary schema is not an enum: schema-valid wrong decisions and
incorrect summaries remain possible.

Instructions and few-shot modes request JSON in text only. Structured mode passes
`response_format={"type":"json_schema", ... "strict":true}` to the service.
All outputs are independently validated by `jsonschema`, then compared **field by
field** with labels. A valid object with an invented priority still fails accuracy.
Few-shot adds examples, so token cost is not held constant. The rules and test
tickets remain the same across modes.

## Missing data, refusal, and incomplete output

* `missing-impact`: priority is null.
* `missing-everything`: category, priority, summary are all explicitly null.
* Every response checks `message.refusal` **before** JSON parsing.
* Non-`stop` finish reasons (`length`, `content_filter`, etc.) are marked
  incomplete even if their text happens to look like valid JSON.
* The live incomplete probe requests only one output token; observe the actual
  response/finish reason instead of labeling a request error as truncation.
* The live refusal probe requests secret information. A service refusal is
  **not guaranteed**: the model can instead return a safe schema-valid null
  object, or Azure can block the prompt with HTTP 400 `content_filter` /
  `ResponsibleAIPolicyViolation`. A blocked prompt is reported as
  `status: content_filter`, with its actual HTTP status and service error; it is
  neither a model refusal nor valid extracted data. The CLI says when no API
  refusal occurred; it does not falsify one.
* Explicit offline refusal, `length`, and `content_filter` fixtures in
  `python demo.py --offline-errors` and `test_demo.py` make handling these branches repeatable, without presenting
  mocked data as live cloud output. Refusals are not ordinary null-valued records.

The CLI prints raw output, service-reported usage, schema compliance, per-field
matches, and per-mode accuracy. A successful run need not achieve perfect accuracy.
Strict schema is not a correctness or safety guarantee; four examples prove
neither production readiness nor that one prompting method always wins.

## Costs, output contract, cleanup

Inputs: `principalId`, `location`; outputs: `endpoint`, `deployment`
(`ticket-extractor`), directly compatible with the root runner.
GlobalStandard pay-as-you-go inference charges input/output tokens; examples
increase prompt tokens. Failed calls are not assigned invented usage or prices.
Check [current prices](https://azure.microsoft.com/pricing/details/cognitive-services/openai-service/)
before running repeatedly; service usage is printed, not a binding cost quote.
Deployments consume quota but this lab does not provision throughput billing.
Public networking is enabled; GlobalStandard may process outside the account
region. Use only synthetic tickets, never confidential support content.

```bash
az group delete --name "$RG" --yes --no-wait
```

## Official sources

* [Structured outputs: supported models/schema, refusal and finish handling](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/structured-outputs)
* [Keyless authentication and v1 inference](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/switching-endpoints)
