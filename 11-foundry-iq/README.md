# Microsoft Foundry - Super-basic Foundry IQ

A 3-minute terminal example of the **knowledge layer behind Foundry IQ**:
create an index containing three tiny workshop fact sheets, reference it from a
knowledge source and knowledge base, then ask a compound question. Azure AI Search
plans retrieval, searches, and synthesizes an answer with references.

This follows [Microsoft Learn's agentic-retrieval quickstart](https://learn.microsoft.com/azure/search/search-get-started-agentic-retrieval),
using its REST API pattern with a much smaller, synthetic dataset. The three
documents are included directly in the script, so no downloads are needed.
It uses the real managed knowledge-base API, **not a local RAG imitation**.

> **Migration note:** Python sources and requirements are temporarily retained
> unchanged for the repository-wide runner. Use the .NET 10 file-app commands;
> Python runner references are transitional until issue #1 completes integration.

Restore and build each .NET 10 file app from this folder:

```bash
dotnet restore demo.cs && dotnet build demo.cs
dotnet restore deploy_lab.cs && dotnet build deploy_lab.cs
dotnet restore cleanup.cs && dotnet build cleanup.cs
dotnet restore test_demo.cs && dotnet build test_demo.cs
```

To keep it small, this example uses a text index and semantic ranker rather than
the quickstart's vector index/NASA corpus. No embedding model, storage account,
hosted agent, Foundry portal connection, or UI is needed. It demonstrates the
agentic-retrieval foundation of IQ, not SharePoint permissions or a full enterprise
IQ deployment.

## Deploy

Requires the .NET 10 SDK, Azure CLI with Bicep, an interactive Azure user with resource
creation and role-assignment permissions, a region supporting agentic retrieval,
and `gpt-4.1-mini` quota. The template deploys:

- One keyless Foundry account with project management enabled and a
  system-assigned managed identity, in **East US 2** by default.
- A **`foundry-iq-demo` project** with its own system-assigned managed identity,
  followed by `gpt-4.1-mini` (`2025-04-14`, Global Standard).
  Account -> project -> model creation is serialized to avoid overlapping
  account child operations. Model deployments remain account-scoped, not
  project child resources.
- One **Basic Azure AI Search** service with managed identity and semantic ranker,
  in **West US 2** by default.
- Search Service Contributor, Search Index Data Contributor, and Search Index
  Data Reader for the presenter.
- Cognitive Services User on Foundry for the search service's managed identity.
- Foundry User (role ID `53ca6127-db72-4b80-b1b0-d745d6d5456d`,
  formerly Azure AI User) on the project for the presenter.

Run from this folder in Bash/Zsh:

```bash
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
az provider register --namespace Microsoft.Search --wait
RESOURCE_GROUP="rg-foundry-foundry-iq-demo"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create -g "$RESOURCE_GROUP" -n demo \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location=eastus2 searchLocation=westus2
export AZURE_OPENAI_ENDPOINT="$(az deployment group show -g "$RESOURCE_GROUP" -n demo \
  --query properties.outputs.endpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show -g "$RESOURCE_GROUP" -n demo \
  --query properties.outputs.deployment.value -o tsv)"
export AZURE_SEARCH_ENDPOINT="$(az deployment group show -g "$RESOURCE_GROUP" -n demo \
  --query properties.outputs.searchEndpoint.value -o tsv)"
export FOUNDRY_PROJECT_ENDPOINT="$(az deployment group show -g "$RESOURCE_GROUP" -n demo \
  --query properties.outputs.projectEndpoint.value -o tsv)"
```

The template also outputs `projectResourceId`. Both deployment runners already
map `projectEndpoint` to `FOUNDRY_PROJECT_ENDPOINT` and record all outputs, so
no shared-runner changes are needed. The demo displays the project endpoint
when supplied through that variable or `--project-endpoint`. It remains optional
for older explicitly configured Search-only runs.

The project is the Foundry workspace, **not** the Search retrieval endpoint.
The index, knowledge source, and knowledge base still live in Azure AI Search;
Search's managed identity still calls the account's model endpoint. This lab
does not create a project-to-Search connection or a portal agent; see lab 27
for that fuller workflow. Creating a project does not fix a downstream model 408.

### Updating an existing deployment

Redeploy the template to the same group/account to enable project management,
add account identity and the project, and retain the existing model/Search names.
Re-export the outputs afterward. No delete, restore, purge, or random rename is
required for this update. Group cleanup also removes the new project.

This account/project/model structure is supported, but deployment still depends
on Foundry project availability, model quota, Search regional capacity, Azure
Policy, and role-assignment permissions. Do not interpret a successful local
build as proof that your subscription will accept the deployment.

The knowledge-base APIs use **`2026-08-01-preview`**, matching the linked Learn
quickstart's REST reference. Confirm preview/region availability before presenting.
Both services use public endpoints and Entra ID, not API keys.

The independent `searchLocation` parameter avoids Search capacity constraints
without moving Foundry to a region lacking model quota. Earlier deployments
were blocked by Search capacity in East US 2 and Sweden Central. West US 2
supports both agentic retrieval and semantic ranker. Capacity is not guaranteed;
choose another supported Search region if Azure rejects provisioning.
An existing Search service cannot be moved to a different region in place:
use a new dedicated resource group when changing its region.

The root runner supports the same override. This Python command is transitional
until the shared runner in issue #1 is converted:

```bash
python3 deploy.py --demo 11 --location eastus2 --search-location westus2
```

Run that command from the repository root. The lab-specific .NET runner
also uses the template's West US 2 Search default; its `--location` changes
the Foundry/resource-group region, not Search.

## Run and present

```bash
dotnet restore demo.cs
dotnet build demo.cs
dotnet run --file demo.cs --
dotnet run --file demo.cs -- --question "Is the workshop free, and do I need prior AI experience?"
```

The standalone .NET lifecycle entry points are:

```bash
dotnet restore deploy_lab.cs
dotnet build deploy_lab.cs
dotnet run --file deploy_lab.cs -- --location eastus2
dotnet restore cleanup.cs
dotnet build cleanup.cs
# Destructive; only with separate authorization:
dotnet run --file cleanup.cs --
```

1. Show the three fact sheets and the index -> knowledge source -> knowledge base
   relationship in the script, and the separate Foundry account/project/model
   hierarchy in the infrastructure.
2. Ask when the workshop is, how long it lasts, and what to bring.
3. Show the actual answer, retrieval activity/subqueries, and source references.

The script creates/updates only its fixed workshop names, checks every document
upload, and waits up to 60 seconds for index visibility. Reruns reuse the same
index/source/base; resources remain available for inspection. The API decides
how to plan a query: multiple subqueries are not guaranteed for every question.
Missing grounding evidence or reported retrieval errors fail explicitly.

### Historical live verification on 2026-10-26 (before adding the project)

Deployed to `rg-foundry-foundry-iq-demo` with Foundry in East US 2 and Basic
Search in West US 2. Both resources reached `Succeeded`; Search is running with
one replica, one partition, managed identity, and API keys disabled.

- Created the real index, uploaded all three documents, and configured the
  managed knowledge source and answer-synthesizing knowledge base.
- Default question returned Friday at 10:00, 45 minutes, and a laptop with
  Python 3.11/Azure CLI, citing the schedule and equipment documents.
- Custom question returned free attendance, registration before Thursday,
  and no required AI experience, citing the cost and equipment documents.
- Observed actual query planning, three/two Search subqueries respectively,
  and answer synthesis, without reported activity errors.
- Checked every returned source document against the exact synthetic input
  and verified that answer citations resolve to the required documents.
- Independent API readback confirmed three indexed documents, the source/base,
  and the cross-region Foundry model configuration.
- Six lab tests, including the compiled regional deployment contract, and
  all 27 root deployment-runner tests passed. Bicep build/lint also passed.

Evidence is retained locally in ignored `.azure/deployment.json`,
`.azure/live-default.log`, `.azure/live-custom.log`, and
`.azure/live-testing-summary.json`. Resources remain deployed and billable.
The earlier blocker was regional Search provisioning capacity, not a broken
knowledge-base API or missing model access. Separating regions fixed this
deployment; it does not reserve capacity for future workshop deployments.
This historical run does not verify the new project-enabled template.

## Validate and clean up

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
```

Offline tests check account/project identities, project RBAC and endpoint
outputs, account -> project -> model dependencies, independent Search region,
and that displaying a project does not redirect retrieval away from Search.

Use the following offline harness and safe smoke paths:

```bash
dotnet restore test_demo.cs
dotnet build test_demo.cs
dotnet run --file test_demo.cs
dotnet run --file demo.cs -- --help
dotnet run --file deploy_lab.cs -- --help
dotnet run --file cleanup.cs -- --help
```

The 2026-10-26 live-verification record above predates this .NET implementation;
it is historical Python evidence, not live validation of the .NET app or the
project-enabled template. No live Azure validation is claimed for this migration.

**Basic Search has ongoing charges while provisioned**, even with no queries.
Agentic retrieval and planning/answer synthesis can also incur usage charges;
the semantic free allowance is limited. For 403, allow both presenter and search
identity RBAC to propagate. For 404/API-version errors, verify preview availability
and endpoint names; don't replace the managed pipeline with local search.

- [Learn quickstart and source sample links](https://learn.microsoft.com/azure/search/search-get-started-agentic-retrieval)
- [Foundry IQ overview](https://learn.microsoft.com/azure/foundry/agents/concepts/what-is-foundry-iq)
- [Search region and agentic-retrieval support](https://learn.microsoft.com/azure/search/search-region-support)
- [Enable Foundry projects on an existing account](https://learn.microsoft.com/azure/foundry/how-to/upgrade-azure-openai)
- [Feature F011](../BACKLOG.md#f011---microsoft-foundry-foundry-iq)
