# Human approval before a tool action

A Foundry model proposes a **synthetic** workshop booking through function
calling. The application, not the model, validates the proposal and owns the
execution gate. Nothing is booked by the default command.

## Deploy

Requires Python 3.10+, Azure CLI/Bicep, an interactive Entra user with resource
creation/role-assignment rights, and `gpt-4.1-mini` quota in a supported region.
From this directory:

```bash
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
RG=rg-foundry-human-approval-demo
az group create -n "$RG" -l eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create -g "$RG" -n demo --template-file infra/main.bicep \
  --parameters principalId="$(az ad signed-in-user show --query id -o tsv)"
export AZURE_OPENAI_ENDPOINT="$(az deployment group show -g "$RG" -n demo \
  --query properties.outputs.endpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show -g "$RG" -n demo \
  --query properties.outputs.deployment.value -o tsv)"
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
python demo.py
```

Review the exact workshop and attendee count, then copy the printed **approve**
or **reject** command with its proposal ID and digest. Decisions require the
same signed-in Entra tenant/object identity that proposed the action. Proposals
expire after 15 minutes. Approval creates one local SQLite booking; repeating
it returns that existing booking. Rejection is final. Generate a fresh proposal
to demonstrate the other branch. Only `foundry-intro` and 1-20 attendees pass
the application policy. The model never receives an executable booking tool.
To keep multiple exercises separate, use `--database <path>` consistently for
proposal and decision commands; the printed commands otherwise use the default
database. Approval/rejection needs Azure CLI authentication but does not make
another model request.

## Teaching points and validation

The digest binds the proposal ID, actor, and exact payload. A changed argument
requires a new proposal and approval. Tests cover approval, rejection, replay,
expiration, another actor, payload modification, and model argument mismatch.
Live Foundry responses can carry a complete tool call with finish reason `stop`
or `tool_calls`; both require exactly one valid tool call. Truncation, refusal,
unexpected operations, and mismatched arguments still fail before any action.

```bash
python -m unittest discover -s . -p 'test_*.py' -v
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
```

This is a local single-machine approval UI, not a production authorization
service. The local database and OS account are trusted; a digest is not a
signature against a malicious database administrator. Token claims are read
only from an SDK-acquired token, not arbitrary client-submitted JWTs. Production
needs server-side token validation, durable audit, and business-specific roles.
No real reservations/payments or external side effects exist.

## Costs and cleanup

Model inference is billed; SQLite is local. Public endpoints and keyless Entra
authentication keep the workshop small. A 403 can mean RBAC propagation or
missing inference rights. Errors are not converted to successful bookings.
The template grants the presenter Cognitive Services OpenAI User and deploys
GPT-4.1-mini `2025-04-14`, GlobalStandard capacity 10 in East US 2 by default.
GlobalStandard processing may leave the account region; use synthetic input only.

```bash
az group delete -n "$RG" --yes
```

Delete this lab's `.azure/bookings.db` explicitly when finished with its
synthetic local history. Deleting the resource group does not delete local state.

- [Foundry function calling](https://learn.microsoft.com/azure/foundry/openai/how-to/function-calling)
- [Tool security guidance](https://learn.microsoft.com/azure/foundry/agents/concepts/tool-best-practice)
- [Backlog F020](../BACKLOG.md#f020---microsoft-foundry-human-approved-tool-actions)
