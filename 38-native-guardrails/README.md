# 38 — Native agent guardrails, with a model baseline

The default entrypoint now requires **two separate live gates**:

1. The original model-attached guardrail baseline below.
2. A temporary native `PromptAgentDefinition` with
   `rai_config=RaiConfig(rai_policy_name=<fully-qualified policy ARM ID>)`.
   The version is read back to verify its persisted native attachment. Its
   **separate `agent-baseline` deployment has only Microsoft.Default**: the same
   synthetic marker succeeds when called directly, but is blocked when sent to
   the guarded agent. A benign agent request must complete successfully.

The actual agent intervention is HTTP 400 / `content_filter` /
`ContentFiltered`, with `content_filters[].blocked=true`, `source_type=prompt`,
and the exact filtered custom-blocklist ID. A model-only result cannot satisfy
the agent gate. The agent is deleted after evidence collection. Model and agent
results are saved separately under ignored `.azure`; agent failure exits nonzero
even if the explanatory model baseline passes.

Installed `azure-ai-projects>=2.9.0` uses preview agent APIs. A documented/runtime
probe showed that a short policy name is rejected; the native agent requires the
full policy ARM resource ID. East US 2 was live-tested. The unguarded agent model
uses capacity 5, with 15-second request pacing.

An actual Foundry model deployment attaches `workshop-native-guardrail`, inheriting
Microsoft defaults plus blocking safety categories and a custom prompt blocklist.
The live gate verifies the ARM attachment, a successful benign request, and a
native `400/content_filter/ResponsibleAIPolicyViolation` with filtered
`custom_blocklists` details for a harmless synthetic inventory marker.
There is **no separate classifier** and no harmful test material.
The evidence gate accepts both the documented legacy details object and the
current service's observed list shape, always requiring the exact deployed
blocklist ID and `filtered=true`.

The agent test demonstrates the **native agent user-input boundary**.
Tool-call/tool-response controls are preview, require moderation-aware supported
tools, and are not configured in this lab; no tool interception is claimed.
Custom blocklists are documented model guardrails; availability and propagation
depend on region, model and Cognitive Services API version. Provisioning uses
`2025-06-01`; inference uses stable `2024-10-21`. The small GlobalStandard
`gpt-4.1-mini` deployment uses capacity 3 in `eastus2` (3 RPM / 3,000 TPM).
Capacity 1 provides only 1 RPM and experimentally throttled the two-case demo;
capacity 3 prevents that unrelated backend throttle from obscuring the guardrail.
The two cases are paced 25 seconds apart because Azure evaluates RPM over short
windows; this does not implement or simulate a guardrail.

## Run

Azure CLI must be signed in as a user with resource creation, role assignment,
ARM read and Cognitive Services OpenAI User permissions. Python 3.10+ is required.
The native agent gate additionally uses Foundry User on the project, assigned by
the template. Both deployments need `gpt-4.1-mini` `2025-04-14` GlobalStandard
quota: capacity 3 for the model baseline plus 5 for the agent baseline.
`deploy_lab.py` restores dependencies, compiles/lints Bicep, tests, deploys and
runs live evidence. Resources stay deployed for presentation.

From the repository root: `python3 deploy.py --demo 38`.
From the lab folder, the standalone alternative is
`python3 deploy_lab.py --prefix rg-foundry`.

For manual ARM deployment only, group creation must include:

```bash
az group create --name rg-foundry-native-guardrails-demo --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
```

Do not manually create an unrecorded group before using the standalone runner;
it creates fresh groups itself and deliberately refuses to adopt an unowned group.

The standalone runner defaults to an isolated `rg-foundry-student-*` prefix and
refuses unrecorded/unowned group reuse. For the already deployed presentation
group, use `--prefix rg-foundry`. Ownership is recorded in
`.azure/lab-deployment.json`; security tags are reapplied when an owned group is reused.

The root runner recognizes `endpoint` → `AZURE_OPENAI_ENDPOINT` and
`deployment` → `AZURE_OPENAI_DEPLOYMENT`. The demo also reads
`accountResourceId`, `projectEndpoint` and `agentDeployment` from its own ignored
root-shaped `{subscription,resourceGroup,outputs}` `.azure/deployment.json`.
Recorded outputs take precedence over inherited environment values.
All authentication is Entra (`AzureCliCredential`); no keys are provisioned or
printed. `.azure` contains deployment metadata and sanitized live evidence only.
Each replay makes paid model/agent requests; rejected requests may also incur
usage charges. Capacity is a throughput allocation, not prepaid PTUs or a
spending cap. Public networking and GlobalStandard cross-region processing are
workshop simplifications. Removing temporary agents does not delete infrastructure.

```bash
.venv/bin/python -m unittest -v
az group delete --name rg-foundry-native-guardrails-demo --yes --no-wait
```

## Official references

- [Native custom blocklists and intervention response](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/use-blocklists)
- [Configure model guardrails](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/content-filters)
- [Native policy ARM schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.cognitiveservices/2025-06-01/accounts/raipolicies)
- [Agent intervention points and supported moderation-aware tools](https://learn.microsoft.com/en-us/azure/foundry/guardrails/intervention-points)
- [Official SDK native RaiConfig definition](https://github.com/Azure/azure-sdk-for-python/blob/main/sdk/ai/azure-ai-projects/azure/ai/projects/models/_models.py)

Offline tests only validate evidence gates; they are not live service evidence.
