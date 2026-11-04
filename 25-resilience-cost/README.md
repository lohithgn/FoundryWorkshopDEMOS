# Resilience, quotas, and cost controls

A small Foundry example makes two concurrent real inference calls after
**locally injected** throttling and timeout failures. No attempt is made to
exhaust Azure quota. A synthetic local booking tool demonstrates a response
lost after commit and an idempotent retry. A persistent outage, permanent 403,
and expired deadline demonstrate explicit bounded failure.

## Deploy and run

Requires Python 3.10+, Azure CLI/Bicep, an interactive Entra user with resource
creation/role-assignment permissions, and `gpt-4.1-mini` quota.

Run the following from this lab directory. The template deploys version
`2025-04-14` with GlobalStandard capacity 10 and grants the presenter Cognitive
Services OpenAI User. Confirm model availability/quota in `eastus2`; allow RBAC
propagation before inference.

```bash
az login
RG=rg-foundry-resilience-cost-demo
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

SDK automatic retries are disabled so every retry counts against the visible
application budget. Defaults: six model attempts, four tool attempts, at most
three attempts per operation, two inference workers, 20-second per-call timeout,
and a shared 90-second execution deadline. Each attempt reserves budget before
calling. Retry-After seconds or HTTP dates take precedence over exponential
backoff; a delay exceeding the remaining deadline fails rather than shortening
the server's requested delay.

The default run recovers both inference calls, creates exactly one synthetic
booking despite a lost response, and prints explicitly labeled expected
failures for the outage, permanent 403, and expired deadline. Unexpected live
failures propagate and exit nonzero; they are not treated as teaching successes.

## Costs and limitations

Input/output token counts come from the actual successful responses. Default
USD rates are **illustrative assumptions**, not current price quotes. Supply
your region/deployment's rates with `--input-rate` and `--output-rate`, expressed
per million tokens. Real failed requests may have billed usage that a response
does not expose; this is not invoice accounting.

Subscription quota is an allocation ceiling, not a guarantee of instantaneous
capacity. Backoff does not solve missing quota or authorization. Azure cost
budgets/alerts notify; they do not automatically impose a hard spending cap.
Use application budgets, permissions, monitoring, and operational policy.

This teaching client uses synchronous SDK timeouts, not a durable cancellation
service; stopping local waiting does not prove server-side cancellation.
The tool is local and synthetic, with in-memory idempotency state. Production
needs transactional durable keys, request-payload binding, and approval (lab 20).
Public endpoints and no API keys keep provisioning small.
GlobalStandard processing may occur outside the account region. The model
capacity is a throughput allocation, not a hard spending cap.

## Validate and clean up

```bash
python -m unittest discover -s . -p 'test_*.py' -v
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
az group delete -n "$RG" --yes
```

- [Foundry quotas and limits](https://learn.microsoft.com/azure/foundry/openai/quotas-limits)
- [Azure retry pattern](https://learn.microsoft.com/azure/architecture/patterns/retry)
- [Backlog F025](../BACKLOG.md#f025---microsoft-foundry-resilience-quotas-and-cost-controls)
