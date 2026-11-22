# Microsoft Foundry - Content Safety

A 2-minute demo: send workshop text to **Azure AI Content Safety**, print severity
for Hate, SelfHarm, Sexual, and Violence, and apply an explicit ALLOW/BLOCK policy.
This is a standalone Content Safety resource, a capability available within the
Foundry ecosystem, not the model's built-in inference content filter.

> **Migration transition:** Use the .NET 10 commands below. Original Python files
> and requirements are temporarily retained unchanged for the root runner while
> issue #1 integrates the lab conversions; they are not the current lab commands.

## Deploy

Requires the .NET 10 SDK, Azure CLI with Bicep, an interactive Azure user with resource
creation and role-assignment permissions, and Content Safety availability in
East US 2 (or change `location`). The template creates one S0 Content Safety
account with a custom subdomain and Cognitive Services User role for the
presenter. No model deployment is needed. API keys are disabled.

Run from this folder:

Alternatively, the lab-local .NET deploy helper creates billable resources, runs
the offline tests, and performs a live Content Safety analysis. Use only with
separate authorization; `--help` is safe and performs no Azure calls.

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
RESOURCE_GROUP="rg-foundry-content-safety-demo"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group "$RESOURCE_GROUP" --name demo \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location=eastus2
export AZURE_OPENAI_ENDPOINT="$(az deployment group show -g "$RESOURCE_GROUP" \
  -n demo --query properties.outputs.endpoint.value -o tsv)"
```

The shared endpoint variable contains a **cognitiveservices.azure.com** URL here;
it is passed to `ContentSafetyClient`, not an OpenAI client.

## Run and present

```bash
dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet run --file demo.cs
dotnet run --file demo.cs -- --text "Thank you for helping me learn Python." --threshold 4
```

The default is deliberately benign. Use only approved, non-sensitive test text
if presenting other categories. The service's four-level scale is **0, 2, 4, 6**;
the app blocks at or above the selected threshold. BLOCK is an application
decision, not an SDK error, so both ALLOW and BLOCK are successful analyses.
An ALLOW does not guarantee text is safe for every audience or use case.
Offline tests prove both sides of the threshold without including harmful text.

## Validate, costs, and cleanup

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
az group delete --name "$RESOURCE_GROUP" --yes
```

Requests are billed against S0; this is a public-endpoint workshop resource,
not a production networking design. For 403, check the Azure CLI identity and
allow for RBAC propagation. Missing categories, invalid scores, and service
failures are errors, never substituted with an ALLOW.

- [Content Safety REST API (2024-09-01) and Entra authentication](https://learn.microsoft.com/rest/api/contentsafety/text-operations/analyze-text?view=rest-contentsafety-2024-09-01)
- [Feature F006](../BACKLOG.md#f006---microsoft-foundry-content-safety)
