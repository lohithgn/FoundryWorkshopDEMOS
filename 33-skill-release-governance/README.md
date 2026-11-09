# 33 - Release a skill independently from its consumers

Store two immutable pricing-skill versions in the preview Foundry Skills API.
Evaluate the candidate on two fixed synthetic pricing facts **before** promoting
its default. Compare a default-resolving consumer with a v1-pinned consumer,
restore v1, and verify both consumers again. Each request prints the exact
consumed skill version and actual response/usage.

The normal path makes eight paid model requests. `--regression` changes the
candidate's intro price from 25 to 40 and must fail the deterministic factual
gate, without promotion. Model instructions are not sufficient evidence:
responses must match the expected workshop, integer price, currency and version
marker with no extra fields. Both skill versions and the request identify the
workshop field as the exact ID (`intro` or `advanced`), not a descriptive name.
A model can ignore instructions, so an unexpected
answer is a failed demonstration, never replaced with a fixture.

## Scope and prerequisites

- Python 3.10+, Azure CLI/Bicep, interactive `az login`.
- Azure resource creation/role-assignment rights, GPT-4.1-mini `2025-04-14`
  GlobalStandard quota, and preview Foundry Skills API support.
- Bicep grants the presenter Foundry User at account scope. Keys are disabled.

**Skill defaults change centrally; downloaded instructions do not hot-reload.**
The local harness resolves the default at the start of each fresh consumer
session, downloads that exact version, checks its instructions, and injects it
into a temporary Foundry prompt agent. Pinned consumers always request their
original immutable version. All consumer creation uses the same agent code,
model and instruction template; this is skill release governance, not agent
application deployment or rolling updates to a hosted service. Fresh temporary
agent versions make the injected snapshot explicit.

The gate is local deterministic checking of real Foundry responses, **not**
Foundry cloud evaluation or a CI pipeline. Lab 23 covers cloud evaluation/CI.
This tiny dataset is not production quality assurance. Prices are fictional;
public endpoints and GlobalStandard processing are workshop defaults.

## Deploy and present

From the repository root:

```bash
az login
python3 deploy.py --subscription "<subscription-id>" --demo 33
```

Standalone, from this folder:

```bash
az login
RESOURCE_GROUP=rg-foundry-skill-release-governance-demo
PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv)
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group "$RESOURCE_GROUP" --name demo \
  --template-file infra/main.bicep --parameters principalId="$PRINCIPAL_ID"
export FOUNDRY_PROJECT_ENDPOINT=$(az deployment group show -g "$RESOURCE_GROUP" -n demo --query properties.outputs.projectEndpoint.value -o tsv)
export AZURE_OPENAI_DEPLOYMENT=$(az deployment group show -g "$RESOURCE_GROUP" -n demo --query properties.outputs.deployment.value -o tsv)
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
python -m unittest discover -s . -p 'test_*.py' -v
python demo.py
python demo.py --regression
```

The last command is intentionally expected to exit nonzero. It creates its own
unique skill, so it cannot change the default of another lab/user's skill.
Preview 400/404, authorization 403 and quota 429 errors surface explicitly.
There is no local fallback if the service cannot store or download versions.

## Offline validation and cleanup

```bash
python -m unittest discover -s . -p 'test_*.py' -v
az bicep build --file infra/main.bicep --stdout >/dev/null
az bicep lint --file infra/main.bicep
```

Tests mock cloud boundaries and check the installed SDK request models, promotion
ordering, pinned isolation, regression failure, rollback, and cleanup. They do
not establish live preview availability or model behavior.

Every temporary agent version is deleted after its request. The default is
restored and read back in `finally`, including after a gate/inference failure;
the uniquely owned skill is then deleted. Cleanup errors surface explicitly.
An interrupted process may require recovery using the printed names. Responses
remain subject to service retention policies. Infrastructure stays deployed:

```bash
az group delete --name "$RESOURCE_GROUP" --yes
```

Confirm subscription and dedicated group before deletion.

## Sources

- [Foundry skills: immutable versions, default promotion, direct injection and preview limitations](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/skills)
