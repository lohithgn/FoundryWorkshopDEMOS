# 43 — Native one-time agent routines

> **Migration status:** .NET 10 file-based C# is the current lab workflow.
> Original Python sources and requirements are retained unchanged temporarily
> for staged root-runner integration. Python-era evidence below is historical
> and does not validate the .NET port; no .NET live Azure validation is claimed.

> **Current strict live status: BLOCKED.** The final root replay failed native
> agentic-token issuance despite explicit creator dispatch identity and ready,
> enabled identities. The tenant/service cause is unresolved; the historical
> standalone timer pass below does not establish reproducibility.

Creates a **native Foundry routine**, not local cron or a sleep-driven local invocation. The timer is scheduled once, 90 seconds in the future. Its action invokes a real prompt agent through the Responses API.

The script:

1. Submits `authorization.identity="creator"` as the explicit root/standalone default and verifies persisted identity before enabling the timer. `--dispatch-identity agent` is an explicit opt-in; identities never silently fall back.
2. Exercises disabled → enabled → disabled → enabled with read-back at each transition.
3. Configures the documented `Entra`/`AzureRbac` endpoint scheme and grants the agent identity Foundry User at project scope.
4. Waits at most 360 seconds for **native timer-delivery** run history; requires `trigger_type=timer`, `attempt_source=timer_delivery`, a completed phase, and a service-issued response identifier.
5. Configures an explicit native conversation, retrieves the timer's exact response through the same agent-scoped client while the agent still exists, verifies a unique synthetic marker, and records actual native response output as trace. A routine with no configured conversation legitimately has no conversation identifier; this lab uses a stored conversation to preserve retrievable evidence.
6. Disables and deletes the routine on exit, deletes the agent, and verifies absence. No recurring or unattended trigger is left running.

A manual dispatch or locally created response cannot satisfy this lab's evidence checks.

## Run

Requires the .NET 10 SDK, Azure CLI/Bicep, a signed-in Entra user with resource
creation/role-assignment rights, the model quota below, and native routine preview
availability. Preview/API access is separate from successful ARM deployment.

Restore/build each file-based entry point; help and the offline harness make no
Azure calls:

```bash
dotnet restore demo.cs
dotnet build demo.cs --no-restore
dotnet run --file demo.cs -- --help
dotnet restore test_demo.cs
dotnet build test_demo.cs --no-restore
dotnet run --file test_demo.cs
```

After an authorized Bicep deployment, run preflight and the native timer workflow
with current deployment outputs. This live workflow creates billable resources
and a one-time native routine:

```bash
dotnet run --file demo.cs -- --preflight
dotnet run --file demo.cs
dotnet run --file demo.cs -- --dispatch-identity agent
```

The final command is an explicit alternate dispatch identity and does not fall
back to creator identity. Existing deployments can instead use these mappings;
clear stale environment overrides when switching projects:

| Output | Environment |
|---|---|
| `endpoint` | `AZURE_OPENAI_ENDPOINT` |
| `projectEndpoint` | `FOUNDRY_PROJECT_ENDPOINT` |
| `deployment` | `AZURE_OPENAI_DEPLOYMENT` |
| `projectResourceId` | `FOUNDRY_PROJECT_RESOURCE_ID` |

ARM: `location`, `principalId` (User); optional account/project/deployment names and capacity 1–10. Default model `gpt-5.4-mini`, version `2026-03-17`, GlobalStandard capacity 10. Runtime uses Azure CLI Entra credentials, not keys or stored tokens. The user needs native routine permissions, and the tenant must permit issuance of agentic identity tokens.

The caller needs role-assignment create/delete permission at `projectResourceId` for the temporary agent identity (for example Owner or Role Based Access Control Administrator); routine/model preflight alone does not establish this IAM permission.

## Actual verification

**Historical Python validation only:** 24 offline tests covered creator default, dispatch identity, timer/lifecycle, response linkage, rejection of manual dispatch/local cron, and cleanup failures. This evidence does not validate the .NET implementation.

**Historical Python ARM deployment:** succeeded in `rg-foundry-agent-routines-demo`, East US 2. This does not establish a .NET deployment.

The historical Python routines list/preflight passed. Initial creation encountered an agentic-token/blueprint identity error; a later standalone timer passed, but the final root replay remained blocked. These results are not .NET validation.

A later root replay encountered the same token-issuance error despite the producer already explicitly selecting creator identity. The earlier PASS used **no creator environment override**, and inspection confirmed the SDK's typed authorization was serialized correctly. The current producer makes the choice a CLI default, submits an explicit JSON body, records that body before creation (including on failure), and checks creation read-back before enabling. This does not claim the previous service failure was caused by a missing identity field; tenant/service failures still return **BLOCKED/nonzero** without another identity being attempted.

Another root replay also failed. Direct comparison proved root-mapped and standalone settings identical, including the project, model, account endpoint, and project resource ID; neither supplied an alternate Azure configuration directory or tenant override. ARM reported `Succeeded`. These observations disprove a default/environment explanation but do not prove which blueprint-installation or Conditional Access condition caused the service error. The producer now records ARM readiness, selected runtime settings, allowlisted service request IDs, and read-only directory results for the fresh agent identity and blueprint **before cleanup**. It never retries issuance or modifies tenant policy to obtain a PASS. The exact root replay result is retained in `.azure/root-replay-terminal.log`.

**Historical Python standalone PASS:** a bounded timer completed with a native conversation and response before cleanup. It did not establish reproducible root success or validate the .NET port.

**Historical Python root replay: BLOCKED, exit 1.** Native routine creation returned HTTP **400/UserError** while acquiring the target agent's agentic identity token. ARM readiness and enabled identities were verified, but the tenant/service cause was not established. The routine, conversation, agent, and temporary project role were cleaned up. This remains a prerequisite to future live .NET verification; it is not evidence that the C# path has passed or failed.

Native response verification and final live status are recorded in `.azure/live-report.json`. If the tenant still rejects identity issuance after the documented endpoint/RBAC setup, report **BLOCKED** and resolve tenant blueprint/Conditional Access policy with an authorized administrator. The lab never substitutes local scheduling. The persisted verified response and trace remain in `.azure/live-report.json` after native cleanup; deleted agent/conversation endpoints need not remain retrievable.

## Evidence and costs

Ignored `.azure/` stores ARM outputs/transcript, live transcript, read-back identity/lifecycle, run history, linked response, and cleanup proof. No secrets/tokens are written. One fresh agent and one one-shot routine per normal invocation; no manual dispatch or periodic cron. Pausing/resuming may produce cancelled scheduler attempts in native history; only a completed timer-delivery response counts as evidence. Native trigger and temporary agent role are disabled/deleted on exit; infrastructure remains deployed.

Timer-delivered model requests and applicable routine/service operations can be
billable; failed attempts are not a free-usage guarantee. Public networking and
GlobalStandard processing outside the account region are workshop defaults.
If the process is interrupted, inspect native routine state and disable/delete
this run's trigger before leaving it unattended. Delete the dedicated lab group
after confirming the subscription/name:

```bash
az group delete --name rg-foundry-agent-routines-demo --yes --no-wait
```

Resource-group deletion does not change tenant Conditional Access, consent or
blueprint policy. Any retained Entra identity artifacts require authorized
administrator review, not broad directory deletion.

Official documentation verified:

- [Automate agents with routines](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/use-routines): timer trigger, native lifecycle, dispatch authorization, and run-history response linkage.
- [Current Python SDK](https://learn.microsoft.com/en-us/python/api/azure-ai-projects/azure.ai.projects): installed `azure-ai-projects==2.9.0` `beta.routines` signatures verified at runtime.
- [Prompt-agent region availability](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/limits-quotas-regions).
- [Native Responses retrieval with Entra authentication](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses).
