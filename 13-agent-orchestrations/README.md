# Microsoft Agent Framework - Four tiny orchestration samples

Four independent examples share one Foundry model deployment. Each pattern is
implemented in its own small Python file; `demo.py` selects and runs them.
Agent Framework orchestrates **locally**; Foundry performs model inference.

| Pattern | Sample | Super-simple scenario |
| --- | --- | --- |
| Sequential | [sequential.py](./sequential.py) | Writer drafts an invitation, editor shortens it |
| Concurrent | [concurrent_sample.py](./concurrent_sample.py) | Independently suggest a title and a snack |
| Group Chat | [group_chat.py](./group_chat.py) | Planner and reviewer refine a two-item agenda |
| Magentic | [magentic.py](./magentic.py) | Manager asks title/checklist specialists for a tiny workshop card |

`concurrent_sample.py` avoids shadowing Python's standard-library `concurrent`
package. "Magentic" is the framework's manager-driven pattern, not a typo for
"magnetic." No web research, tools, hosted agent deployment, database, or UI.

## Deploy

Requires Python 3.10+, Azure CLI with Bicep, an interactive Azure user with resource
creation and role-assignment permissions, and Foundry project/model availability.
Bicep creates one keyless account, project, `gpt-4.1-mini` (`2025-04-14`, Global
Standard) deployment, and presenter Foundry User RBAC. Endpoints are public.

### Recommended: saved lab lifecycle

Run from this folder in Bash/Zsh. Give each student a different `STUDENT_ID`
when sharing a subscription:

```bash
az login
az account set --subscription "<subscription-id>"
STUDENT_ID="student01"
python3 deploy_lab.py --prefix "rg-foundry-${STUDENT_ID}" --location eastus2
export FOUNDRY_PROJECT_ENDPOINT="$(python3 -c 'import json; print(json.load(open(".azure/lab-deployment.json"))["outputs"]["projectEndpoint"]["value"])')"
export AZURE_OPENAI_DEPLOYMENT="$(python3 -c 'import json; print(json.load(open(".azure/lab-deployment.json"))["outputs"]["deployment"]["value"])')"
```

The script installs dependencies, runs offline tests, deploys, then runs all four
live samples (billable). It generates a random account name **once**, saves it
in `.azure/lab-deployment.json` **before** testing/deploying, and passes that name
to Bicep. Repeat the same command after a failure or to update the deployment:
it reuses the saved name rather than creating another account.

Do not delete the state file or run concurrent deployments from the same lab
folder. It records the subscription, resource group, location, account name, and
outputs. Do not copy it between students. Existing groups without matching local
state and ownership tags are rejected; changing target or location requires
cleanup and a new lifecycle.

For a **clean rebuild**, delete the tracked group first, then explicitly rotate
the account name:

```bash
python3 cleanup.py
python3 deploy_lab.py --prefix "rg-foundry-${STUDENT_ID}" --location eastus2 --start-fresh
```

`--start-fresh` refuses while the group exists. Normal deployment refuses a
lifecycle marked deleted by cleanup. No restore or purge is performed, and no
purge permissions are needed. Re-export endpoints after rebuilding.

Deleting a Foundry account leaves a soft-deleted name reserved for 48 hours.
The new lifecycle avoids that name; it does not bypass subscription model quota
or service availability. If deletion was performed outside `cleanup.py`, verify
the group is gone and use `--start-fresh`. A legacy script state without
`accountName` also requires cleanup followed by `--start-fresh`; it is never
silently migrated into an extra live account.

If deployment reports `RequestConflict` / "Another operation is in progress",
allow the current Azure operation to finish, then retry the same deployment
command **without `--start-fresh`**. Keep the saved state; do not clean up merely
to retry a partial deployment. The template creates the project before the
model deployment to avoid overlapping account child operations.

### Manual Bicep deployment using the same lifecycle

To deploy without installing dependencies or running samples, prepare the saved
name first. Preparation reads Azure account/group information but creates no
Azure resources. Use the same student ID, subscription, and location on retries:

```bash
python3 deploy_lab.py --prefix "rg-foundry-${STUDENT_ID}" --location eastus2 --prepare-only
az provider register --namespace Microsoft.CognitiveServices --wait
STUDENT_ID="student01"
RESOURCE_GROUP="rg-foundry-${STUDENT_ID}-agent-orchestrations-demo"
ACCOUNT_NAME="$(python3 -c 'import json; print(json.load(open(".azure/lab-deployment.json"))["accountName"])')"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags managedBy=foundry-lab-script lab=13-agent-orchestrations SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create -g "$RESOURCE_GROUP" -n demo \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location=eastus2 accountName="$ACCOUNT_NAME"
export FOUNDRY_PROJECT_ENDPOINT="$(az deployment group show -g "$RESOURCE_GROUP" -n demo \
  --query properties.outputs.projectEndpoint.value -o tsv)"
export AZURE_OPENAI_DEPLOYMENT="$(az deployment group show -g "$RESOURCE_GROUP" -n demo \
  --query properties.outputs.deployment.value -o tsv)"
```

For a manual clean rebuild, run `python3 cleanup.py`, then prepare with both
`--start-fresh --prepare-only` and repeat the manual commands. Manual deployment
outputs are read from Azure, not written to the local state; `cleanup.py` still
tracks and deletes the prepared group.

This lifecycle experiment is limited to Lab 13's script and the prepared manual
commands above. The repository-wide `deploy.py` and direct Bicep deployments
without `accountName` retain their existing deterministic naming behavior.
Other labs are unchanged. Resources created using the old manual instructions
are not adopted: inspect and delete their old group separately if necessary.

## Run one sample at a time

```bash
python3 -m venv .venv
source .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py --pattern sequential
python demo.py --pattern concurrent
python demo.py --pattern group-chat
python demo.py --pattern magentic
# Or run all four, also the default used by the root runner:
python demo.py --pattern all
```

### Talk track

1. **Sequential:** writer -> editor is a fixed handoff. The editor receives prior
   output; this is not two independent prompts.
2. **Concurrent:** both agents receive the original input independently.
   Calls overlap; there is no writer/editor dependency.
3. **Group Chat:** both agents see the shared discussion. A round-robin selector
   chooses speakers, and a termination rule stops after four assistant messages,
   with a five-round safety cap.
4. **Magentic:** a model-driven manager plans, chooses specialists, checks progress,
   and produces the final card. The sample bounds coordination to five rounds,
   two stalls, and a reset limit of one. Unlike the sequential pattern, which
   specialist is called next is decided by the model.

Participant output and the final workflow output are printed. Manager choices
and exact wording aren't deterministic; limits keep this a small teaching sample,
not a promise that every live run will call specialists in the same order.
Each workflow has a five-minute local deadline; an empty/non-agent output fails.
All four can take longer than one short presentation slot; pick one at a time.

## Validate, costs, and cleanup

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
python -m unittest discover -s . -p 'test_*.py' -v
python3 cleanup.py
```

Tests execute the **real four framework graphs** with an offline chat client.
They verify the sequential handoff, actual overlapping concurrent calls, bounded
group conversation, and manager coordination of both specialists. They do not
call Foundry or substitute local orchestration for the real builders.
Lifecycle tests use mocked Azure commands and temporary state to verify saved
names, retries, failure recovery, cleanup, explicit rotation, and ownership
checks; they create no Azure resources.

Each agent/manager step incurs model usage charges. Magentic needs extra planning
and progress-check calls. For 403, allow presenter RBAC to propagate; for 404,
use **projectEndpoint** and the correct deployment name. A 429 may require
increasing quota or presenting fewer patterns, not retrying continuously.

- [Sequential](https://learn.microsoft.com/agent-framework/workflows/orchestrations/sequential?pivots=programming-language-python)
- [Concurrent](https://learn.microsoft.com/agent-framework/workflows/orchestrations/concurrent?pivots=programming-language-python)
- [Group Chat](https://learn.microsoft.com/agent-framework/workflows/orchestrations/group-chat?pivots=programming-language-python)
- [Magentic](https://learn.microsoft.com/agent-framework/workflows/orchestrations/magentic?pivots=programming-language-python)
- [Foundry soft delete, recovery, and purge](https://learn.microsoft.com/azure/ai-services/recover-purge-resources)
- [Feature F013](../BACKLOG.md#f013---microsoft-agent-framework-four-orchestration-patterns)
