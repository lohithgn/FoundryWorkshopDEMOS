# Lab 35 - A Foundry agent uses Work IQ as the calling user

## What you will see

A **persistent prompt agent in Microsoft Foundry**, with the native
`WorkIQPreviewTool`, delegates a profile lookup to Work IQ through A2A.
There is no local Work IQ process, Graph retrieval tool, shared user token,
application-only Microsoft 365 access, or local function-call runner.

The client authenticates as an Azure CLI **user**. Foundry's **OAuth2 identity
passthrough** connection asks each caller to authorize their own Microsoft 365
account and keeps individual credentials on the service side. The connection's
OAuth client secret identifies the OAuth application, **not a shared data-access
user**. `isSharedToAll` shares connection configuration, not user sessions.
The agent/project identity is not the Microsoft 365 retrieval identity.

OAuth requires a separate sign-in: it does not silently reuse the incoming Azure
token. Authorize the **same account** as the Azure CLI caller. The harness checks
this explicitly against an independent Graph `/me` lookup. If the browser selects
another account, the identity gate fails. This sample isn't a production API
gateway and doesn't implement automatic incoming-token OBO delegation.

Default requests read only the user's own object ID and principal name. Those
values are **not supplied to the model**. The client forces the sole native tool,
pins the agent version, checks completion, and compares the answer to Graph
ground truth. Each repetition starts fresh; only the same caller's consent
continuation uses `previous_response_id`.

## Prerequisites and boundaries

- Python 3.10+, Azure CLI, Bicep, Azure model quota, and an interactive Azure user
  with resource creation and role-assignment permissions.
- Microsoft 365 users in the **same tenant** as the Foundry project. Cross-tenant
  exchange and application-only Work IQ access are unsupported.
- Work IQ API billing with **Copilot Credits** enabled by your administrator.
  Connector-specific licensing is different; having a Copilot license alone
  does not establish that this A2A billing path is enabled.
- An administrator-approved, single-tenant **confidential Web OAuth application**,
  delegated `WorkIQAgent.Ask` permission, and tenant-wide admin consent.
- One-time provisioning of the Work IQ service principal:
  application ID `fdcc1f02-fc51-4226-8753-f668596af7f7`.
  Follow the official quickstart below; use a Global Administrator with
  just-in-time PIM activation. The lab never grants tenant-wide consent itself.
- Two distinct real users are needed to verify **two-user** behavior. One successful
  user's test does not establish isolation, document ACL trimming, or resistance
  to prompt injection.

Work IQ is preview. Requests cross the Foundry service boundary and can be
processed outside the Azure compliance boundary. Review tenant policy, billing,
regional processing and service terms before authorization. Use approved test
accounts. Do not query real mail or files during this identity-only exercise.

## 1. Provision the Azure infrastructure

From the repository root:

```bash
dotnet run --file deploy.cs -- --demo 35
```

On the **first** run without tenant setup, Azure infrastructure is deployed and
outputs are saved, but the live phase must **fail** with a missing-connection
error. This is not a successful Work IQ test. Complete steps 2 and 3, then rerun
the root command to exercise the complete workflow.

The template creates a dedicated Foundry account, `gpt-4.1-mini`, project,
Foundry User for the developer, and project-scoped Foundry Project Manager for
connection administration. It does not create an OAuth app or bypass consent.

Alternatively, deploy manually:

```bash
cd 35-work-iq
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
.venv/bin/python -m unittest -v
az group create --name rg-foundry-work-iq-demo --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
mkdir -p .azure
az deployment group create --resource-group rg-foundry-work-iq-demo \
  --name work-iq-demo --template-file infra/main.bicep \
  --parameters principalId="$(az ad signed-in-user show --query id -o tsv)" \
  --query properties.outputs -o json > .azure/deployment.json
```

The manual output-only record is supported by this lab. The root runner saves
subscription/group metadata too. If using environment configuration instead,
set `FOUNDRY_PROJECT_ENDPOINT`, `FOUNDRY_PROJECT_RESOURCE_ID`,
`AZURE_OPENAI_DEPLOYMENT`, and `AZURE_TENANT_ID` consistently.

## 2. Administrator: configure the OAuth application and connection

1. Follow [Microsoft's Work IQ setup](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/work-iq#authentication-and-security).
   Provision the Work IQ service principal if absent.
2. Register a **single-tenant** app. Add delegated
   `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask` and grant admin consent.
   Create a short-lived secret in your approved secret store.
3. Configure the Foundry connection from the lab directory. Never commit or log
   the secret, pass it in command-line arguments, or place it in a prompt:

   ```bash
   export WORK_IQ_CLIENT_ID="<approved-app-client-id>"
   # Set WORK_IQ_CLIENT_SECRET from your approved secret store without echoing it.
   .venv/bin/python configure_connection.py
   unset WORK_IQ_CLIENT_SECRET
   ```

4. Add the exact returned `oauthRedirectUrl` as a **Web redirect URI** on that
   app. The script does not mutate app registrations or admin grants.

The connection uses `OAuth2`, category `RemoteA2A`, target
`https://workiq.svc.cloud.microsoft/a2a/`, tenant-specific authorize/token/refresh
URLs, and `WorkIQAgent.Ask` plus `offline_access`. Secrets go only to ARM over
HTTPS and aren't written to deployment outputs, reports, or logs.
The setup script upserts this lab's named connection; do not use it to replace a
production connection. Replacing it may require renewed user authorization.

## 3. Caller: authorize and test the saved agent

```bash
.venv/bin/python demo.py
```

The agent remains in project `work-iq` as **work-iq-user-agent**. A first-use
`oauth_consent_request` causes exit code **2**, not PASS. The consent URL is kept
in a mode-0600 ignored state file, not printed into the deployment transcript:

```bash
.venv/bin/python demo.py --ask-only --show-consent
# Open the printed HTTPS URL yourself. Review consent and sign in as the SAME
# account used by az login. Wait for authorization success in that window.
.venv/bin/python demo.py --ask-only --resume
```

Opening/closing the window alone is not evidence that consent succeeded.
Continuation state is bound to the tenant, Graph-authenticated caller, project,
connection, agent version, and question, and expires after one hour. If it expires,
start again with `--ask-only`. Each run checks two independent identity answers
by default (`--repetitions 1` through `5`).

Mode-0600 `*-report.json` files contain IDs, the tested identity, output item
types, and a `complete` flag. They do not contain access tokens, client secrets,
email/file bodies, or full Work IQ responses. Reports are local test evidence,
not tamper-proof audit records. A successful response and forced-tool setting
are a smoke test, not a substitute for inspecting the native Work IQ trace in
Foundry. Failed authorization, billing, service or identity checks fail visibly.

## 4. Prove two-user behavior, not a shared presenter session

Grant the second same-tenant user Foundry User on the project, without granting
connection administration:

```bash
az role assignment create --assignee-object-id "<second-user-object-id>" \
  --assignee-principal-type User \
  --role 53ca6127-db72-4b80-b1b0-d745d6d5456d \
  --scope "<projectResourceId from deployment outputs>"
```

Alternatively supply `secondPrincipalId` when manually deploying Bicep.
Run the first user's test and note its printed report path. For the second user,
use a **separate** Azure CLI configuration and the same saved agent:

```bash
AZURE_CONFIG_DIR="$HOME/.azure-workiq-lab-second" az login --tenant "<tenant-id>"
AZURE_CONFIG_DIR="$HOME/.azure-workiq-lab-second" .venv/bin/python demo.py --ask-only
AZURE_CONFIG_DIR="$HOME/.azure-workiq-lab-second" .venv/bin/python demo.py --ask-only --show-consent
# Authorize user TWO, preferably in a separate browser profile.
AZURE_CONFIG_DIR="$HOME/.azure-workiq-lab-second" .venv/bin/python demo.py --ask-only --resume
.venv/bin/python demo.py --verify-isolation "<first-report-path>" "<second-report-path>"
```

The comparison requires different authenticated object IDs, the **same** tenant,
project, connection and agent version, distinct response IDs, and each answer
matching its own caller. A copied report or same-user test fails. Don't create a
new version between the two runs.

In the Foundry portal, open this project and saved agent, sign in as each caller,
and use the same profile question. Inspect the Work IQ/A2A trace and authorization
prompt. Don't paste tokens into playground messages. Portal answers aren't
automatically added to the Python reports.

## Costs, cleanup and current validation status

Model inference and Work IQ usage incur charges. This lab has no Function,
Search, database, or continuously running compute plan. Azure resources and the
agent intentionally remain for the presentation.

```bash
az group delete --name rg-foundry-work-iq-demo --yes
```

The repository cleanup script discovers lab 35 automatically. Separately revoke
this lab's OAuth application consent and delete a dedicated lab app/secret when
no longer needed, **only if your administrator confirms it is not shared**.
Deleting a resource group does not remove Entra apps, Copilot billing policies,
or local Azure CLI sign-ins. Remove the named private consent/report files when
no longer needed; never delete your normal Azure CLI configuration.

**Status — 2026-10-27: Work IQ and two-user verification BLOCKED.** Azure
deployment and saved-agent readback passed, but the live OAuth connection failed
and administrator setup, billing confirmation and individual user sign-in were
unavailable. See [the verification log](../tests.md). Neither offline mocks,
successful deployment nor a model-only response count as a live Work IQ pass.

References:
[native Work IQ](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/work-iq),
[OAuth continuation](https://learn.microsoft.com/azure/foundry/agents/how-to/mcp-authentication#oauth-identity-passthrough),
[connection authentication](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/tool-authentication).
