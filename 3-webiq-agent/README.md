# Microsoft Foundry - Simple WebIQ Agent

A short terminal comparison: a Foundry prompt agent searches the public web,
answers a question about model router, and prints **source titles and URLs**.
The script then runs the same model, instructions, and prompt **without the
Web Search tool**, displaying the answer and zero search calls/citations.
Here, the historical workshop label "WebIQ" means Foundry's built-in **Web Search**
tool, not the separate [Microsoft Web IQ APIs](https://learn.microsoft.com/web-iq/resources/faq)
or a private enterprise search integration.

> **Migration transition:** Use the .NET 10 commands below. Original Python files
> and requirements are temporarily retained unchanged for the root runner while
> issue #1 integrates the lab conversions. Historical live results below are from
> the Python version and are not .NET live validation.

## Infrastructure and prerequisites

Bicep creates a Foundry account, project, `gpt-5-mini` Global Standard deployment
(`2025-08-07`), and a Foundry User role for the presenter. The C# app uses the
Foundry REST API to create and invoke a temporary server-side prompt-agent version
with native Web Search, then repeats with an otherwise identical definition using
no tools. Each version is deleted even on inference failure, before the next
version is created. A failure stops the comparison.
No separate Bing resource, connection, or API key is required for this path.
The template serializes model and project creation to avoid Azure `RequestConflict`
from concurrent writes to the same account.

Requires the .NET 10 SDK, Azure CLI with Bicep, model access/quota in **East US 2**,
and permissions to create resources and assign roles. Your organization must
permit Web Search; access policies can disable it independently of deployment.
Contributor alone cannot assign roles.

**Costs and data:** each full comparison makes two model requests, with web
search enabled only in the first. Model requests and web search incur charges. Web Search uses
Grounding with Bing, whose terms apply; data can leave Azure compliance and
geographic boundaries, and the Microsoft DPA does not apply to that data.
Use only public, non-sensitive questions. Public endpoints and Global Standard
processing are intentional workshop simplifications.

## Deploy

From this folder in Bash/Zsh:

The lab-local .NET deploy helper is an alternative to the manual steps below. It
creates billable resources, runs the offline tests, then runs live Web Search and
model requests; use it only with separate authorization. `--help` is safe.

```bash
dotnet run --file deploy_lab.cs -- --help
dotnet run --file deploy_lab.cs -- --subscription YOUR_SUBSCRIPTION_ID --location eastus2
dotnet run --file cleanup.cs -- --help
dotnet run --file cleanup.cs
```

Cleanup prompts for the full recorded resource-group name; use `--yes` only for
separately authorized unattended cleanup.

```bash
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
RESOURCE_GROUP="rg-foundry-webiq-agent-demo"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group "$RESOURCE_GROUP" \
  --name webiq-agent-demo --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location=eastus2

export FOUNDRY_PROJECT_ENDPOINT="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" --name webiq-agent-demo \
  --query properties.outputs.projectEndpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" --name webiq-agent-demo \
  --query properties.outputs.deployment.value -o tsv)"
```

If Graph is unavailable, supply your user object ID directly. Model name/version,
deployment name, and capacity are parameters for subscriptions where the default
model is unavailable. Select a model that supports Web Search.

## Run

```bash
dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet run --file demo.cs
dotnet run --file demo.cs -- --prompt "Search Microsoft Learn for Foundry Agent Service and summarize its purpose with citations."
dotnet run --file demo.cs -- --debug-response
```

`--project-endpoint` and `--deployment` can replace environment variables.
The default deployment name is `gpt-5-mini`. Redeploy the Bicep template to create
the new model deployment; changing the C# deployment name alone does not
upgrade an existing `gpt-4.1-mini` deployment. Refresh the environment variables
from the deployment outputs afterward, especially if a previous session set
`AZURE_OPENAI_DEPLOYMENT` to the old name.
The [root runner](../README.md) is temporarily Python during the staged migration;
issue #1 owns converting it. It selects all discovered labs if `--demo` is
omitted; it is not limited to the original four demos.

### Presenter talk track

1. Show that the native Foundry prompt-agent definition enables Web Search and
   that the request sets `tool_choice="required"`.
2. Run the example. Under **WITH WebSearchTool()**, point out the actual
   web-search call count and structured source citations.
3. Under **WITHOUT WebSearchTool()**, show the otherwise identical definition
   with `tools=[]`. This request uses `tool_choice="none"` because requiring a
   tool when none exists would test an invalid configuration rather than model
   behavior. No previous response or conversation is passed between requests,
   so the second run does not receive the first answer or its search results.
4. Point out the zero search calls and structured citations. The model may
   answer from prior knowledge or explain that it cannot browse. It may also
   print plausible URLs; those are not evidence of retrieval. Asking for a
   search in instructions does not grant the ability to search.
5. Explain that citations provide provenance, not a guarantee of truth. Visit
   the source and verify important claims; search results and wording can change.
   This comparison demonstrates retrieval, not that the no-tool answer must be
   wrong or that the two answers must differ in their factual claims.

The with-tool run requires completed searches, text, and URL citations. The
no-tool run requires completed text and rejects unexpected web-search calls or
structured URL citations. It explicitly labels its answer as not web-retrieved.
Neither run substitutes a made-up answer on failure.

## Validate and troubleshoot

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
dotnet restore test_demo.cs
dotnet build test_demo.cs --no-restore
dotnet run --file test_demo.cs
dotnet restore deploy_lab.cs
dotnet build deploy_lab.cs --no-restore
dotnet run --file deploy_lab.cs -- --help
dotnet restore cleanup.cs
dotnet build cleanup.cs --no-restore
dotnet run --file cleanup.cs -- --help
```

Tests use offline response fixtures. They do not prove
subscription access; perform a separate live smoke test before presenting.
Every response prints its status, output item types, search-call count/statuses,
and structured URL-citation count before validation, including on failure.
`--debug-response` additionally prints the full response JSON. Use only public,
non-sensitive prompts with this flag; captured output may contain response text
and source data. Raw JSON is not printed by default.

If grounding validation fails in the with-tool run:

- **Citations but no search-call output:** inspect the Foundry agent trace and raw
  response for how this API surfaces tool execution. Do not assume the model did
  not search solely because the response lacks `web_search_call`.
- **Search-call output but no citations:** inspect message annotations. Plain-text
  URLs do not count as structured `url_citation` annotations.
- **Neither:** compare the same model and Web Search configuration in the Foundry
  playground and check tool availability and organizational policy. Try a public,
  time-sensitive question, such as the latest Microsoft Foundry announcement and
  its publication date, explicitly requesting sources.

These diagnostics do not relax the with-tool checks: completed search calls,
nonempty answer text, and structured citations are still required. Failed or
unfinished searches report their statuses and fail explicitly. Missing search
calls and citations are expected only for the no-tool run.

For 403, check RBAC propagation and organizational Web Search policy. For 404,
use the Bicep **projectEndpoint** and correct deployment name. For 429 or deployment
failure, check model quota and region. If cleanup fails, the error remains visible;
remove the named agent version in Foundry or delete the dedicated group.

## Live verification (2026-10-26)

The results below describe the original with-tool-only demo. The new two-run
comparison needs a separate live smoke test; offline tests mock both responses.

Redeployed in East US 2 under `rg-foundry-webiq-agent-live-20261026` using
`gpt-5-mini`. The first deployment reproduced `RequestConflict`; serializing the
project after the model fixed the deployment.

| Actual request | Response | Completed web-search calls | Structured URL citations |
| --- | --- | --- | --- |
| Default model-router question | Completed; exit 0 | 1 | 3 |
| Custom Agent Service question | Completed; exit 0 | 1 | 5 |

Parsed the actual service response JSON to verify completed `web_search_call`
items and `url_citation` annotations; plain-text URLs were not accepted instead.
Independent `get_version` calls returned HTTP 404 for both temporary agents after
cleanup. Nine offline tests and Bicep build/lint passed separately.

Ignored evidence is retained in `.azure/live-default-2026-10-26.log`,
`.azure/live-custom-2026-10-26.log`, and `.azure/live-2026-10-26.json`.
The previous lab deployment had been deleted; the new group remains deployed.
Its state is recorded for the staged Python cleanup helper; use the .NET
`cleanup.cs` only for records created by the C# deploy helper.

## Cleanup

Both temporary agent versions are deleted automatically; the account/project/model remain:

```bash
az group delete --name "$RESOURCE_GROUP" --yes
```

- [Foundry Web Search setup, terms, and limitations](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/web-search?pivots=python)
- [Feature F003](../BACKLOG.md#f003---microsoft-foundry-simple-webiq-agent)
