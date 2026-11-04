# 14 — Foundry fundamentals

Create a Foundry account, project, and GPT-4.1-mini deployment; use **Microsoft
Entra ID, never an API key**, for a first answer and a streamed answer with usage.
Default execution is noninteractive and makes real cloud calls.

## Prerequisites and availability

Python 3.10+, Azure CLI with Bicep, `az login`, an Azure subscription, and model
quota are required. The deploying identity needs resource creation and role
assignment rights (for example Contributor plus User Access Administrator).
Creating the restricted inference identity also requires permission to register
applications and create service principals in your Microsoft Entra tenant.
The deployer and inference identity are deliberately separate. The latter gets
**Cognitive Services OpenAI User**, scoped only to the primary account.
Do not remove the presenter's existing roles to make the negative probe fail.

The default is `eastus2`, GPT-4.1-mini version `2025-04-14`, GlobalStandard.
Check **your subscription's** model/version/SKU availability and quota first;
region support, access, and capacity are not guaranteed:

```bash
az cognitiveservices model list --location eastus2 -o table
az cognitiveservices usage list --location eastus2 -o table
```

## Deploy, run, test

Run from this directory. Use a **dedicated** resource group and a fresh service
principal without inherited inference rights. The certificate below lasts one
day, has no password, and must remain private; `umask` restricts its local files.
It authenticates to Microsoft Entra ID, not through an Azure OpenAI API key.
Do not rerun certificate creation over an existing identity without cleaning up
that identity first.

```bash
az login
az account set --subscription YOUR_SUBSCRIPTION_ID
RG=foundry-lab14
LOCATION=eastus2
umask 077
mkdir -p .azure
openssl req -x509 -newkey rsa:2048 -nodes -days 1 \
  -subj '/CN=FoundryLab14Inference' \
  -keyout .azure/lab14-inference.pem \
  -out .azure/lab14-inference-public.pem
cat .azure/lab14-inference-public.pem >> .azure/lab14-inference.pem
chmod 600 .azure/lab14-inference.pem
az ad sp create-for-rbac --name "foundry-lab14-inference-$(date +%Y%m%d%H%M%S)" \
  --cert @.azure/lab14-inference-public.pem \
  --query '{clientId:appId,tenantId:tenant}' -o json > .azure/restricted-identity.json
# No RBAC roles/scopes are assigned by the service-principal creation command.
export WORKSHOP_INFERENCE_CLIENT_ID=$(python3 -c 'import json; print(json.load(open(".azure/restricted-identity.json"))["clientId"])')
export WORKSHOP_INFERENCE_TENANT_ID=$(python3 -c 'import json; print(json.load(open(".azure/restricted-identity.json"))["tenantId"])')
export WORKSHOP_INFERENCE_CERTIFICATE_PATH="$PWD/.azure/lab14-inference.pem"
PRINCIPAL_ID=$(az ad sp show --id "$WORKSHOP_INFERENCE_CLIENT_ID" --query id -o tsv)
az group create --name "$RG" --location "$LOCATION" --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --name lab14 --resource-group "$RG" \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" principalType=ServicePrincipal location="$LOCATION" \
  --query properties.outputs -o json > .azure/deployment-outputs.json
export AZURE_OPENAI_ENDPOINT=$(az deployment group show -g "$RG" -n lab14 --query properties.outputs.endpoint.value -o tsv)
export AZURE_OPENAI_DEPLOYMENT=$(az deployment group show -g "$RG" -n lab14 --query properties.outputs.deployment.value -o tsv)
export FOUNDRY_PROJECT_ENDPOINT=$(az deployment group show -g "$RG" -n lab14 --query properties.outputs.projectEndpoint.value -o tsv)
export WORKSHOP_DENIED_ENDPOINT=$(az deployment group show -g "$RG" -n lab14 --query properties.outputs.deniedEndpoint.value -o tsv)
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py
python -m unittest discover -s . -p 'test_demo.py' -v
az bicep build --file infra/main.bicep --stdout >/dev/null
```

Tests are offline: stream aggregation and explicitly labeled SDK error fixtures
do not authenticate or call Azure. No test fixture is presented as a live answer.
Allow several minutes for new RBAC assignments to propagate, then rerun if needed.
The template serializes the primary model and project writes: concurrent account
child operations can fail with Azure `RequestConflict`.

The three `WORKSHOP_INFERENCE_*` values select `CertificateCredential` explicitly
for **both** accounts. Equivalent flags are `--tenant-id`, `--client-id`, and
`--certificate-path`. Partial or empty configuration is rejected before Azure is
called; invalid/expired certificates fail without falling back to the deployer.
Keep the exports when running through the root runner: it preserves these
variables while using your Azure CLI session for deployment. A new terminal can
reuse the identity JSON and certificate by repeating the three export commands;
after certificate expiry, clean up and recreate the development identity.

If none of these values is supplied, the demo retains `DefaultAzureCredential`
with browser authentication disabled. CLI/managed identities with inherited
Foundry Owner or other inference rights can therefore bypass the intended denial.
For a successful-response-only run, `--skip-negative-probes` is available.

## What to observe

* `FOUNDRY_PROJECT_ENDPOINT` ends in `/api/projects/workshop-tutor`: it is the
  **project management/agent** endpoint, not a chat completions base URL.
* `AZURE_OPENAI_ENDPOINT` is the account inference endpoint; the OpenAI client
  appends `/openai/v1/`. This lab does not use the project SDK for inference.
* The catalog model is `gpt-4.1-mini`; its deployment alias is `tutor-mini`.
  Pass the **alias** to `model=...`. Token counts are service-reported; a stream
  can contain a final usage-only chunk with no choices.
* A deliberately nonexistent deployment prints the actual HTTP 404 service body.
* A **second account and model** have deliberately no scoped inference assignment.
  The client derives its `-deny` hostname (or accepts `--denied-endpoint` /
  `WORKSHOP_DENIED_ENDPOINT`) and prints the actual permission error.
  Fix by assigning OpenAI User to the target identity at that account, then wait
  for propagation. Azure management Contributor alone is not inference access.

Inherited subscription/resource-group inference roles can make the denied probe
succeed. The script reports that honestly and exits 1 instead of manufacturing a
denial. A missing role does **not** guarantee HTTP 403: the live service returned
HTTP **401** with code `PermissionDenied`, both on `/openai/v1/` and the versioned
inference API. Its messages were either `Principal does not have access to
API/Operation.` or an explicit missing
`Microsoft.CognitiveServices/accounts/OpenAI/deployments/chat/completions/action`.
The permission probe requires HTTP 401/403 **and** code `PermissionDenied` **and**
one of those explicit authorization messages. Arbitrary 401s, invalid/expired
tokens, network-policy errors, and unrelated failures are not counted as passes.
Successful primary-account calls with the same credential establish that it
authenticates. Statuses and error bodies are printed unchanged, never relabeled.
Do not put actual secrets in prompts or publish unredacted tenant error details.

## Verified live result (2026-10-26)

The full demo exited **0** in `rg-foundry-foundry-fundamentals-demo`, East US 2:
normal inference used 83 tokens; streaming used 71 tokens; a nonexistent alias
returned real HTTP 404 `DeploymentNotFound`; the second account returned real
HTTP 401 `PermissionDenied`. Independent versioned-API verification explicitly
identified the missing chat-completions data action.

The new service principal has exactly one role assignment, **Cognitive Services
OpenAI User** on the primary account, and none inherited or on the denied account.
The denied model deployment exists and is healthy. The presenter's subscription
Foundry Owner assignment was retained. Nine offline tests passed separately.
Ignored local evidence is under `.azure/`: `live-restricted-identity.log`,
`live-validation.json`, `versioned-denial-probe.json`, and `restricted-roles.json`.
The retained development identity's Entra certificate expires at
**2026-10-27 10:00:19 UTC**; its private PEM is owner-only and uncommitted.
That historical certificate is now expired, not a reusable credential. Follow
the fresh-identity setup above rather than expecting the retained live-run
identity to authenticate.

## Contracts, costs, and limitations

Bicep accepts `principalId`, `principalType`, `location`; emits `endpoint`, `deployment`,
`projectEndpoint`, and `deniedEndpoint`. The root runner maps all four to the
corresponding environment variables. Without an explicit denied endpoint the
script also derives its default from the primary hostname.
Custom `accountName` must leave five characters for `-deny` (max 64 total).

Default run: **four requests** (normal, streamed, wrong-deployment, denied).
Successful model responses incur pay-as-you-go token charges; errors are not
assumed to be billable successes. Two model deployments reserve quota, not
provisioned-throughput billing. Check the current
[Azure OpenAI prices](https://azure.microsoft.com/pricing/details/cognitive-services/openai-service/).
No fixed total is promised. GlobalStandard may process outside the account region;
public networking is enabled for workshop convenience. This is not production
network isolation, a retry framework, or a guarantee of answer quality.

## Cleanup

Deletes **both accounts**, project, deployments, and scoped role assignment.
Deleting the resource group does **not** delete the tenant application/service
principal or the local credential. After use, also delete that dedicated app
(which deletes its service principal), then its named local credential files:

```bash
az group delete --name "$RG" --yes --no-wait
az ad app delete --id "$WORKSHOP_INFERENCE_CLIENT_ID"
rm .azure/lab14-inference.pem .azure/lab14-inference-public.pem .azure/restricted-identity.json
unset WORKSHOP_INFERENCE_CLIENT_ID WORKSHOP_INFERENCE_TENANT_ID WORKSHOP_INFERENCE_CERTIFICATE_PATH
```

## Official API references

* [Keyless OpenAI/Azure endpoint differences](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/switching-endpoints)
* [Azure OpenAI role-based access](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/role-based-access-control)
* [Foundry account schema](https://learn.microsoft.com/azure/templates/microsoft.cognitiveservices/2025-06-01/accounts)
* [Project schema](https://learn.microsoft.com/azure/templates/microsoft.cognitiveservices/2025-06-01/accounts/projects)
