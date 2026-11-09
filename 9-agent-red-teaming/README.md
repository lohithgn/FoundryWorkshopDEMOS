# Microsoft Foundry - Agent red teaming

The infrastructure deploys the model before the project to avoid concurrent
Foundry account-child writes failing with `RequestConflict`.

Run one small **Foundry AI Red Teaming Agent** baseline scan against a local
workshop tutor backed by Foundry inference. The managed service generates a
test objective, invokes the actual agent callback, and evaluates the result.
The script prints the scorecard and saves the report; no attack examples are
embedded in this repository.

## Deploy

Requires **Python 3.10-3.13** (the redteam/PyRIT extra is larger than other demos),
Azure CLI with Bicep, an Azure user with resource creation and role-assignment
permissions, and access to the preview AI Red Teaming service in a supported
region. Confirm regional availability before presenting; East US 2 is the
template's default, not a guarantee of subscription access.

Bicep creates one Foundry account, project, `gpt-4.1-mini` deployment
(`2025-04-14`, Global Standard), presenter RBAC, and project identity RBAC.
It disables keys and uses public endpoints. No hosted agent or UI is needed.

Run from this folder:

```bash
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
RESOURCE_GROUP="rg-foundry-agent-red-teaming-demo"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group "$RESOURCE_GROUP" --name demo \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location=eastus2
export AZURE_OPENAI_ENDPOINT="$(az deployment group show -g "$RESOURCE_GROUP" \
  -n demo --query properties.outputs.endpoint.value -o tsv)"
export FOUNDRY_PROJECT_ENDPOINT="$(az deployment group show -g "$RESOURCE_GROUP" \
  -n demo --query properties.outputs.projectEndpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show -g "$RESOURCE_GROUP" \
  -n demo --query properties.outputs.deployment.value -o tsv)"
```

## Run and present

```bash
python3 -m venv .venv
source .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py
# For another run, choose a fresh report path:
python demo.py --output .azure/redteam-second-run
```

Only one `HateUnfairness` objective and baseline direct testing are selected to
bound the demonstration. No encoding/jailbreak strategy expansion is requested.
The target is a real system-prompted tutor agent, not a fixed refusal callback.
The scan can still take several minutes and make multiple service requests.

Use only your own authorized test resource. Service-generated tests and the full
report can include disturbing safety-test text: do not project raw transcripts
to an unprepared audience or commit reports. The script prints only the scorecard,
but SDK logging may still contain test text. The `.azure/` folder is Git-ignored.
Reports and SDK logs are saved in a new `.azure/redteam-<unique-id>/` directory
on each run, with the returned result in `report.json`. The script refuses to reuse
an existing output directory. One objective in one category
is an illustration, not proof of safety or coverage of all risks.

## Validate, costs, and cleanup

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
python -m unittest discover -s . -p 'test_*.py' -v
az group delete --name "$RESOURCE_GROUP" --yes
```

Target inference and red-team service usage can incur charges. Offline tests
never generate attack text or call Azure. A missing result/report/scorecard and
target inference failures raise errors. For 403, check both identity permissions
and RBAC propagation; skipped/errored evaluations also fail explicitly.
For service/region errors, verify preview access rather
than substituting a local scan.

- [Official SDK, regional prerequisites, and scorecard](https://learn.microsoft.com/azure/foundry/how-to/develop/run-scans-ai-red-teaming-agent)
- [Feature F009](../BACKLOG.md#f009---microsoft-foundry-agent-red-teaming)
