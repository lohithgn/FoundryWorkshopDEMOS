# 18 · Agent tracing: find a slow tool and a failed tool

> **Migration note:** The Python entry point and `requirements.txt` remain unchanged
> temporarily for the root runner. They are transitional compatibility files; use
> the .NET 10 file-based app below for this lab.

[Feature backlog](../BACKLOG.md#f018---microsoft-foundry-agent-tracing)

A deliberately small, scripted pricing agent executes a real pricing function and
a real Azure OpenAI request. Each of three runs exports a parent orchestration
span, a tool span, and a model span through the supported OpenTelemetry SDK and
Azure Monitor trace exporter. It is not an automatic model-selected tool loop.

## Prerequisites / deploy

.NET 10 SDK, Azure CLI with Bicep, `az login`, a subscription with `gpt-4.1-mini` quota in
`eastus2` (or override `location`), and permission to create resources and role
assignments. No API keys. Run commands from this directory.

```bash
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
az provider register --namespace Microsoft.Insights --wait
az provider register --namespace Microsoft.OperationalInsights --wait
az group create -n lab18-tracing -l eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create -g lab18-tracing -n lab18 -f infra/main.bicep \
  -p principalId="$(az ad signed-in-user show --query id -o tsv)"
export AZURE_OPENAI_ENDPOINT="$(az deployment group show -g lab18-tracing -n lab18 --query properties.outputs.endpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show -g lab18-tracing -n lab18 --query properties.outputs.deployment.value -o tsv)"
export APPLICATIONINSIGHTS_CONNECTION_STRING="$(az deployment group show -g lab18-tracing -n lab18 --query properties.outputs.applicationInsightsConnectionString.value -o tsv)"
dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet run --file demo.cs -- --help
dotnet restore test_demo.cs
dotnet build test_demo.cs --no-restore
dotnet run --file test_demo.cs
# Live inference and Azure Monitor export (not part of offline validation):
dotnet run --file demo.cs
```

`principalType=ServicePrincipal` is available for a workload identity; use its
**service principal object ID**, not client ID or app-registration object ID.
The template grants Foundry User (formerly Azure AI User) for inference and
Monitoring Metrics Publisher for **Entra-authenticated telemetry ingestion**.
Application Insights local authentication is disabled. Allow RBAC propagation.
The deployment operator needs separate deployment/role-assignment privileges.
Both inference and telemetry use `DefaultAzureCredential`, which can select
configured environment/workload/managed credentials before Azure CLI. Ensure
the selected identity is the principal granted both roles; `az login` alone
does not override other configured credentials.
The template serializes model deployment and project creation: parallel writes
to the same Foundry account can fail with ARM `RequestConflict`.

Root runner contracts: `endpoint`, `deployment`, `projectEndpoint` are standard.
Additional mapping: `applicationInsightsConnectionString` →
`APPLICATIONINSIGHTS_CONNECTION_STRING`. The demo also reads that output from the
root runner's own `.azure/deployment.json` when the environment variable is absent.
The connection string identifies the telemetry destination; never print tokens.

## Inspect the evidence

The default runs are `normal`, `slow` (two seconds), and `failed`. The tool failure
is intentional and recovered: the tool span is ERROR, the model still summarizes
the unavailable result, and the parent is OK with `workshop.recovered_tool_failure`.
An actual model failure is fatal and marks both model and orchestration ERROR.
Printed `request_id` maps to `workshop.request_id`; printed `trace_id` maps to
Application Insights `operation_Id`. Model spans include response ID and token
usage when returned. Span timestamps provide durations.

Open the Application Insights resource (`applicationInsightsResourceId` output)
→ Transaction search / Logs. Wait several minutes for ingestion. In its
Application Insights Logs scope:

```kusto
union requests, dependencies
| where timestamp > ago(30m)
| where operation_Id == "<printed trace_id>"
| project timestamp, name, duration, success, operation_Id,
          id, operation_ParentId, customDimensions
| order by timestamp asc
```

Compare three trace IDs. `pricing-tool` dominates the slow run; the failed run has
`success=false` and `customDimensions["workshop.error_type"]=SyntheticToolFailure`.
The standard OTel `error.type` is also set in process, but Azure Monitor's exporter
reserves that attribute and doesn't retain it as a custom dimension.
A model error instead appears on
`chat workshop-summary`; parentage separates it from tool and orchestration issues.
In workspace scope use `AppRequests`/`AppDependencies` and `OperationId` instead.
Viewing logs needs Log Analytics Reader on the workspace (or equivalent inherited
permissions). The template does **not** link Application Insights to the Foundry
portal's Traces blade; use Application Insights directly, or connect it in
Foundry separately. This avoids promising portal correlation not configured here.

## Safety, validation, limitations, costs

Only allowlisted attributes are recorded. No prompts, completions, tool arguments,
tool results, exception messages/events, request headers, or credentials are
exported. No auto-instrumentation is enabled, so third-party HTTP instrumentation
cannot silently collect content. Inputs are synthetic. Do not add real customer
data. Sensitive-data recording environment switches are not consumed by this
manual instrumentation. Exporter offline storage is disabled.
The exporter receives this demo's `TracerProvider` explicitly so its resource
metadata does not depend on an unrelated or uninitialized global provider.

```bash
dotnet run --file test_demo.cs
az bicep build --file infra/main.bicep --stdout > /dev/null
```

Offline tests use an in-memory ActivityListener and mocked model requests: parentage,
usage, slow call, intentional tool ERROR, and secret-safe model failure. No cloud
calls occur in tests. A successful flush does **not** prove Azure accepted spans;
verify the query and exporter warnings. Exporter and GenAI semantic conventions
are preview/fast-moving. This is single-process instrumentation, not distributed
context propagation across a remote tool service.

Costs: three short model requests per run, Application Insights ingestion and
Log Analytics retention (30 days); no container hosting. Sampling is 100% for this
tiny lab. Model availability/quota and telemetry/RBAC propagation are regional.
The deployment uses GPT-4.1-mini `2025-04-14`, GlobalStandard capacity 10;
processing may leave the account region. Public endpoints are a workshop default.

## Cleanup

```bash
az group delete -n lab18-tracing --yes --no-wait
```

Delete the root runner's actual resource group instead if used. Remove local
`.azure`/`.venv` state only after recording any evidence you want to keep.

## Official API grounding

- [Configure tracing and privacy](https://learn.microsoft.com/azure/foundry/observability/how-to/trace-agent-framework)
- [Azure Monitor custom instrumentation](https://learn.microsoft.com/azure/azure-monitor/app/opentelemetry-add-modify)
- [Azure Monitor OpenTelemetry exporter for .NET](https://learn.microsoft.com/dotnet/api/azure.monitor.opentelemetry.exporter.azuremonitorexporterextensions.addazuremonitortraceexporter)

The lab uses explicit OTel spans plus `AzureMonitorTraceExporter`, not a fictional
Foundry tracing API. No resources are deployed as part of offline validation.
