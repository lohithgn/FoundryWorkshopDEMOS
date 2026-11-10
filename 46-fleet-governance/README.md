# 46 — Native two-project fleet governance

> **Migration status:** .NET 10 file-based C# is the current lab workflow.
> Original Python sources and requirements are retained unchanged temporarily
> for staged root-runner integration. Python-era evidence below is historical
> and does not validate the .NET port; no .NET live Azure validation is claimed.
>
> **Historical Python status: API workflow PASS; Operate portal NOT_VERIFIED.**
> Defender/Purview remain OPTIONAL_NOT_CONFIGURED. Cloud API/trace/policy evidence
> is not visual verification of the full Control Plane experience.

Deploys **two Foundry projects**, two real persistent prompt agents, a native
external-agent registration, shared Entra-authenticated Application Insights and
an **isolated RG-scoped Azure Policy Audit assignment**. Native agents/connections
are inventoried across both projects. Actual prompt-agent invocations and an
actual synthetic external C# runtime emit OpenTelemetry spans; ingested
Azure Monitor rows are correlated by project ID, agent name, telemetry ID and run.

This is fleet governance, not a claim that every Microsoft security service or
every Foundry Control Plane portal screen has been configured.

## Deploy and run

.NET 10 SDK, Azure CLI/Bicep, signed-in Entra user with deployment, role-assignment
and RG policy-assignment rights, model quota and external-agent preview access.
All resources are eastus2; one `gpt-5.4-mini` GlobalStandard deployment at capacity
**5**, shared between the projects. Application Insights/Log Analytics are billable.
No gateway, Defender plan, Purview subscription or broad tenant identity changes.

From this lab directory, restore/build and run safe help and offline checks:

```bash
dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet run --file demo.cs -- --help
dotnet restore test_demo.cs
dotnet build test_demo.cs --no-restore
dotnet run --file test_demo.cs
dotnet restore deploy_lab.cs
dotnet build deploy_lab.cs --no-restore
dotnet run --file deploy_lab.cs -- --help
```

After confirming subscription, location, user permissions, and charges, deploy
using the standalone ownership-checked helper. `--infra-only` still creates
billable Azure resources; without it, the helper also runs live gates:

```bash
dotnet run --file deploy_lab.cs -- --infra-only
dotnet run --file demo.cs -- --timeout 600
```

The wrapper creates and records its group in `.azure/lab-deployment.json`.
Reuse requires both that record and matching `managedBy`/`lab` ownership tags;
unrecorded/unowned groups or changed subscriptions/targets are refused.
`--infra-only` skips live gates. Fresh default is the requested RG; existing
recorded owned targets are retained. Equivalent tagged RG command (reference,
**not a pre-create step for the wrapper**):

```bash
az group create --name rg-foundry-fleet-governance-demo --location eastus2 --tags managedBy=foundry-lab-script lab=46-fleet-governance SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
```

Bicep requires `location` and `principalId` (**User**); Foundry child writes are
serialized. Outputs: `endpoint`, `deployment`, `projectEndpoint`,
`projectResourceId`, `secondProjectEndpoint`, `secondProjectResourceId`,
`applicationInsightsResourceId`, `workspaceId`, `policyAssignmentId`, `tenantId`.
The demo loads the root runner's `.azure/deployment.json` format and/or common
output-to-environment mappings. Resources remain deployed for presentation.

## Native fleet, registration and telemetry

* `AIProjectClient.agents.create_version/get_version/list` and
  `connections.list` provide actual assets in both project scopes.
* `ExternalAgentDefinition(otel_agent_id="workshop.synthetic.fleet.external")`
  with `allow_preview=True` registers the actual synthetic external runtime.
  SDK readback must retain that telemetry ID.
* Registration **does not host a runtime, proxy requests, configure APIM or apply
  gateway-based blocking**. The external runtime is the real C# function called
  under an instrumented server span, not a fake cloud agent.
* Two real native Responses API calls must complete with `36 months`.
* Azure Monitor exporter uses `AzureCliCredential`, with Application Insights
  `DisableLocalAuth=true`; the connection string is fetched in memory and never
  logged. Disk retry storage is disabled.
* Log Analytics must contain real ingested rows for every executed agent. No
  local collection, test fixture or registration readback counts as cloud telemetry.
  These are instrumented **client/external runtime spans**, not claims of complete
  Foundry server trace instrumentation. Missing ingestion is explicitly BLOCKED.
  Failed real requests can produce genuine failure traces; cloud correlation can
  PASS while the separate `nativeInvocations` gate remains BLOCKED. Full lab
  success still requires both actual prompt-agent responses to complete.

## Native isolated Azure Policy

The built-in **Azure AI Services resources should have key access disabled**
(`71ef260a-8f18-47b7-abcb-62d0673d94dc`) is assigned only to
`rg-foundry-fleet-governance-demo`, with effect **Audit**. No subscription custom
definition, broad Deny, remediation or shared resource mutation.
The demo verifies assignment scope/effect, triggers Policy Insights evaluation
and requires an actual **Compliant** state for the isolated Foundry account.
An existing assignment without evaluated state is **BLOCKED**, not compliance PASS.

## Portal-only Control Plane verification — separate, not claimed by APIs

Open the new Foundry portal in the same tenant/subscription; choose **Operate**:

1. **Assets/Agents**: verify `synthetic-fleet-operator` in `fleet-operations`,
   `synthetic-fleet-auditor` in `fleet-audit`, and `synthetic-fleet-external`.
2. Open the external registration, verify telemetry ID and new trace association;
   compare run ID against `.azure/invocations.json`.
3. Inspect fleet monitoring and compliance views and their role-aware scope.
4. Record portal screenshots separately if presenting the full UI experience.

`controlPlanePortal` remains **NOT_VERIFIED** until a human/browser actually checks
those panes. Native cross-project API inventory is **not** full UI verification.
Advanced Control Plane guardrails can require AI gateway onboarding; this lab
does not claim that RG Azure Policy is a substitute for agent-level guardrails.

## Optional Defender/Purview

Security dashboards require tenant subscriptions, onboarding and permissions.
They are **OPTIONAL_NOT_CONFIGURED** here; no fabricated security alerts or
synthetic Purview/Defender API successes. To extend in an approved tenant, follow
the Control Plane security integration documentation and use only designated
synthetic assets. Neither integration is a prerequisite for this fleet demo.

## Evidence, exit codes and current facts

`.azure/deployment.json`, `agents.json`, `external-agent.json`, `invocations.json`
and `live-report.json` are ignored. Full implemented API workflow returns 0 only
when native agents, completed native invocations, external registration, cross-project inventory, cloud
telemetry correlation and evaluated isolated policy all PASS; otherwise exit 2
with feature-specific BLOCKED evidence. Portal and optional integrations retain
their separate status even when API workflow succeeds.
Offline tests are contract tests only. No access tokens, keys or secrets are logged.

Official API facts were historically verified with Python SDK `azure-ai-projects==2.7.0`:

* [Control Plane scope and portal](https://learn.microsoft.com/azure/foundry/control-plane/overview)
* [External native registration and telemetry boundary](https://learn.microsoft.com/azure/foundry/control-plane/migrate-custom-to-external-agent)
* [Azure AI Services policy controls](https://learn.microsoft.com/azure/ai-services/security-controls-policy)
* [Policy state evaluation API](https://learn.microsoft.com/rest/api/policy/policy-states/trigger-resource-group-evaluation)
* [Azure Monitor OpenTelemetry](https://learn.microsoft.com/azure/azure-monitor/app/opentelemetry-enable)

The historical live report contains execution-specific Python evidence; it does not
establish .NET live behavior.

## Cleanup

Agents, external registration, the RG-scoped Audit assignment and Monitor
resources remain deployed; telemetry ingestion/retention can continue billing.
Stop this lab's instrumented processes, confirm the subscription and actual
group in `.azure/deployment.json` (recorded/custom targets can differ), then delete
only the dedicated lab group:

```bash
az group delete --name rg-foundry-fleet-governance-demo --yes --no-wait
```

Do not delete shared Monitor workspaces or change tenant Defender/Purview settings
to clean up this isolated lab. Local ignored reports remain after RG deletion.

## Historical Python live verification — 2026-11-03

**Historical Python validation only:** 16 offline tests passed. Actual ARM deployment and full API workflow
passed in `rg-foundry-fleet-governance-demo`, eastus2:
two native agent responses completed with the expected synthetic answer; external
registration/readback and actual external runtime completed; native two-project
inventory and **three actual Azure Monitor trace groups** correlated by exact
telemetry ID, project and run; isolated Azure Policy evaluated **Compliant**.

Current Bicep build/lint have only two **BCP036 schema-drift warnings** for
`ProjectManagedIdentity` connections; ARM accepted those connections and SDK
inventory verified them. The Operate portal remains **NOT_VERIFIED** and
Defender/Purview **OPTIONAL_NOT_CONFIGURED**, not implied by API workflow PASS.
Historical evidence: `.azure/live-report.json`; final verified run
`c8772476-9c24-4fa0-9e72-b43bdf1f4ce8`.
