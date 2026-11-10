# 39 — Real AI Gateway token limits

Deploy an isolated Azure API Management AI gateway with actual
`llm-token-limit` policies: **100 tokens/minute** and **100 tokens/hour**.
Two real Foundry project resource IDs are fixed into separate API policy
counter keys; client headers cannot change their project identity or budget.
The same authorized workshop presenter can exercise both project routes.
These are explicit APIM project-scoped APIs, **not** an undocumented claim
that Foundry portal onboarding or project inference redirection is automatic.

The live demo reads all four actual ARM policies and verifies their fixed project
resource-ID counter keys and exact limits, verifies successful backend-reported usage, exhausts Project A's
small budgets with benign synthetic inventory text, proves gateway-origin
**429** (rate) and **403** (hourly quota), then proves Project B still works.
Status alone never passes: the exact APIM policy ID and native
`OpenAITokenLimitExceeded` / `OpenAITokenQuotaExceeded` reason must match.
Eight bounded nonstreaming requests, paced seven seconds apart to respect the
backend's short-window RPM limit, max 16 output tokens each; no token-limit retry loops
or local quota simulation. With `estimate-prompt-tokens=false`, one request
can overshoot the budget before the following request is blocked, as documented.
The only exception is up to two rejected initial `404` retries, 15 seconds apart,
for observed new-APIM route propagation. No quota/rate failures are retried and
these startup failures are recorded, never interpreted as a successful model call.

## Deployment and presentation

Consumption does **not** support this policy. **Developer capacity 1** is the
lowest-cost supported workshop tier; it has no production SLA and incurs hourly
charges while deployed. Provisioning can take 30–60 minutes. This is public
networking with Entra authentication, not a private-network demonstration.
Azure's retail pricing API checked on 2026-11-03 lists East US 2 Developer Unit
at **USD 0.0658/hour** (~USD 1.58/day, excluding model tokens and taxes;
subscription-specific prices may differ). The zero-priced workspace-pack meter
is not the deployed Developer Unit. [Current pricing](https://azure.microsoft.com/en-us/pricing/details/api-management/)
The backend disables local authentication; APIM uses its managed identity.
Frontend tokens require the signed-in user's object ID and Azure CLI client ID.
There are no API keys, APIM subscriptions or secrets in code/logs.

### Transitional Python deployment and prerequisites

This Python-only deployment, prerequisite, and cleanup guidance is retained for
transitional compatibility with the shared root runner and `deploy_lab.py`;
`requirements.txt` and Python files remain unchanged.

From the repository root: `python3 deploy.py --demo 39`.
From the lab folder, the standalone alternative is
`python3 deploy_lab.py --prefix rg-foundry`.

For manual ARM deployment only, group creation must include:

```bash
az group create --name rg-foundry-gateway-token-limits-demo --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
```

Do not create an unrecorded group before running standalone: it creates fresh
groups itself and refuses to adopt an unowned group.

Prerequisites for this transitional Python runner: Python 3.10+, signed-in
Azure CLI user; resource/role-assignment
creation and ARM policy-read/**write** permissions for the per-run counter reset,
APIM Developer availability in `eastus2`, and
`gpt-4.1-mini` GlobalStandard quota capacity 10 (10 RPM / 10,000 TPM), needed
to keep the eight-call gateway test below the model's own request limit.
Model capacity reserves quota, not an extra fixed token charge. Foundry project writes are
serialized. ARM uses Cognitive Services `2025-06-01` and APIM `2024-05-01`;
model inference uses `2024-10-21`. This standalone gateway does not depend
on preview Foundry portal gateway APIs.

The root command above and `deploy_lab.py` continue to use the Python
implementation during this transition.

The ownership-safe standalone runner defaults to `rg-foundry-student-*`; use
`--prefix rg-foundry` for the recorded presentation group. Unowned/unrecorded
group reuse is refused; owned groups receive the required security tags again.

Root mappings: `endpoint` → `AZURE_OPENAI_ENDPOINT`,
`deployment` → `AZURE_OPENAI_DEPLOYMENT`, optional
`gatewayEndpoint` → `AI_GATEWAY_ENDPOINT`. `demo.py` loads its own ignored
`.azure/deployment.json`; recorded gateway outputs override inherited environment.
Deployment metadata follows `{subscription, resourceGroup, outputs}`.

**Default reruns are reproducible even within the same exhausted hour.** Before
each test, the authorized presenter uses ARM to rotate all four APIM policies to
new per-run counter keys, still anchored to the same real project resource IDs
and still enforcing exactly 100 TPM / 100 hourly tokens. This is an explicit
privileged lab reset, not local quota simulation or a client-controlled bypass.
It requires APIM policy-write permissions. The run ID and limits are recorded in
ignored `.azure/counter-run.json`, read back from ARM, and matched to a
server-generated response header on every actual request. Old/stale counter
policies cannot pass. Existing counters remain centrally exhausted; new test
counters are isolated. No waiting for the next hour or manual exports is required.
Only bounded propagation retries are allowed; budgets are never increased.
Run one presentation at a time per gateway: concurrent policy rotations are
detected by the run-ID gate and fail rather than share or misattribute counters.
Leave resources deployed for presentation; delete afterwards:

```bash
.venv/bin/python -m unittest -v
az group delete --name rg-foundry-gateway-token-limits-demo --yes --no-wait
```

## .NET 10 file-based counterpart

The C# entry points are standalone .NET 10 file-based apps with no
`.csproj`/`.sln` files and no NuGet dependencies. Starting at the repository
root, enter this lab directory, then restore and compile all three files, run the
offline evidence-gate harness, and inspect the safe CLI help. The shared source
uses the file-app `#:include` directive, available in .NET 10 SDK 10.0.300+:

```bash
cd 39-gateway-token-limits
dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet restore deploy_lab.cs
dotnet build deploy_lab.cs --no-restore
dotnet restore test_demo.cs
dotnet build test_demo.cs --no-restore
dotnet run --file test_demo.cs
dotnet run --file demo.cs -- --help
dotnet run --file deploy_lab.cs -- --help
```

Deploy/run with the standalone C# counterpart, or replay the demo after an
existing deployment:

```bash
dotnet run --file deploy_lab.cs -- --prefix rg-foundry
dotnet run --file demo.cs
```

`dotnet run --file deploy_lab.cs -- --offline-only` runs the offline harness and
builds/lints Bicep without deploying; Azure CLI with Bicep is still required.
The live C# app uses Azure CLI user tokens for ARM
(`https://management.azure.com/`) and inference
(`https://cognitiveservices.azure.com/`), and performs the same privileged
per-run ARM policy rotation and exact service-side evidence checks as the Python
runner. These are live paid service requests. This conversion ran no live
deployment or gateway/model workload; offline results are not live enforcement
evidence.
The C# demo uses the same root-shaped `.azure/deployment.json` output contract;
saved gateway metadata takes precedence over `AI_GATEWAY_ENDPOINT`.

## Official references

- [Token-limit policy, supported tiers and accounting](https://learn.microsoft.com/en-us/azure/api-management/llm-token-limit-policy)
- [AI Gateway capabilities](https://learn.microsoft.com/en-us/azure/api-management/genai-gateway-capabilities)
- [Managed-identity backend authentication](https://learn.microsoft.com/en-us/azure/api-management/authentication-managed-identity-policy)
- [Entra frontend validation](https://learn.microsoft.com/en-us/azure/api-management/validate-azure-ad-token-policy)
- [Foundry portal gateway onboarding — distinct from this lab](https://learn.microsoft.com/en-us/azure/foundry/configuration/enable-ai-api-management-gateway-portal)

Offline tests validate gates, not live enforcement. Live failures remain BLOCKED,
with the actual stage and sanitized response recorded under `.azure`.
