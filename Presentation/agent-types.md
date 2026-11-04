# Agent types and registration in Microsoft Foundry

An agent built with Microsoft Agent Framework is not automatically a registered
Foundry agent. Calling a model through a Foundry project endpoint does not
create an agent resource.

## Implementation and management options

These options describe where agent definitions and runtimes live, rather than
four mutually exclusive kinds of intelligence.

| Option | Who owns the definition? | Where does it run? | What appears in Foundry? |
|---|---|---|---|
| Application-owned Agent Framework agent | Application code defines instructions, tools, and orchestration. | Wherever the application runs: a laptop, server, or application hosting platform. Foundry performs model inference. | No agent entry is created automatically. The model deployment is separate from the agent. |
| Foundry-managed Prompt Agent | Foundry stores a named, versioned definition created through the portal or service APIs. | Foundry Agent Service manages agent execution; external tools can run elsewhere. | A service-managed agent definition, unless it is subsequently deleted. |
| Hosted Agent | Developers provide a code-based agent application and explicitly deploy it to Foundry. | Foundry hosts the deployed agent runtime. | A hosted agent with an agent-specific endpoint. |
| External-agent registration | The application retains its definition and runtime; Foundry stores registration metadata. | Outside Foundry, on the application's existing host. | A registered external agent for agent-scoped observability and evaluation when telemetry is configured. |

External-agent registration for observability and evaluation is currently
documented as **preview**. Registration alone does not host, proxy, or invoke
the agent. This is distinct from Control Plane custom-agent registration,
which routes traffic through an AI Gateway.

## Example: the writer-to-editor workflow

The [workflow demo](../2-workflow-agent/demo.py) uses application-owned agents:

```text
Local Python process
  Prompt -> writer -> editor -> printed output
               |         |
               v         v
       Foundry model deployment
```

- `FoundryChatClient` connects to the deployed model.
- `client.as_agent(...)` creates local agent objects named `writer` and `editor`.
  Those names are application labels, not Foundry registrations.
- `SequentialBuilder` controls the order and handoff locally.
- Both agents use the same model deployment with different instructions.
- The script does not explicitly create, deploy, or register Foundry agents.
  Their in-memory definitions disappear when the process exits.

**Expected result:** running this demo does not add `writer`, `editor`, or the
workflow to the Foundry Agents list. This is intentional.

## When should agents be registered or managed in Foundry?

- **Demos and experiments:** application-owned agents are sufficient.
- **Centralized, versioned definitions:** use Foundry-managed Prompt Agents.
- **A Foundry-hosted code runtime:** deploy a Hosted Agent.
- **An externally running application that needs Foundry agent-scoped tracing
  and evaluation:** consider external-agent registration and telemetry.

Registration and telemetry are separate steps. For external-agent observability,
the application exports OpenTelemetry spans to Application Insights connected to
the Foundry project. Foundry matches the spans' `gen_ai.agent.id` to the
registered agent. Registration without telemetry does not produce traces.

It is not necessary to register every internal role separately. A writer/editor
workflow can be represented as one application-level agent, with its internal
stages captured in traces. Choose boundaries based on independent deployment,
ownership, reuse, and monitoring needs.

## Key distinctions

- **Model deployment:** the inference resource used by an agent.
- **Agent definition:** instructions, tools, and configuration.
- **Orchestration:** the logic coordinating agents and handing off results.
- **Hosting:** where the agent application executes.
- **Registration:** a persistent record; it does not necessarily provide hosting.
- **Identity:** credentials and permissions, such as Microsoft Entra Agent ID.
  Identity provisioning is separate from Foundry agent registration.

  ## Where to see these boundaries in the labs

  - [Lab 2](../2-workflow-agent/README.md) and
    [lab 13](../13-agent-orchestrations/README.md): local Agent Framework orchestration.
  - [Lab 27](../27-FoundryIQwithKB/README.md): a persistent managed prompt agent
    connected to a service-managed knowledge base.
  - [Lab 36](../36-hosted-agent/README.md): custom code deployed to Foundry's
    hosted runtime, with actual cloud invocations and correlated telemetry.
  - [Lab 42](../42-managed-harness-tool-search/README.md): a managed Copilot
    harness, distinct from lab 28's local skill-injection harness.
  - [Lab 46](../46-fleet-governance/README.md): cross-project inventory and native
    external registration with real telemetry. Its API evidence does not
    establish portal Control Plane verification.

  Consult the [dated results](../tests.md) before claiming a live gate passed.
  Registration, identity, network isolation and runtime hosting are separate
  concerns; one does not automatically provide the others.

## Sources

- [Agent Framework: Microsoft Foundry model provider](https://learn.microsoft.com/agent-framework/integrations/by-component/model-providers/microsoft-foundry)
- [Agent Framework: Microsoft Foundry Agent Service](https://learn.microsoft.com/agent-framework/integrations/agent-services/foundry)
- [Agents in Microsoft Foundry](https://learn.microsoft.com/azure/foundry/agents/overview)
- [Register external agents for observability and evaluation (preview)](https://learn.microsoft.com/azure/foundry/agents/how-to/register-external-agent)
