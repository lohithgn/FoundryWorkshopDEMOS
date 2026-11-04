# 19 — Conversation state, context limits and memory boundaries

A real Foundry model tutors two **logical application users**. SQLite is the supported
durable application-state mechanism: Chat Completions itself is stateless, so the app
must explicitly send history on each request. This is a local single-process teaching
application, not an authenticated web service.

## Prerequisites and standalone deployment

Python 3.10+, Azure CLI/Bicep and `az login` as a user. The deployer needs permission
to create Cognitive Services resources and role assignments (Contributor plus User
Access Administrator is one option). Register `Microsoft.CognitiveServices` if needed.
The region must support `gpt-4.1-mini` version `2025-04-14`, GlobalStandard capacity 10,
with sufficient quota. Pricing and model regional availability can change.

From this directory:

```sh
az login
az account set --subscription YOUR_SUBSCRIPTION_ID
export RG=rg-lab19
export LOCATION=eastus2
az group create --name "$RG" --location "$LOCATION" --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv)
mkdir -p .azure
az deployment group create --resource-group "$RG" --name lab19 \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location="$LOCATION" \
  --query properties.outputs -o json > .azure/outputs.json
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
export AZURE_OPENAI_ENDPOINT=$(python -c 'import json; print(json.load(open(".azure/outputs.json"))["endpoint"]["value"])')
export AZURE_OPENAI_DEPLOYMENT=$(python -c 'import json; print(json.load(open(".azure/outputs.json"))["deployment"]["value"])')
python demo.py
```

Bicep disables local auth and grants Foundry User (role ID
`53ca6127-db72-4b80-b1b0-d745d6d5456d`, formerly Azure AI User) to the presenter.
Inference uses that presenter's `AzureCliCredential`, not a model-side user identity.
If inference returns 403, confirm the principal and allow time for RBAC propagation.
`endpoint` is the inference root, not a Foundry project URL; the client appends
`/openai/v1/`. The `deployment` output matches the root deploy runner contract.
No project endpoint, Search service, or server-side conversation API is required.

## Default demonstration

The default command is noninteractive and makes real model turns. It:

1. Saves Alice's notebook fact to SQLite, closes/reopens connections and resumes.
2. Asks Alice to recall the fact, then asks Bob the same question with the same
   conversation ID but a different user scope.
3. Sends enough turns to evict the opening pair and shows the fact is now absent.
4. Asks again, illustrating lost context, resets Alice without affecting Bob,
   then deletes both demonstration conversations.

The runner verifies persisted history isolation, resume, truncation, reset and deletion.
It does not claim the model's stochastic wording is a proof of authentication or
memory deletion. The default budget is deliberately tiny to make loss visible.

## Persist across separate CLI invocations

```sh
python demo.py --action turn --user alice --conversation lesson \
  --prompt 'My notebook label is MAPLE-742. Remember it.'
python demo.py --action turn --user alice --conversation lesson \
  --prompt 'What is my notebook label?'
python demo.py --action show --user alice --conversation lesson
python demo.py --action show --user bob --conversation lesson
python demo.py --action reset --user alice --conversation lesson
python demo.py --action delete --user alice --conversation lesson
```

`--state` defaults to `.state/conversations.sqlite3`, with owner-only file permissions.
`show`, `reset` and `delete` need no cloud credentials. Every operation binds both
`user` and `conversation` as SQL parameters. Reset keeps an empty row; delete removes
the row. There is no separate summary, vector memory or hidden model-side history.
This single-process example has no concurrent-writer reconciliation; do not run
parallel turns against the same scope.

`--user` is a **logical teaching scope, not authentication**. Anyone with local CLI/DB
access can select it. A production server must derive it from a verified authenticated
principal, enforce ownership on every operation and protect backups/encryption.
The SQLite database contains plaintext prompts and answers: use synthetic data only.

## Explicit context policy

`--budget 800 --reserve 120` enforces a conservative input-plus-output bound before
every model call. Input cost is UTF-8 content bytes plus 16 per message (including
system and current user); this intentionally overestimates text tokens for this
text-only model, rather than pretending to be exact usage accounting. `reserve`
also sets `max_completion_tokens`. There are no tools, images or hidden summaries.
The app discards **whole oldest user/assistant pairs**, never the system message.
If the current prompt cannot fit, it fails rather than silently truncating it.
Persisted pairs are also bounded after generation; very large pairs can be discarded
entirely. Removed messages are printed. For other model protocols/token accounting,
use their tokenizer and overhead rules instead.

Conversation history is the selected messages sent to the model. Durable application
memory is data deliberately retained by an app (here only this bounded SQLite history).
Model knowledge is learned training information; saving a row does not retrain the model.
Truncation here is destructive in the logical store: there is no unbounded transcript
to retrieve later. Database deletion is not forensic secure erasure, and does not
delete Azure service telemetry, backups or already printed terminal output.

## Offline tests and validation

Tests write unique disposable SQLite files **in this directory**, then remove them.
No cloud access or installed SDKs are required.

```sh
python3 -m unittest -v test_demo.py
mkdir -p .azure
az bicep build --file infra/main.bicep --stdout > .azure/template.json
```

Tests cover persistence, scoped read/reset/delete, SQL parameterization, whole-pair
budget truncation, UTF-8 cost, oversize rejection and a failed model response leaving
state unchanged. Model behavior and Azure reachability require the live run.

## Costs and cleanup

Only a pay-per-token GlobalStandard model deployment is created: no Search, provisioned
throughput or hosted agent compute. The default lesson makes nine short chat calls;
charges depend on actual input/output tokens and current regional model pricing.
Use the Azure pricing calculator. SQLite has local storage only.
The Azure endpoint is public; GlobalStandard processing can leave the account
region. Logical Alice/Bob scopes do not alter the Azure billing/authentication identity.

```sh
python demo.py --action delete --user alice --conversation lesson
az group delete --name "$RG" --yes --no-wait
rm -rf .state .azure .venv __pycache__
```

Delete only this lab's dedicated group. Local `rm` is not secure erasure.

## API reference

[Azure OpenAI v1 Python endpoints, deployment names and Entra authentication](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/switching-endpoints)
