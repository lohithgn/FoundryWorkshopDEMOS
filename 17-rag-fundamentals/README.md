# 17 — RAG fundamentals before Foundry IQ

The application chunks three synthetic documents, calls a real Foundry-hosted embedding
deployment, uploads vectors to **real Azure AI Search**, retrieves evidence, then calls
the Foundry-hosted chat deployment. No managed IQ, local vector approximation, API keys,
or simulated cloud responses are used in the live runner.

## Prerequisites and deployment

Python 3.10+, Azure CLI with Bicep, an Azure subscription, and `az login` as a user.
The deployer needs permission to create Cognitive Services/Search resources and role
assignments (for example Contributor plus User Access Administrator). Register
`Microsoft.CognitiveServices` and `Microsoft.Search` if necessary. The selected region
must offer `gpt-4.1-mini` version `2025-04-14` (GlobalStandard) and
`text-embedding-3-small` version `1` (Standard), with quota for capacity 10 each.
Check regional availability/quota before deploying; adjust the Bicep model/SKU if needed.

If Search provisioning returns `InsufficientResourcesAvailable`, keep the model
resource's existing region and retry the same deployment with
`--parameters principalId="$PRINCIPAL_ID" location=eastus2 searchLocation=westus2`.
`searchLocation` defaults to `location`; changing it does not move an existing Search
service. Choose an available region from the
[Search regions list](https://learn.microsoft.com/azure/search/search-region-support).
Cross-region retrieval can add latency and data-transfer charges. Do not change the
model account's location after it has been created.

From this directory:

```sh
az login
az account set --subscription YOUR_SUBSCRIPTION_ID
export RG=rg-lab17
export LOCATION=eastus2
az group create --name "$RG" --location "$LOCATION" --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv)
mkdir -p .azure
az deployment group create --resource-group "$RG" --name lab17 \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location="$LOCATION" \
  --query properties.outputs -o json > .azure/outputs.json
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
export AZURE_OPENAI_ENDPOINT=$(python -c 'import json; print(json.load(open(".azure/outputs.json"))["endpoint"]["value"])')
export AZURE_OPENAI_DEPLOYMENT=$(python -c 'import json; print(json.load(open(".azure/outputs.json"))["deployment"]["value"])')
export AZURE_OPENAI_EMBEDDING_DEPLOYMENT=$(python -c 'import json; print(json.load(open(".azure/outputs.json"))["embeddingDeployment"]["value"])')
export AZURE_SEARCH_ENDPOINT=$(python -c 'import json; print(json.load(open(".azure/outputs.json"))["searchEndpoint"]["value"])')
python demo.py
```

Role assignment propagation can take minutes; retry a 403 after checking roles.
Bicep assigns Foundry User (role ID `53ca6127-db72-4b80-b1b0-d745d6d5456d`,
formerly Azure AI User), Search Service Contributor (index schema)
and Search Index Data Contributor (upload/read/delete). Local auth is disabled.
`endpoint` is the inference service root; the client appends `/openai/v1/`.
`deployment` and `embeddingDeployment` are deployment names, not project URLs.
There is no project/agent resource or required project endpoint. These outputs match
the root runner's environment mapping; this directory can also run independently.

## What to inspect

`python demo.py` is noninteractive. Both 12-word and 48-word configurations use exactly
the same documents, four questions, labels, embeddings model and top-3 limit.
Keyword retrieval uses `search_text`, vector retrieval uses `VectorizedQuery`, and
hybrid sends both to Search (which combines rankings). Inspect printed passages and
references **before** each model answer. Compare the final document recall@3 and
MRR@3 table; duplicate chunks count toward the three slots. Labels evaluate retrieval
only and are never passed to generation. Three answerable questions contribute to
metrics; the fourth is deliberately unanswerable.

Small chunks can separate a condition from its remedy; larger chunks add irrelevant
context. There is no guaranteed winner on a tiny corpus. Search scores from different
modes are not comparable confidence probabilities. Pure vector search may return
irrelevant passages for an unknown question, so a top-k hit alone is not evidence.
The model must explicitly return `Insufficient evidence.` for the unknown question.
The runner fails if it does not. It checks every citation against retrieved IDs and
every quote against exact passage text. This catches fabricated references, not every
possible semantic hallucination; manually judge whether quotes support each answer.

Application: chunking, labels, embedding orchestration, prompts, citation checks.
Foundry model inference: embeddings and chat generation.
Search: durable index, keyword/vector ranking and hybrid fusion.

## Offline tests and Bicep validation

No credentials, network, or installed SDKs are needed for these unit tests:

```sh
python3 -m unittest -v test_demo.py
mkdir -p .azure
az bicep build --file infra/main.bicep --stdout > .azure/template.json
```

Tests cover lossless chunking, labeled metrics, distinct retrieval modes, citation
and quote rejection, abstention, model request boundaries and bounded indexing wait.
They do not prove live service availability, model quality, or Azure permissions.

## Live rerun (2026-10-26)

Reused `rg-foundry-rag-fundamentals-demo`, with Foundry in East US 2 and Basic
Search in West US 2. The full run exited 0 across all 24 combinations: two chunk
sizes, three retrieval modes, and four questions. Independently parsed 18
nonempty cited/quoted answers and six abstentions with empty citations/quotes.
The live runner validated citations and quotes against actual retrieved passages.

Recall@3 was 1.0 in every configuration. MRR@3 was 0.833 for 12-word keyword
chunks and 1.0 otherwise, matching the previous run. Index-name readback before
and after the run confirmed no new indexes remained. Six offline tests passed
separately. Evidence is retained in ignored `.azure/live-2026-10-26.log` and
`.azure/live-2026-10-26.json`.

## Costs and cleanup

Basic Search charges continuously for one replica/partition, including when idle.
The run embeds the corpus twice plus four questions and makes 24 small chat calls;
token usage, model deployment type, currency and regional prices determine cost.
No Provisioned SKU or semantic ranker is used. Check the Azure pricing calculator;
do not leave the Search service running between workshops.
Both services have public endpoints. GlobalStandard chat processing is not
restricted to the account region; the regional embedding deployment has a
different deployment type. Use only the bundled synthetic documents/questions.

Each run creates unique indexes and removes them in `finally`; an interrupted process
may leave an index, and deleting indexes does **not** stop Search charges.
Delete the dedicated resource group to stop resource charges:

```sh
az group delete --name "$RG" --yes --no-wait
rm -rf .azure .venv __pycache__
```

## Verified API references

- [Azure OpenAI v1 Python endpoints and Entra authentication](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/switching-endpoints)
- [Vector and hybrid queries](https://learn.microsoft.com/azure/search/vector-search-how-to-query)
- [Python vector quickstart](https://learn.microsoft.com/azure/search/search-get-started-vector)
