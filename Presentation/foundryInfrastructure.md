# Foundry private networking: presenter discussion

## Coverage and purpose

This is an architecture discussion, **not an implemented private-network lab**.
The [lab catalog](../demos.md) does not include dedicated private-network
deployment or end-to-end network-isolation verification. Existing public
workshop endpoints must not be presented as private merely because they use
Entra authentication or managed identities.

The teaching goal is to distinguish three separate controls:

- **Authentication and authorization:** which user or workload may call an API.
- **Inbound isolation:** which networks can reach the service endpoint.
- **Outbound connectivity:** which models, data sources and tools the agent
  runtime can reach.

## Architecture to explain

```text
Presenter / application on an approved network
  -> private endpoint + private DNS -> Foundry account / project
  -> Agent Service delegated subnet -> approved model, data and tool endpoints
```

Foundry Agent Service's documented Standard Setup with private networking
uses delegated-subnet integration and private endpoints. Connected resources
such as Storage, Search and Cosmos DB need their own network configuration;
a private Foundry endpoint does not automatically make those services private.
Portal-created setups do not automatically create every connected-resource
private endpoint. Verify the deployed topology, DNS and public-access settings.

## What to verify before claiming it works

| Surface | Required evidence |
| --- | --- |
| Client access | An authorized request succeeds from the approved network; the equivalent public-network request is denied. |
| DNS | The intended service hostnames resolve through the configured private DNS from the client and runtime networks. |
| Data and model access | The actual agent can reach each configured dependency with both network access and the required RBAC. |
| Tool traffic | Each tool is tested separately; public web grounding and external APIs can require approved outbound access. |
| Hosted code | Image pulls, deployment endpoints and runtime dependencies follow the hosting-specific firewall requirements. |
| Operations | Logs, traces and diagnostics remain reachable through the intended administration path. |

These are proposed verification criteria, not results from this repository.
Private networking does not grant data permissions, replace source ACLs,
guarantee every tool supports isolation, or make public-web retrieval an
offline/private-corpus operation. Disabling public access before validating
the administrative path can also block the presenter.

## Official references

Review current region, setup, subnet and tool restrictions before designing a
deployment; this repository does not certify them.

- [Set up private networking for Foundry Agent Service](https://learn.microsoft.com/azure/foundry/agents/how-to/virtual-networks)
- [Foundry Agent Service networking deep dive](https://learn.microsoft.com/azure/foundry/agents/concepts/agents-networking-deep-dive)
- [Configure network isolation for Microsoft Foundry](https://learn.microsoft.com/azure/foundry/how-to/configure-private-link)