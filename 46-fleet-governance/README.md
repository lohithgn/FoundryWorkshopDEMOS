# 46 — Native two-project fleet governance

Deploys **two Foundry projects**, two real persistent prompt agents, a native
external-agent registration, shared Entra-authenticated Application Insights and
an **isolated RG-scoped Azure Policy Audit assignment**. Native agents/connections
are inventoried across both projects. Actual prompt-agent invocations and an
actual synthetic external Python runtime emit OpenTelemetry spans; ingested
Azure Monitor rows are correlated by project ID, agent name, telemetry ID and run.

This is fleet governance, not a claim that every Microsoft security service or
every Foundry Control Plane portal screen has been configured.

## Deploy and run

Python 3.11+, Azure CLI/Bicep, signed-in Entra user with deployment, role-assignment
and RG policy-assignment rights, model quota and external-agent preview access.
All resources are eastus2; one `gpt-5.4-mini` GlobalStandard deployment at capacity
**5**, shared between the projects. Application Insights/Log Analytics are billable.
No gateway, Defender plan, Purview subscription or broad tenant identity changes.

From repository root (no manual runtime output exports required):

```bash
python3 deploy.py --demo 46
```

Standalone:

```bash
python3 -m venv 46-fleet-governance/.venv
46-fleet-governance/.venv/bin/pip install -r 46-fleet-governance/requirements.txt
46-fleet-governance/.venv/bin/python 46-fleet-governance/deploy_lab.py --infra-only
cd 46-fleet-governance
.venv/bin/python -m unittest -v
.venv/bin/python demo.py --timeout 600
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
  gateway-based blocking**. The external runtime is the real `external_runtime`
  Python function called under an instrumented server span, not a fake cloud agent.
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

Official facts verified with installed `azure-ai-projects==2.7.0` signatures:

* [Control Plane scope and portal](https://learn.microsoft.com/azure/foundry/control-plane/overview)
* [External native registration and telemetry boundary](https://learn.microsoft.com/azure/foundry/control-plane/migrate-custom-to-external-agent)
* [Azure AI Services policy controls](https://learn.microsoft.com/azure/ai-services/security-controls-policy)
* [Policy state evaluation API](https://learn.microsoft.com/rest/api/policy/policy-states/trigger-resource-group-evaluation)
* [Azure Monitor OpenTelemetry](https://learn.microsoft.com/azure/azure-monitor/app/opentelemetry-enable)

Pylance transport was unavailable; runtime SDK facts, syntax compilation and focused
unittest provide verification. See ignored live report for execution-specific results.

## Live verification — 2026-11-03

**16 offline tests PASS**. Actual ARM deployment and full implemented API workflow
**PASS** in `rg-foundry-fleet-governance-demo`, eastus2:
two native agent responses completed with the expected synthetic answer; external
registration/readback and actual external runtime completed; native two-project
inventory and **three actual Azure Monitor trace groups** correlated by exact
telemetry ID, project and run; isolated Azure Policy evaluated **Compliant**.

Current Bicep build/lint have only two **BCP036 schema-drift warnings** for
`ProjectManagedIdentity` connections; ARM accepted those connections and SDK
inventory verified them. The Operate portal remains **NOT_VERIFIED** and
Defender/Purview **OPTIONAL_NOT_CONFIGURED**, not implied by API workflow PASS.
Evidence: `.azure/live-report.json`; final verified run
`c8772476-9c24-4fa0-9e72-b43bdf1f4ce8`.
