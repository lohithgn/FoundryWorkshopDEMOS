# Microsoft Foundry - Agent red teaming

The infrastructure deploys the model before the project to avoid concurrent
Foundry account-child writes failing with `RequestConflict`.

Run one small **Foundry AI Red Teaming Agent** baseline scan against a local
workshop tutor backed by Foundry inference. The .NET 10 file-based C# version
uses Foundry REST APIs and the Azure CLI for Entra tokens; it needs no NuGet
packages. Foundry generates the test objective, the tutor callback returns the
target response, and the Foundry evaluator scores it. No attack examples are
embedded in this repository.

## Prerequisites

Use the **.NET 10 SDK**, Azure CLI with Bicep, an Azure user with resource
creation and role-assignment permissions, and access to the preview AI Red
Teaming service in a supported region. Confirm regional availability before
presenting; East US 2 is the template's default, not a guarantee of subscription
access.

Bicep creates one Foundry account, project, `gpt-4.1-mini` deployment
(`2025-04-14`, Global Standard), presenter RBAC, and project identity RBAC.
It disables keys and uses public endpoints. No hosted agent or UI is needed.
The original Python files remain unchanged for staged migration.
**Transitional Python documentation:** Any Python commands retained below are
for the repository-wide runner only; they do not validate the .NET apps.

Run commands from this folder:

```bash
az login
az account set --subscription "<subscription-id>"
```

The standalone .NET file apps can be restored and built without Azure access:

```bash
dotnet restore demo.cs
dotnet build demo.cs
dotnet restore deploy_lab.cs
dotnet build deploy_lab.cs
dotnet restore cleanup.cs
dotnet build cleanup.cs
dotnet restore test_demo.cs
dotnet build test_demo.cs
dotnet run --file test_demo.cs
```

## Deploy and run

To deploy and run the live demonstration from the single-lab helper:

```bash
dotnet run --file deploy_lab.cs -- --subscription "<subscription-id>" --location eastus2
```

This helper runs the offline harness, Bicep build/lint, deploys a dedicated
resource group, then runs the live scan. It retains resources and writes
`.azure/lab-deployment.json`, `lab-tests.log`, and `demo.log`.

For manual deployment or an already deployed workshop, run:

```bash
RESOURCE_GROUP="rg-foundry-agent-red-teaming-demo"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
az provider register --namespace Microsoft.CognitiveServices --wait
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
dotnet run --file demo.cs
# For another run, choose a fresh report path:
dotnet run --file demo.cs -- --output .azure/redteam-second-run
```

Only one `HateUnfairness` objective and baseline direct testing are selected to
bound the demonstration. No encoding/jailbreak strategy expansion is requested.
The target is a real system-prompted tutor agent, not a fixed refusal callback.
The scan can still take several minutes and make multiple service requests.

Use only your own authorized test resource. Service-generated tests and the full
report can include disturbing safety-test text: do not project raw transcripts
to an unprepared audience or commit reports. The program prints only the
scorecard, but SDK/service logging may still contain test text. The `.azure/`
folder is Git-ignored. Reports are saved in a new `.azure/redteam-<unique-id>/`
directory on each run, with the returned result in `report.json`; the program
refuses to reuse an existing output directory. One objective in one category
is an illustration, not proof of safety or coverage of all risks.

## Validate, costs, and cleanup

These offline checks do not call Azure:

```bash
dotnet run --file test_demo.cs
dotnet run --file demo.cs -- --help
dotnet run --file deploy_lab.cs -- --help
dotnet run --file cleanup.cs -- --help
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
```

The following invalid-argument checks exit nonzero before any Azure operations:

```bash
dotnet run --file demo.cs -- --project-endpoint https://project.example --endpoint http://localhost
dotnet run --file deploy_lab.cs -- --prefix 'invalid prefix'
dotnet run --file cleanup.cs -- --invalid
```

Target inference and red-team service usage can incur charges. The offline
harness never generates attack text or calls Azure. A missing objective,
incomplete target response, missing evaluator score, or incomplete scan fails
explicitly. For 403, check identity permissions and RBAC propagation; for
service/region errors, verify preview access rather than substituting a local
scan.

To remove only the lab helper's recorded resource group:

```bash
dotnet run --file cleanup.cs
# Use only for unattended cleanup:
dotnet run --file cleanup.cs -- --yes
```

The cleanup helper verifies the recorded subscription and ownership tags,
prompts for the exact group name unless `--yes` is supplied, verifies deletion,
and retains local reports/logs. Manual deployments can be removed with:

```bash
az group delete --name "$RESOURCE_GROUP" --yes
```

- [Official SDK, regional prerequisites, and scorecard](https://learn.microsoft.com/azure/foundry/how-to/develop/run-scans-ai-red-teaming-agent)
- [Feature F009](../BACKLOG.md#f009---microsoft-foundry-agent-red-teaming)
