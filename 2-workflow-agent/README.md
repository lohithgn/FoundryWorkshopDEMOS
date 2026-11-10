# Microsoft Foundry - Workflow Agent

The infrastructure deploys the model before the project to avoid concurrent
Foundry account-child writes failing with `RequestConflict`.

A 3-minute terminal demo: a **writer** drafts a workshop invitation, then an
**editor** improves it. Both outputs are printed so the handoff is visible.
Microsoft Agent Framework runs the sequential workflow locally; Microsoft Foundry
runs the model inference. This is not a portal-authored or hosted workflow.

> **Migration transition:** Use the .NET 10 commands below. Original Python files
> and requirements are temporarily retained unchanged for the root runner while
> issue #1 integrates the lab conversions; they are not the current lab commands.

## Infrastructure and prerequisites

The standalone Bicep template creates a Foundry account, project, `gpt-4.1-mini`
Global Standard deployment (`2025-04-14`), and Foundry User role for the presenter.
API keys are disabled; authentication uses the same interactive `az login` user.

Requires the .NET 10 SDK, Azure CLI with Bicep, model access/quota in **East US 2**,
and permissions to create resources and assign roles. Contributor alone is not
enough to assign roles. Public endpoints and Global Standard processing make
this a workshop setup, not a production networking or data-residency design.

## Deploy

Run from this folder in Bash/Zsh:

The lab-local .NET deploy helper is an alternative to the manual steps below. It
creates billable resources, runs the offline tests, then runs live inference; use
it only with separate authorization. `--help` is safe and performs no Azure calls.

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
RESOURCE_GROUP="rg-foundry-workflow-agent-demo"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group "$RESOURCE_GROUP" \
  --name workflow-agent-demo --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location=eastus2

export FOUNDRY_PROJECT_ENDPOINT="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" --name workflow-agent-demo \
  --query properties.outputs.projectEndpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" --name workflow-agent-demo \
  --query properties.outputs.deployment.value -o tsv)"
```

If Graph access is restricted, use your administrator-provided user object ID.
Model name, version, deployment name, and capacity are Bicep parameters if the
default model is unavailable in your subscription. Match the name and version.

## Run

```bash
dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet run --file demo.cs
dotnet run --file demo.cs -- --prompt "Invite developers to a 30-minute beginner Python workshop."
```

Alternatively, use `--project-endpoint` and `--deployment`. To deploy and run
**all demos**, use the [root runner](../README.md).

### Presenter talk track

1. Show the .NET `WorkflowBuilder` and its writer-to-editor edge.
2. Run it and compare **Writer draft** with **Editor final**.
3. Explain that ordering and handoff are deterministic, while the wording is not.
   Both agents use the same deployed model; no second model is needed.

There are two model requests per run, with usage charges. Nothing is booked,
sent, or published. The workflow and agents exist only in the local process.

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

Tests are offline; Azure deployment and inference require a separate live run.
For 403, check the signed-in identity and allow several minutes for RBAC
propagation. For 404, use **projectEndpoint**, not the account's OpenAI endpoint,
and check the deployment name. For deployment/quota errors or 429, check regional
model availability/quota; do not retry continuously. Empty stages and service
errors fail explicitly rather than producing a substitute answer.

## Cleanup

Delete only the dedicated resource group:

```bash
az group delete --name "$RESOURCE_GROUP" --yes
```

- [Agent Framework sequential workflows](https://learn.microsoft.com/agent-framework/workflows/orchestrations/sequential?pivots=programming-language-csharp)
- [Foundry workflow migration guidance](https://learn.microsoft.com/azure/foundry/agents/how-to/workflow)
- [Feature F002](../BACKLOG.md#f002---microsoft-foundry-workflow-agent)
