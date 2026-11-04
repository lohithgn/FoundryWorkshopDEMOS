# Microsoft Foundry - Model Router

A short terminal demo: send three different prompts to **one endpoint** and show
which underlying model actually answered each request. Model router chooses a
model based on the prompt; the Python code never chooses an underlying model.
Allow about 3-5 minutes to present after provisioning.

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

- Python 3.10+; Azure CLI with Bicep (`az bicep version`).
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
command, run `python3 deploy.py --demo 1` from the repository root. Omitting
`--demo 1` selects every discovered lab, including billable training/evaluation
and labs with additional tenant prerequisites. See the
[repository runner instructions](../README.md) for subscription selection, logs,
and all-demo deployment. The manual steps below remain available.

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
python3 -m venv .venv
source .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py

# Optional: try your own prompt.
python demo.py --prompt "Explain model routing to a Python developer in two sentences."
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
python -m unittest discover -s . -p 'test_*.py' -v
```

Tests mock authentication and service calls; they do not deploy resources or
incur inference charges. Live deployment/inference is a separate smoke test.

## Cleanup

After the demo, delete only the dedicated group you created:

```bash
az group delete --name "$RESOURCE_GROUP" --yes
```

## References

- [Use model router](https://learn.microsoft.com/azure/foundry/openai/how-to/model-router)
- [Model router concepts and supported regions](https://learn.microsoft.com/azure/foundry/openai/concepts/model-router)
- [Tracked feature F001](../BACKLOG.md#f001---microsoft-foundry-model-router)
