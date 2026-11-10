# Prompt-injection trust boundaries

Two synthetic sources contain a workshop start time and a harmless instruction
to output `UNRELATED_MARKER`: one represents a document, the other a tool result.
Compare actual raw model behavior with a protected path that projects only an
allowed factual field, constrains JSON output, validates the result, and exposes
no executable tools. The baseline may already resist the override; results are
observations, not a guaranteed attack success or safety certification.

## .NET 10 file-based runner

`demo.cs` is the primary runner. It uses Azure CLI bearer tokens and the supported
Content Safety and Azure OpenAI REST APIs; no NuGet libraries are required. The retained
`demo.py`, `test_demo.py`, and `requirements.txt` are transitional Python references and
remain unchanged.

From this directory, restore/build both file-based apps and run the live demo or offline
regression harness:

```bash
mkdir -p .dotnet-home
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet restore demo.cs --configfile NuGet.Config
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet build demo.cs --no-restore
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet restore test_demo.cs --configfile NuGet.Config
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet build test_demo.cs --no-restore
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file demo.cs --no-build --no-restore -- --help
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file demo.cs --no-build --no-restore
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file demo.cs --no-build --no-restore -- --without-shields
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file test_demo.cs --no-build --no-restore
```

## Deploy and run

Requires Azure CLI/Bicep, resource/role-assignment permissions,
`gpt-4.1-mini` quota, structured outputs support, and Content Safety Prompt Shields
availability in your selected region. Uses Entra ID, public endpoints, no keys. Python
3.10+ and `requirements.txt` apply only to the transitional Python path below.

```bash
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
RG=rg-foundry-prompt-injection-demo
az group create -n "$RG" -l eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create -g "$RG" -n demo --template-file infra/main.bicep \
  --parameters principalId="$(az ad signed-in-user show --query id -o tsv)"
export AZURE_OPENAI_ENDPOINT="$(az deployment group show -g "$RG" -n demo \
  --query properties.outputs.endpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show -g "$RG" -n demo \
  --query properties.outputs.deployment.value -o tsv)"
export CONTENT_SAFETY_ENDPOINT="$(az deployment group show -g "$RG" -n demo \
  --query properties.outputs.contentSafetyEndpoint.value -o tsv)"
mkdir -p .dotnet-home
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet restore demo.cs --configfile NuGet.Config
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet build demo.cs --no-restore
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file demo.cs --no-build --no-restore
```

The C# run shows the two raw sources, Prompt Shields' actual
classifications, raw baseline
answers, and validated protected answers. `--without-shields` explicitly omits
the classifier for comparison; it does not claim a successful shield analysis.
Classification failures are fatal rather than treated as "safe."
Azure inference guardrails may block the raw baseline with `content_filter`
before an answer exists. This is explicitly reported as a blocked observation,
not a successful model response; the protected factual projection is still
tested separately. Other API errors propagate. Do not disable service filters
to force an instruction-override demonstration.

The classifier is an independent observable layer; this lab always uses the
factual projection rather than trusting a negative classification. Model output
must equal the projected fact, not merely parse as JSON. No booking/write tool
is available, so generated text cannot authorize an action. See lab 20 for an
application-enforced approval gate.

## Limits, tests, and cleanup

Projection works here because a toy record has one strictly defined field.
It is not a general sanitizer for arbitrary documents, nor proof that the
source's factual value is truthful. A production retrieval application needs
source authorization, provenance, application-specific validation, and ongoing
adversarial evaluation. Prompt Shields differs from content-category filtering;
false positives and false negatives remain possible.

```bash
HOME="$PWD/.dotnet-home" DOTNET_CLI_HOME="$PWD/.dotnet-home" dotnet run --file test_demo.cs --no-build --no-restore
python -m unittest discover -s . -p 'test_*.py' -v
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
az group delete -n "$RG" --yes
```

The C# harness covers the Azure CLI scope, Prompt Shields API version/request/response
contract, strict protected-model request, marker exclusion, unrelated API errors, and
content-filtered baseline output using mocked HTTP only. The Python harness is
transitional and is not required for C# validation.

The default attempts four model requests and two safety analyses; a filtered
request is not a successful generation. Actual service usage determines charges.
The template deploys GPT-4.1-mini `2025-04-14`, GlobalStandard capacity 10, plus
an S0 Content Safety account in East US 2 by default. It grants the presenter
Cognitive Services OpenAI User for inference and Cognitive Services User for
Content Safety. GlobalStandard processing may leave the account region.
Allow RBAC propagation after deployment. No tenant objects or durable source
data are created.

- [Prompt Shields quickstart](https://learn.microsoft.com/azure/ai-services/content-safety/quickstart-jailbreak)
- [Tool trust boundaries](https://learn.microsoft.com/azure/foundry/agents/concepts/tool-best-practice)
- [Backlog F022](../BACKLOG.md#f022---microsoft-foundry-prompt-injection-defenses)
