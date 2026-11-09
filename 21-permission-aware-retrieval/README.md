# 21 — Permission-aware retrieval with real user identities

Use two **real Microsoft Entra user sessions**, a real Azure AI Search service and a
Foundry chat deployment. The backend derives a trusted security filter from Graph-
authenticated user/group identities. Prompt text can never supply or change ACLs.
This is application-enforced security trimming, **not** Search's native token-based
document ACL feature, and not proof that two invented names authenticate.

## Tenant prerequisites (complete before the live runner)

- Python 3.10+, Azure CLI/Bicep, an Azure subscription and two different test **users
  in the same tenant**. Use synthetic documents and dedicated accounts where possible.
- The presenter/backend deployer needs resource creation and role-assignment rights
  (e.g. Contributor plus User Access Administrator). Register
  `Microsoft.CognitiveServices` and `Microsoft.Search` if needed.
- Model `gpt-4.1-mini` version `2025-04-14` must be available with GlobalStandard quota
  for capacity 10 in the selected region.
- Both users need successful delegated Microsoft Graph `/me` and
  `/me/transitiveMemberOf/microsoft.graph.group` access using their Azure CLI login.
  Reading the signed-in user's own profile and memberships uses delegated `User.Read`;
  tenant consent/Conditional Access policy may require administrator approval or block
  the Azure CLI application. Resolve that policy with your administrator. This runner
  does not request directory-wide application permissions, create users, or bypass MFA.
  A Graph error or incomplete/malformed membership result fails closed.
  Hidden memberships require `Member.Read.Hidden` to enumerate; this example does not
  grant access based on memberships it cannot see.
- The second user needs **no Search/inference Azure RBAC assignment**. It authenticates
  to Graph; the backend uses the presenter's resource credential. Do not grant end users
  direct Search access in an application relying on this filter.

From this directory, establish separate sessions (these setup logins are interactive;
`demo.py` itself is not):

```sh
export AZURE_TENANT_ID=YOUR_TENANT_UUID
az login --tenant "$AZURE_TENANT_ID"
az account set --subscription YOUR_SUBSCRIPTION_ID
export SECOND_AZURE_CONFIG_DIR="$PWD/.identity-b"
AZURE_CONFIG_DIR="$SECOND_AZURE_CONFIG_DIR" az login \
  --tenant "$AZURE_TENANT_ID" --allow-no-subscriptions
python3 demo.py --preflight
```

Sign in as the **second** user in the second command; logging in twice as the same user
is rejected. Keep the first session as presenter. `.identity-b/` is gitignored and holds
sensitive credential cache files: never commit/copy them. `AZURE_CONFIG_DIR` for the
first user may also be set explicitly, but must differ from `SECOND_AZURE_CONFIG_DIR`.
The script acquires tenant-specific Graph tokens, calls Graph to authenticate each
user and enumerates transitive group membership, including pagination. It does not
trust unsigned token decoding, environment-supplied user IDs or group names.

### Unblock the missing second-user session

On 2026-10-26, the presenter's actual Graph profile and transitive membership
requests succeeded, and all ten offline tests passed. The second session directory
was absent and `SECOND_AZURE_CONFIG_DIR` was unset. This is an authentication setup
blocker, not a demonstrated retrieval-code failure; live two-user isolation remains
unverified.

An authorized provisioning attempt on 2026-10-26 was rejected by Graph:
`POST /v1.0/users` returned HTTP 403 `Authorization_RequestDenied`,
`Insufficient privileges to complete the operation.` Directory-role readback
showed the presenter has **Global Reader**, not a user-administration role.
No user was created; temporary password/request files were removed. No roles,
MFA settings, Conditional Access policies, or consent grants were changed.
An authorized tenant administrator must create the test account, or authorize
user creation through the tenant's normal role-assignment process.

What is needed:

1. A different real user in the same tenant. An existing permitted member/guest
   account can be used. If none is available, ask a tenant administrator to provide
   or invite a dedicated test user; subscription Owner alone does not grant Entra
   user-administration rights.
2. That user must complete its own login/MFA and be allowed to access its Graph
   profile and memberships. No subscription, Search/model RBAC, custom group, or
   directory-wide application permission is needed for the default user-ID ACL.
3. Keep its Azure CLI session separate from the presenter's session. A second login
   as the presenter, an invented user UUID, or lab 14's service principal does not
   satisfy this delegated-user test.

From this lab directory, while the default Azure CLI session is still the
presenter, run:

```sh
export AZURE_TENANT_ID="$(az account show --query tenantId -o tsv)"
export SECOND_AZURE_CONFIG_DIR="$PWD/.identity-b"
AZURE_CONFIG_DIR="$SECOND_AZURE_CONFIG_DIR" az login \
  --tenant "$AZURE_TENANT_ID" --allow-no-subscriptions --use-device-code
python3 demo.py --preflight
```

Complete the device login yourself as the **second user**, using a private browser
window if necessary to avoid reusing the presenter. Do not share passwords, tokens,
device codes, or the credential cache. The verified presenter's tenant was
`a1d6c791-d37f-46f2-860a-105cc2a00fe3`; the command above uses your currently selected
tenant instead of silently switching subscriptions.

Only after preflight reports two distinct authenticated users should you deploy
and run the exercise below. Keep the exports in the same terminal. The final
verification must still check all six real question/user combinations, exclusion
of restricted evidence/citations, and index cleanup; first-user authentication and
offline tests alone are not a live pass. No new billable lab resources were deployed
during this prerequisite investigation.

### Optional: provision a dedicated test user with PIM

The lab runner still never creates users. Two separate, interactive **PowerShell 7**
helpers are available:

- [activate_user_admin.ps1](./activate_user_admin.ps1) checks the signed-in user's
  existing, direct, currently valid, **tenant-wide User Administrator** PIM
  eligibility and optionally submits a time-limited `selfActivate` request.
- [create_test_user.ps1](./create_test_user.ps1) creates one enabled cloud-managed
  member account with a securely prompted temporary password and a required
  password change at first sign-in. It assigns no license, groups, or roles.
  An existing UPN is an error, not an instruction to modify/reset that account.

**Feasibility:** Global Reader and subscription Owner cannot create users.
PIM activation works only if a tenant administrator has already made your account
eligible for User Administrator and your tenant has the required PIM licensing.
These scripts cannot grant eligibility, consent permissions, approve a request,
or bypass MFA/Conditional Access. Group-mediated or administrative-unit-scoped
eligibility is deliberately unsupported by this helper; use your tenant's normal
PIM process instead. An already-active User Administrator can skip activation.
The historical 403 above is not a fresh check of your current eligibility.

Both helpers require the `Microsoft.Graph.Authentication` PowerShell module.
If missing, install it in PowerShell with
`Install-Module Microsoft.Graph.Authentication -Scope CurrentUser`.
Your administrator must approve the delegated Graph permissions used by the helpers:
`User.Read`, `RoleEligibilitySchedule.Read.Directory` and
`RoleAssignmentSchedule.ReadWrite.Directory` for PIM, and `User.Read` plus
`User.Create` for provisioning. `-CheckOnly` does not request the PIM write scope.
Directory-role activation and Graph application consent are separate requirements;
activating User Administrator does not grant application consent.

Run from this lab directory, substituting your tenant and a **verified, managed**
domain (the tenant's `onmicrosoft.com` domain is usually suitable):

```powershell
$tenant = "YOUR_TENANT_UUID"
./activate_user_admin.ps1 -TenantId $tenant `
  -Justification "Lab 21 dedicated test account" -CheckOnly
./activate_user_admin.ps1 -TenantId $tenant `
  -Justification "Lab 21 dedicated test account" -DurationMinutes 60
# If policy requires a ticket, also supply -TicketNumber and -TicketSystem.
```

Sign in as the presenter/operator. Activation prompts for confirmation and reports
the **request ID and actual request status**, not an unconditional activation pass.
Check Entra PIM **My roles / My requests**, complete any required MFA/approval, and
wait until the role is active and permissions have propagated. A pending request is
not authorization to proceed. If Graph authentication cannot satisfy a tenant's
authentication-context/MFA policy, activate through the Entra portal instead.
Then run the separate provisioning helper, signing in as the same operator:

```powershell
./create_test_user.ps1 -TenantId $tenant -DisplayName "Lab 21 test user" `
  -UserPrincipalName "lab21-test@YOURTENANT.onmicrosoft.com" -WhatIf
./create_test_user.ps1 -TenantId $tenant -DisplayName "Lab 21 test user" `
  -UserPrincipalName "lab21-test@YOURTENANT.onmicrosoft.com"
```

Both scripts support `-WhatIf` and confirmation. PIM `-WhatIf` authenticates and
reads eligibility but submits nothing; provisioning `-WhatIf` does not authenticate
or prompt for a password. Graph failures terminate without attempting policy changes
or fallback accounts. If a request times out, check PIM/Entra before retrying: a server
may have accepted it. Do not enable PowerShell transcription, debug tracing, or
HTTP logging while provisioning. Passwords are never printed or saved by the helper;
they necessarily exist briefly in process memory for the HTTPS request. Graph
sessions are process-scoped and disconnected on exit; Azure CLI logins are unchanged.

Give the temporary password to the intended test user through an approved secure
channel. That user must change it and complete any required MFA registration,
then perform the separate Azure CLI login shown above and pass `demo.py --preflight`.
These helpers are **not** a live two-user retrieval test. No live provisioning or
PIM activation is performed by the offline tests. When done, deactivate the PIM role
through the portal (or let its approved duration expire), and have an authorized
administrator remove only the dedicated account you created.

## Standalone deploy/run

```sh
export RG=rg-lab21
export LOCATION=eastus2
az group create --name "$RG" --location "$LOCATION" --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv)
mkdir -p .azure
az deployment group create --resource-group "$RG" --name lab21 \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location="$LOCATION" \
  --query properties.outputs -o json > .azure/outputs.json
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
export AZURE_OPENAI_ENDPOINT=$(python -c 'import json; print(json.load(open(".azure/outputs.json"))["endpoint"]["value"])')
export AZURE_OPENAI_DEPLOYMENT=$(python -c 'import json; print(json.load(open(".azure/outputs.json"))["deployment"]["value"])')
export AZURE_SEARCH_ENDPOINT=$(python -c 'import json; print(json.load(open(".azure/outputs.json"))["searchEndpoint"]["value"])')
python demo.py
```

Run `python3 demo.py --preflight` **before deployment**. This uses only the standard
library and Azure CLI, authenticates both real users and checks the optional group,
without provisioning anything or calling Search/model inference.
Missing two-user setup produces an explicit preflight error, never a fake access proof.
These outputs match root `deploy.py`; it must inherit `AZURE_TENANT_ID` and
`SECOND_AZURE_CONFIG_DIR` and invoke this preflight before provisioning this lab.
No project endpoint is needed.
`endpoint` is the inference service root; the app appends `/openai/v1/`.
The Bicep roles are Foundry User (role ID
`53ca6127-db72-4b80-b1b0-d745d6d5456d`, formerly Azure AI User),
Search Service Contributor and
Search Index Data Contributor for the backend presenter only. Local API-key auth is
disabled. If 403 occurs, check identity/roles and wait for role-assignment propagation.

Default restricted access uses the **first Graph-authenticated user's object ID**,
namespaced by tenant. For a group ACL demonstration, optionally set:

```sh
export RESTRICTED_GROUP_ID=YOUR_EXISTING_ENTRA_GROUP_UUID
python demo.py
unset RESTRICTED_GROUP_ID
```

That group must contain the first user (possibly transitively) but not the second.
The runner verifies both real membership sets before uploading documents. It never
adds memberships or accepts group claims from the question. Creating a suitable test
group/memberships may require a Groups Administrator; allow propagation before running.

## What happens and what is verified

1. Authenticate both real user principals through successful Graph requests.
2. Upload one public agenda and one restricted launch-code document to a unique index.
3. Ask the same public question, restricted question and prompt-injection question
   as each identity.
4. Apply `public eq true or acl/any(p:search.in(...))` on **every Search query**,
   with canonical tenant/user/group UUID principals, before any passage reaches the model.
5. Print filtered evidence, the trusted filter, model-selected answer and citations.
   Verify public answers for both, restricted answer for the allowed user, and explicit
   insufficient evidence for the excluded user's restricted question. Verify excluded
   passages, answers and citations never contain the restricted document/canary.
6. Remove the unique index in `finally`.

The real model selects exact supporting quotes in JSON; the application validates
those against authorized passages and renders them as an **extractive answer**.
There is intentionally no freeform model answer channel that could introduce content
outside the authorized evidence. Invalid quotes/citations fail closed. No caching,
conversation state, reranking or model-based authorization is used.

The first user is also the trusted backend operator and can inspect all synthetic
content. This CLI demonstrates the retrieval boundary, not protection against a
malicious backend administrator/local file reader. A production service must validate
incoming bearer tokens (issuer/audience/signature/expiry), derive the end-user subject,
resolve membership through a trusted path and keep Search credentials server-side.
Never expose this example's helper functions as arbitrary client-controlled identity APIs.
The Graph group cast uses the documented `$count=true` plus
`ConsistencyLevel: eventual` advanced query contract. Membership snapshots are refreshed
per run, not continuously, and the directory index can lag recent changes. Wait for
membership propagation and rerun; this is not an immediate revocation mechanism.
No claim is made about preventing all semantic inference from public data.

An **agent identity** authorizes an agent/backend to call services. It does not
automatically authorize every document for every end user. Here the backend's Azure
credential and the caller's effective document access are deliberately separate.

## Offline tests and Bicep validation

```sh
python3 -m unittest -v test_demo.py
mkdir -p .azure
az bicep build --file infra/main.bicep --stdout > .azure/template.json
```

Offline tests need no installed SDKs or credentials. They use explicitly synthetic
UUID fixtures to test filter construction, prompt independence, Graph authentication
failures/pagination, group validation, public/restricted ACLs and output quote/citation
rejection. **Offline fixtures are not live authentication or access-control proof.**
Only an actual two-user cloud run can verify tenant setup and Search enforcement.

The provisioning helpers also have standalone offline PowerShell tests (no Graph
module, credentials, or network access required):

```sh
pwsh -NoProfile -File ./test_identity_setup.ps1
```

## Costs and cleanup

Basic Search bills continuously for one replica/partition even with an empty index.
One run makes six short chat requests. The model is pay-per-token GlobalStandard;
there is no embedding deployment, provisioned throughput or semantic ranker.
Check current regional Search/model prices in the Azure pricing calculator.
Search and Foundry use the same region by default; `searchLocation` defaults
to `location`. When Basic Search capacity is unavailable in the model region,
pass an available region, for example `searchLocation=westus2` for standalone
deployment or `--search-location westus2` for the root runner, after checking
that it is suitable for your data residency requirements. Public endpoints and
GlobalStandard inference (which may
process outside the account region) are workshop defaults, not a residency design.
The app removes its unique index, but interrupted runs may leave one. Index deletion
does not stop Search service costs: delete this lab's dedicated resource group.

```sh
az group delete --name "$RG" --yes --no-wait
AZURE_CONFIG_DIR="$SECOND_AZURE_CONFIG_DIR" az logout
rm -rf .identity-b .azure .venv __pycache__
unset SECOND_AZURE_CONFIG_DIR RESTRICTED_GROUP_ID
```

Remove any test groups/users/memberships you created via your tenant's normal process.
Do not delete a shared group or remove other users' memberships.

## Official API references

- [Graph: create a user](https://learn.microsoft.com/graph/api/user-post-users?view=graph-rest-1.0)
- [Graph: PIM activation requests](https://learn.microsoft.com/graph/api/rbacapplication-post-roleassignmentschedulerequests?view=graph-rest-1.0)
- [Graph: PIM eligibility instances](https://learn.microsoft.com/graph/api/rbacapplication-list-roleeligibilityscheduleinstances?view=graph-rest-1.0)
- [Search security trimming and trusted application filters](https://learn.microsoft.com/azure/search/search-security-trimming-for-azure-search)
- [Graph: signed-in user's transitive memberships and permissions](https://learn.microsoft.com/graph/api/user-list-transitivememberof?view=graph-rest-1.0)
- [Azure OpenAI v1 Python and Entra authentication](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/switching-endpoints)
