# Microsoft Foundry - Model Router

A short terminal demo: send three different prompts to **one endpoint** and show
which underlying model actually answered each request. Model router chooses a
model based on the prompt; the C# code never chooses an underlying model.
Allow about 3-5 minutes to present after provisioning.

> **Migration transition:** Use the .NET 10 commands below. Original Python files
> and requirements are temporarily retained unchanged for the root runner while
> issue #1 integrates the lab conversions; they are not the current lab commands.

## What gets deployed

- One Microsoft Foundry account (`AIServices`, S0).
- One `model-router` deployment, version `2025-11-18`, Global Standard, with the
  default Balanced routing mode and default eligible model pool.
- A Cognitive Services OpenAI User role assignment for the presenter.

No separate underlying model deployments or Foundry project are needed for this
account-level Chat Completions example. No storage, web server, or UI is created.
The public endpoint enables access from your laptop; API keys are disabled.
This is a public Azure workshop setup, not a production networking design.

## Prerequisites

- .NET 10 SDK; Azure CLI with Bicep (`az bicep version`).
- An Azure subscription with access and sufficient quota for Model Router in
  **East US 2** (the default). Regional availability, model access, and organization
  policies can still prevent deployment.
- Permission to create the resource group/account/deployment and assign roles.
  Contributor alone cannot assign roles; you also need an appropriate role such
  as Role Based Access Control Administrator at the deployment scope.
- An interactive user login. The template assigns a **User** role and the script
  deliberately uses `AzureCliCredential` so deployment and inference use your
  `az login` identity.

## 1. Deploy

To deploy, install dependencies, and run local tests plus this live demo in one
command, the transitional Python root runner can still be run as `python3 deploy.py --demo 1` from the repository root. Omitting
`--demo 1` selects every discovered lab, including billable training/evaluation
and labs with additional tenant prerequisites. See the
[repository runner instructions](../README.md) for subscription selection, logs,
and all-demo deployment. The manual steps below remain available.

The lab-local .NET helper is an alternative, billable path that also runs offline
tests and the live demo. Use it only when deployment and inference are authorized;
`--help` is safe and performs no Azure calls:

```bash
dotnet run --file deploy_lab.cs -- --help
dotnet run --file deploy_lab.cs -- --subscription YOUR_SUBSCRIPTION_ID --location eastus2
dotnet run --file cleanup.cs -- --help
dotnet run --file cleanup.cs
```

Cleanup prompts for the full recorded resource-group name; `--yes` skips that
confirmation and should be used only for separately authorized unattended cleanup.

Run in Bash or Zsh from this demo folder. Choose your subscription and use a
dedicated resource group; the cleanup command deletes everything in that group.

```bash
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait

RESOURCE_GROUP="rg-foundry-model-router-demo"
LOCATION="eastus2"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"

az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --name model-router-demo \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location="$LOCATION"

export AZURE_OPENAI_ENDPOINT="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" --name model-router-demo \
  --query properties.outputs.endpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" --name model-router-demo \
  --query properties.outputs.deployment.value -o tsv)"
```

The generated account name is deterministic for the resource group. Optionally
pass `accountName=<globally-unique-name>`, `capacity=10`, or
`deploymentName=<name>` to the same deployment command. If Graph access prevents
`az ad signed-in-user show`, get your user's object ID from your administrator.
Role assignments can take several minutes to propagate.

## 2. Run locally

```bash
dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet run --file demo.cs

# Optional: try your own prompt.
dotnet run --file demo.cs -- --prompt "Explain model routing to a Python developer in two sentences."
```

You can pass `--endpoint` and `--deployment` instead of environment variables.
The script sends three independent requests (simple, writing, and reasoning) and
prints each prompt, the response's **serving model**, client-observed elapsed
time, input/output/total tokens when supplied, finish reason, and answer.
It uses the supported Chat Completions API version `2024-10-21`; this is separate
from the deployed router's model version.

### Presenter talk track

1. Show that every request sets `model` to the same router **deployment name**.
2. Run the default prompts. Point at `Serving model`, taken from `response.model`,
   not guessed from the prompt. This is the service-reported identifier, not
   independent billing evidence or a guarantee of how every router/API version
   exposes its underlying model.
3. Run a custom prompt and explain that selection happens server-side. Repeated
   or different prompts **can select the same model**; this is not a failure.
4. Discuss the trade-off: one deployment can balance quality and cost across tasks,
   but this short demo does not prove savings or benchmark models.

Illustrative output only (actual model, timing, tokens, and wording vary):

```text
=== Simple ===
Prompt: What is the capital of France? Answer in one sentence.
Serving model: gpt-4.1-mini-2025-04-14
Elapsed: 1.23s (client-observed, including routing/network)
Tokens: input=29, output=8, total=37
Finish reason: stop
Answer:
Paris is the capital of France.
```

## Troubleshooting and costs

- **Deployment fails:** check model access, policy, quota, and version availability.
  List the available versions/SKUs with:
  `az cognitiveservices model list --location eastus2 --query "[?model.name=='model-router']"`.
  Capacity units and their rate limits are model-specific; don't assume they map
  to a fixed token-per-minute value. Reduce `capacity` only if your available
  quota permits it, or request quota/use another supported region.
- **401/403:** check `az account show`, use the same user who received the role,
  and allow time for RBAC propagation. Use the account's OpenAI endpoint, not a
  Foundry project endpoint.
- **404:** check both exported variables against the deployment outputs and wait
  for provisioning to finish.
- **429:** wait for the rate-limit window or obtain additional quota. The script
  disables automatic retries so throttling and failures are visible.
- **Timeout/refusal/empty response:** the script exits with an explicit error.
  It does not substitute an invented answer.
- Global Standard can process prompts globally. Use non-sensitive sample prompts.
  Requests incur model usage charges; token counts alone are not dollar costs.
  The eligible pool can change as Microsoft updates the router version.

## Local validation

From this folder, with the dependencies installed:

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

The deployment and cleanup helpers have safe `--help` checks; invoking deployment
creates billable resources and performs live inference. Tests are offline and do
not deploy resources or incur inference charges. Live validation is separate.

## Cleanup

After the demo, delete only the dedicated group you created:

```bash
az group delete --name "$RESOURCE_GROUP" --yes
```

## References

- [Use model router](https://learn.microsoft.com/azure/foundry/openai/how-to/model-router)
- [Model router concepts and supported regions](https://learn.microsoft.com/azure/foundry/openai/concepts/model-router)
- [Tracked feature F001](../BACKLOG.md#f001---microsoft-foundry-model-router)
