# 26 — Multimodal understanding with uncertainty

Send **two generated synthetic PNG receipts** to GPT-4.1-mini `2025-04-14`.
Extract a strict structured result, validate the schema, and independently compare
it with visual ground truth. No personal images, scanned receipts, external image
URLs, or API keys are used. Default execution is noninteractive and makes two
real cloud requests.

## Images and truth

The included `images/clear.png` shows merchant `WORKSHOP CAFE`, currency `USD`,
and total `12.50`. `images/ambiguous.png` has the same legible merchant, **no
currency**, and an **opaque ink rectangle** covering the total. No line-item
prices are present, so the amount cannot be reconstructed.
`images/truth.json` is the ground-truth manifest, not content sent to the model.

```json
{"merchant":"WORKSHOP CAFE","currency":null,"total":null,
 "uncertainty":["currency_missing","total_unreadable"]}
```

`dotnet run --file demo.cs -- --generate-only` regenerates both PNGs and the
manifest locally using an embedded bitmap font; no network, system font, Azure
login, or image service is needed. They are also regenerated before a live run.

## Prerequisites and deployment

.NET 10 SDK, Azure CLI/Bicep, `az login`, model access and quota. Resource creation
plus role-assignment rights are needed (for example Contributor plus User Access
Administrator). Inference uses noninteractive `DefaultAzureCredential` and
**Cognitive Services OpenAI User** at the account scope. Service-principal users
can pass `principalType=ServicePrincipal` and the **service principal object ID**
(Enterprise application), not the app/client ID or app-registration object ID.
Allow a few minutes for RBAC propagation.

`demo.py`, `test_demo.py`, and `requirements.txt` remain unchanged as transitional
compatibility files for the existing Python runner. Use the C# file-based apps below.

From this directory, verify the subscription's region, model/version and
GlobalStandard availability before deployment. This model must support **both
image input and structured outputs**, not merely text chat:

```bash
az login
az account set --subscription YOUR_SUBSCRIPTION_ID
az cognitiveservices model list --location eastus2 -o table
az cognitiveservices usage list --location eastus2 -o table
RG=foundry-lab26
LOCATION=eastus2
PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv)
az group create --name "$RG" --location "$LOCATION" --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --name lab26 -g "$RG" --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location="$LOCATION" \
  --query properties.outputs -o json > deployment.json
export AZURE_OPENAI_ENDPOINT=$(az deployment group show -g "$RG" -n lab26 --query properties.outputs.endpoint.value -o tsv)
export AZURE_OPENAI_DEPLOYMENT=$(az deployment group show -g "$RG" -n lab26 --query properties.outputs.deployment.value -o tsv)
dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet run --file demo.cs --no-build -- --generate-only
dotnet run --file demo.cs --no-build
dotnet restore test_demo.cs
dotnet build test_demo.cs --no-restore
dotnet run --file test_demo.cs --no-build
az bicep build --file infra/main.bicep --stdout >/dev/null
```

The packaged files are real 640×400 PNGs, sent as base64 `data:image/png` URLs
inside user `image_url` content with `detail: high`. Azure's vision API supports
PNG/JPEG (and some additional image formats); **SVG is not used as model input**.
Do not assume that any deployment alias supports images. HTTP 400 may indicate
unsupported image/schema/model; 403 is usually RBAC; 429 means quota/rate limits.
Service errors remain errors rather than substitute predictions.

## Validation and interpretation

The supported JSON Schema uses a root object, required nullable fields, an array
of enum uncertainty reasons, and `additionalProperties: false`. All fields are
required, with **null for missing or unreadable values**. Schema validation is
followed by independent merchant/currency/total/uncertainty comparison. Uncertainty
ordering is ignored, but missing or duplicate reasons fail exact matching.
Refusals and non-`stop` finishes are reported explicitly before attempting JSON
parsing. Request errors have no fabricated token usage or success result.

The C# offline harness checks the supplied PNG dimensions/data payload, opaque
mask, truth values, schema-valid-but-wrong guesses, refusals, truncation, invalid
schema/JSON, timeouts and cost calculation without Azure access. `--help`, invalid
rate validation, and `--generate-only` are safe local paths. The ink-covered amount has no knowable numeric answer. A schema-valid guessed
total or currency is a **visual accuracy failure**, even if it matches the clear
receipt. Tests explicitly demonstrate this distinction. Offline tests check
the supplied PNG payloads, mask, truth, nullable fields, refusal/truncation,
schema violations, cost calculations and timeout handling; they call no cloud API.

## Costs, limitations, outputs

Every live result includes serving model, service-reported token usage where
available, and an estimated USD cost. Defaults are **illustrative**, not a current
quote: input $0.40/million tokens and output $1.60/million tokens. Override with
`--input-rate` / `--output-rate` after checking
[current Azure prices](https://azure.microsoft.com/pricing/details/cognitive-services/openai-service/).
Formula: `(prompt_tokens * input_rate + completion_tokens * output_rate) / 1e6`.
Image tokens are included in prompt usage where reported; no separate per-image
count is invented. High detail can cost more; cache discounts, tax, negotiated
rates, and retries are excluded. Missing usage yields null cost, not zero.

Inputs: `principalId`, `location`. Outputs: `endpoint`, `deployment`
(`receipt-vision`); no additional runner mapping is required.
Quota, region availability, and model support vary. GlobalStandard may process
outside the account region; public networking is enabled for this workshop.
These clean synthetic images are not an OCR accuracy benchmark or a substitute
for human review of financial documents. Schema compliance is not visual truth.

## Cleanup

```bash
az group delete --name foundry-lab26 --yes --no-wait
```

## Official sources

* [Vision-enabled chat models, image payloads and limitations](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/gpt-with-vision)
* [Structured outputs and supported schema subset](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/structured-outputs)
* [OpenAI v1 endpoint and Entra authentication](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/switching-endpoints)
