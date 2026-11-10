# Lab 41: GA schema-based Content Understanding

> **Migration status:** .NET 10 file-based C# is the current lab workflow.
> Original Python sources and requirements are retained unchanged temporarily
> for the staged root-runner integration; Python-era evidence below is historical
> and does not validate the .NET port. No .NET live Azure validation is claimed.

Uses **native Azure Content Understanding GA API `2025-11-01`**, not Document
Intelligence `prebuilt-invoice` and not an LLM guessing from a local PDF.
Creates two custom field-schema analyzers based on native CU
`prebuilt-document` and `prebuilt-audio`. Document fields use `method: extract`.
**GA audio rejects `extract`** (`UnsupportedGenerationMethod` from the actual
service), so its schema uses the documented `generate` method for structured
facts. Each generated audio fact must still match actual time-grounded service
transcript evidence; it isn't accepted as an ungrounded summary or guess.
Existing analyzers are reused only after their remote base, models and exact
field generation methods match; replay doesn't overwrite create-only analyzers.

Both extract the same known synthetic facts: **Cedar Workshop**, **Blue Room**,
**12 seats**. The script uploads repository PDF/WAV bytes directly to CU,
polls real service operations, and compares every extracted fact to ground truth.
Each long-running operation has a bounded 600-second deadline; cold-start or
model-capacity delays are surfaced rather than replaced with local extraction.
Immediately after ARM deployment, the idempotent defaults configuration retries
the specific `DeploymentIdNotFound` response for one of the requested deployment
aliases for up to 180 seconds. Other errors and a persistent missing deployment
still fail explicitly.

- **Document:** requires service-emitted confidence >= 0.8 for every field,
  actual document source polygons referencing returned pages, and matching
  service document text. Missing evidence fails; no fabricated confidence.
- **Audio:** requires actual extracted fields and matching service transcript
  phrases with valid start/end timestamps. GA audio does not promise per-field
  confidence/polygon grounding. The report keeps absent confidence/source
  explicitly `null` and uses the original time-grounded transcript phrases,
  **not an invented confidence score**.

## Dependencies and location

- .NET 10 SDK, Azure CLI/Bicep, `az login` user allowed to create resources and
  role assignments. Only Entra authentication, no stored keys or credentials.
- A Foundry `AIServices` resource with model deployment defaults configured.
  The lab deploys `gpt-5.2` **`2025-12-11`**, verified against the actual
  regional model catalog, and `text-embedding-3-large` **`1`**.
  Both use GlobalStandard capacity **10**; Bicep allows capacity 1–10.
  Real `gpt-5.4-mini` extractions reached the CU worker but failed with its
  `RateLimit, 429` model request/token quota error at both capacity 1 and 10.
  The documented `gpt-4o-mini` alternative was then rejected by ARM with
  `ServiceModelDeprecating`, despite still appearing in CU's supported-model
  list and the regional catalog. The current custom-analyzer tutorial explicitly
  uses `gpt-5.2`; that alternative **passed both live modalities**.
  No subscription quota increase or capacity above 10 is requested.
- `--preflight` verifies the **actual model catalog and available quota** before
  provisioning. EastUS2 is the default and is documented for GA CU. Bicep's
  region allowlist matches the official CU region list. Unsupported locations
  fail before resources are created.
- The Foundry resource has managed identity and inference RBAC; presenter gets
  Foundry User. Account child model/project writes are explicitly serialized.
  Each taggable resource and the RG have the workshop exemption tags.

## Run

From this lab directory, restore/build and exercise the safe help path, then run
the offline regression harness:

```bash
dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet run --file demo.cs -- --help
dotnet restore test_demo.cs
dotnet build test_demo.cs --no-restore
dotnet run --file test_demo.cs
dotnet restore generate_assets.cs
dotnet build generate_assets.cs --no-restore
dotnet run --file generate_assets.cs -- --help
```

`generate_assets.cs` rewrites the committed PDF/WAV fixtures when run without
`--help`; audio regeneration requires macOS `say`. After an authorized deployment,
run the service preflight and live demo using the existing output mappings:

```bash
dotnet run --file demo.cs -- --preflight --location eastus2
dotnet run --file demo.cs
```

Standard output mappings:
`endpoint -> AZURE_OPENAI_ENDPOINT`, `projectEndpoint -> FOUNDRY_PROJECT_ENDPOINT`,
`deployment -> AZURE_OPENAI_DEPLOYMENT`,
`embeddingDeployment -> AZURE_OPENAI_EMBEDDING_DEPLOYMENT`.
The script reads root-shaped `.azure/deployment.json` directly, including model
names, so replay requires no manual runtime output exports.

For an existing
standalone resource, set `CONTENT_UNDERSTANDING_ENDPOINT`,
`AZURE_OPENAI_DEPLOYMENT` (completion deployment), and
`AZURE_OPENAI_EMBEDDING_DEPLOYMENT` (embedding deployment).
Optional model overrides are `CU_COMPLETION_MODEL`, `CU_EMBEDDING_MODEL`;
template parameters are `location`, `principalId`, `accountName`, `projectName`,
`deploymentName`, `completionModel`, `completionVersion`,
`embeddingDeploymentName`, `embeddingModel`, `embeddingVersion`, and `capacity`.
Model overrides must stay compatible with the GA supported-model list.

## Evidence, fixtures and costs

**Historical Python validation only (2026-11-03):** 32 Python offline tests passed,
Bicep build/lint and EastUS2 ARM deployment succeeded, and both GA native
modalities passed live. Document confidences were **0.919 / 0.982 / 0.981** with
real page polygons. Audio returned the same three facts with real transcript
timestamps and explicitly absent per-field confidence. The authoritative replay
result is `.azure/live-report.json`. A separate 2026-11-09 recording run also
passed both modalities and its resource group was verified deleted.

Ignored `.azure/` contains `deployment.json` in
`{subscription,resourceGroup,outputs}` format, preflight/command logs,
`defaults.json`, each persisted analyzer definition, raw service operation
responses and `live-report.json`. Reports include actual operation IDs,
submitted file SHA-256 hashes and service evidence. Missing fields,
confidence, page grounding, transcript timing or expected values never pass.
An HTTP/service/model authorization failure is `BLOCKED`, not a local fallback.

[notice.pdf](assets/notice.pdf) is a deterministic, single-page synthetic PDF;
[notice.wav](assets/notice.wav) is locally synthesized benign speech, not a
microphone capture. Running `dotnet run --file generate_assets.cs` regenerates PDF and macOS
Samantha speech; committed bytes work cross-platform. No personal data.

**Deployed resources are billable.** CU document pages/audio duration,
completion/embedding tokens and retained model deployments can incur charges.
This template allocates throughput capacity 10 for each deployment, not prepaid
PTUs. GlobalStandard models bill for token usage, not an idle PTU reservation.
The historical standalone lab resource group was absent after cleanup on
2026-11-09. It previously retained a failed `cu-completion` `gpt-5.4-mini`
deployment and analyzer for diagnosis; these were not a passing fallback.
After a new run, delete only its isolated lab resource group when finished:
`az group delete --name rg-foundry-content-understanding-demo --yes --no-wait`.

## Official current references

- [GA REST quickstart and model defaults](https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/quickstart/use-rest-api)
- [GA binary upload API](https://learn.microsoft.com/en-us/rest/api/contentunderstanding/content-analyzers/analyze-binary?view=rest-contentunderstanding-2025-11-01)
- [Custom analyzer schemas and Python SDK sample](https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/tutorial/create-custom-analyzer)
- [Models and deployment defaults](https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/concepts/models-deployments)
- [Supported GA generative models](https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/service-limits#supported-generative-models)
- [Supported regions](https://learn.microsoft.com/en-us/azure/ai-services/content-understanding/language-region-support)

Learn schema/API examples were fetched before implementation. Actual runtime
signatures, syntax compilation, focused unit tests and service responses ground
validation; Pylance was unavailable in this session.
