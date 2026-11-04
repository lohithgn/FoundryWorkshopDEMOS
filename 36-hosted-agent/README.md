# Lab 36 - Your custom code on the Foundry hosted runtime

## What this proves

A custom Python agent runs in a **Foundry-hosted container**, not in the local
demo or UI process. A real GPT-5.4 mini response proposes a pricing function call;
the container validates it, executes a read-only USD 25-per-attendee tool, and
returns a second, schema-constrained model response. Application code checks
the answer against the actual tool result.

The current hosted-agent API provisions the version automatically, creates an
agent identity, and routes requests through an authenticated Invocations endpoint
using container protocol **2.0.0**. There is no legacy application deployment or
local workflow fallback. The sample is framework-independent custom code.

The simple UI shows the actual quote, model response IDs, function call ID,
cloud sandbox instance, session marker/counter, and trace ID. A repeated session
retains its filesystem state; a new session starts independently. These are two
sessions of the same authenticated presenter, **not a two-user isolation test**.
The live gate also requires actual ingested Azure Monitor telemetry.

## Prerequisites

- Python 3.10+, Azure CLI with Bicep, and `az login` as a user who can create
  resources and role assignments in the selected subscription.
- Hosted-agent availability in `eastus2`, GPT-5.4 mini Global Standard quota
  (10 capacity units), and permission to run Azure Container Registry Tasks.
- A current Foundry project supporting the hosted-agent APIs. The template creates
  a dedicated account/project. Preview behavior and regional availability can change.

Azure builds the Linux/AMD64 image using ACR Tasks: **local Docker is not needed**.
No API keys, registry admin credentials, or user tokens are baked into the image.
The browser never receives an Azure access token.

## Deploy and test

From this directory:

```bash
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
.venv/bin/python deploy_lab.py --prefix rg-foundry
```

Only resource group **rg-foundry-hosted-agent-demo** is deployed. The command
compiles/deploys the Bicep template, runs unit tests, builds a uniquely tagged
container, creates and routes a hosted version, and runs live tests.
To provision infrastructure without publishing, use `--infra-only`.
The shared standalone-runner default prefix is `rg-foundry-student`; use the
explicit prefix above to reproduce the presenter's deployment.

The standalone wrapper records ownership in `.azure/lab-deployment.json` and
requires matching `managedBy`/`lab` group tags before reusing a group. It refuses
an unrecorded group, even if a separate root-runner deployment created it.
Do not pre-create the reference group below before running the wrapper.

The equivalent tagged resource-group creation command is:

```bash
az group create --name rg-foundry-hosted-agent-demo --location eastus2 --tags managedBy=foundry-lab-script lab=36-hosted-agent SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
```

To retest the persistent deployed version without rebuilding:

```bash
.venv/bin/python -m unittest -v
.venv/bin/python demo.py --live-test
```

To publish changes to the runtime, run `.venv/bin/python demo.py --publish`
before retesting. Publishing a version is a paid cloud build and deployment,
not part of an ordinary UI request.

## See it work

```bash
.venv/bin/python ui.py
```

Open **http://127.0.0.1:8036**:

1. Run for three attendees: observe a verified USD 75 quote.
2. Change to four and run again: USD 100, same marker, incremented visit count.
3. Select **New isolated session**, then run: a new marker and visit count 1.
4. Inspect the returned cloud execution IDs. The UI only proxies the authenticated
   cloud request; stopping the UI does not delete or stop the deployed agent.

The UI deliberately binds only to loopback and rejects cross-origin requests.
It uses the presenter's Azure CLI credentials, so do not expose it as a shared
web application or use it for production authentication.

In [Foundry](https://ai.azure.com), select the deployed `hosted-agent` project,
open **Agents**, and inspect `hosted-workshop-pricer`, its version, and traces.
Application Insights is connected using **ProjectManagedIdentity** with local
ingestion authentication disabled.

## Gates and retained evidence

Ignored `.azure/` state contains:

- `deployment.json`: actual resource outputs.
- `agent.json`: image tag, immutable agent version and endpoint read-back.
- `live-report.json`: live pricing/tool results, same-session continuity,
  separate-session state, and queried telemetry. `passed` is true only after
  all gates succeed. A timeout or cloud failure is not replaced with a local result.

Provisioning waits at most 20 minutes and trace ingestion at most 15 minutes.
First invocation can incur a cold start. A coherent answer without tool evidence,
wrong pricing, missing state isolation, or absent telemetry fails the live gate.
The sample doesn't prove cross-user authorization, scale under load, or automatic
state recovery after idling; those are outside this small exercise.

## Cost and cleanup

Paid resources include model tokens, hosted runtime compute/session storage,
ACR Basic and cloud builds, and Azure Monitor ingestion/retention. Resources remain
deployed for presentation; idle behavior is not a hard spending cap.

After stopping the local UI, delete **only this lab's** group when finished:

```bash
az group delete --name rg-foundry-hosted-agent-demo --yes --no-wait
```

References:
[Hosted agents](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agents),
[current deployment flow](https://learn.microsoft.com/azure/foundry/agents/how-to/deploy-hosted-agent),
[Invocations SDK](https://learn.microsoft.com/python/api/overview/azure/ai-agentserver-invocations-readme),
[Entra-authenticated traces](https://learn.microsoft.com/azure/foundry/observability/how-to/trace-ingestion-entra-authentication).
