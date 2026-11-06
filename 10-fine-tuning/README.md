# Supervised fine-tuning: classification and Polish domain knowledge

This lab has two independent examples:

1. **Managed Azure classification:** the original tiny support-ticket example,
   described below. Its completed training does not prove improved accuracy.
2. **Local Phi knowledge adaptation:** [Polish culture and dishes](#example-2-local-phi-for-polish-culture-and-dishes),
   with real before/after inference, a trained adapter, and retained evaluation.

## Example 1: managed support-ticket classification

A short setup demo of **managed supervised fine-tuning (SFT)**: upload 12 tiny
chat-format examples and submit one training epoch for `gpt-4.1-mini-2025-04-14`.
The task is returning exactly BILLING, TECHNICAL, or ACCOUNT for a support ticket.
No GPU cluster, custom training code, reinforcement learning, or UI is needed.

**Training is asynchronous, not a guaranteed fast live demo.** Submit ahead of
the presentation and retain the job ID. By default the script reports submission
status, not successful training. Use `--job-id` to inspect/resume without paying
for another job, and `--wait` to poll for up to 30 minutes.
The latest job JSON, including its ID and eventual `fine_tuned_model`, is saved
to ignored `.azure/fine-tuning-job.json` immediately after submission and on
each poll. Use `--report <path>` to choose a different report location.

## Deploy

Requires Python 3.10+, Azure CLI with Bicep, an Azure user with resource creation
and role-assignment permissions, fine-tuning access/quota, and a supported Global
training region for the model. Check the official matrix before presenting.
East US 2 is a supported Global training region in the referenced documentation;
Standard fine-tuned inference has a different regional matrix.

The template creates one keyless Foundry account, a base `gpt-4.1-mini`
(`2025-04-14`, Global Standard) deployment for optional comparison, and presenter
Foundry User RBAC. Training uses the **model ID**, not this deployment name.
Python submits the data-plane job; Bicep provisions its prerequisites.

Run from this folder:

```bash
az login
az account set --subscription "<subscription-id>"
az provider register --namespace Microsoft.CognitiveServices --wait
RESOURCE_GROUP="rg-foundry-fine-tuning-demo"
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
az group create --name "$RESOURCE_GROUP" --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group "$RESOURCE_GROUP" --name demo \
  --template-file infra/main.bicep \
  --parameters principalId="$PRINCIPAL_ID" location=eastus2
export AZURE_OPENAI_ENDPOINT="$(az deployment group show -g "$RESOURCE_GROUP" \
  -n demo --query properties.outputs.endpoint.value -o tsv)"
```

## Run and present

```bash
python3 -m venv .venv
source .venv/bin/activate
python -m pip install -r requirements.txt
python demo.py                      # Submits a NEW paid job
python demo.py --job-id "<job-id>"   # Only inspect an existing job
python demo.py --job-id "<job-id>" --wait
```

Show the JSONL messages, one-epoch supervised method, returned job ID/status, and
eventual fine-tuned model ID. Global training can process data outside the account
region; all included data is synthetic and non-sensitive. Twelve examples meet
the minimum-size demonstration requirement, **not** a production-quality dataset.
No improvement is claimed; use held-out validation/test data for real experiments.

### Submission payload compatibility

For the OpenAI v1 API, Global training uses the exact API value
`extra_body={"trainingType": "GlobalStandard"}`. The former value `"Global"`
was rejected with HTTP 400 `invalidPayload` and the misleading message
`The fineTuningJob field is required.` The corrected request keeps the existing
top-level `model`, `training_file`, and supervised `method`; it does not add a
`fineTuningJob` wrapper. A live corrected submission was accepted with
`trainingType: "globalStandard"` in the returned job.

### Completed paid training on 2026-10-26

The real one-epoch job succeeded in East US 2 under
`rg-foundry-fine-tuning-demo`. Independent Azure readback confirmed exactly one
paid job, no training error, **663 trained tokens**, and a nonempty results file.
All seven local regression tests and Bicep build/lint passed.

| Evidence | Retained value |
| --- | --- |
| Base model | `gpt-4.1-mini-2025-04-14` |
| Training type | `GlobalStandard` (returned as `globalStandard`) |
| Job ID | `ftjob-1f5660c4acff48438874163995f91d99` |
| Status | `succeeded` |
| Fine-tuned model ID | `gpt-4.1-mini-2025-04-14.ft-1f5660c4acff48438874163995f91d99-workshop` |
| Training file | `file-9dd76966ed5c41d480f32d227739cdb5` |
| Results file | `file-d594c3b94d3e4ecbbb0cda03301db818` |
| Resource endpoint | `https://foundry-tuning-uf7l22buq6vcg.openai.azure.com/` |

The job took approximately **71 minutes**, including GPU queueing and model
finalization. Two 30-minute local polling windows timed out while cloud training
continued; resuming the same job did not create another paid job.
With the endpoint set as above, inspect the completed job without resubmission:

```bash
python demo.py --job-id ftjob-1f5660c4acff48438874163995f91d99 --wait
```

The final job/model JSON is in `.azure/fine-tuning-job.json`. Independent
verification, events, downloaded results CSV, and live transcripts are retained
locally in ignored `.azure/`, including `live-testing-summary.json` and
`live-training-finalization.log`.
The trained model and account remain available; deleting the account/resource
group removes that retained model. No trained-model inference deployment or
held-out comparison was performed, and no accuracy improvement is claimed.

### Optional: deploy the trained model and try it

After a job **succeeds**, copy its `fine_tuned_model` value:

```bash
ACCOUNT_NAME="$(az deployment group show -g "$RESOURCE_GROUP" -n demo \
  --query properties.outputs.accountName.value -o tsv)"
az cognitiveservices account deployment create -g "$RESOURCE_GROUP" -n "$ACCOUNT_NAME" \
  --deployment-name workshop-tuned --model-format OpenAI \
  --model-name "<fine-tuned-model-id>" --model-version "1" \
  --sku-name GlobalStandard --sku-capacity 1
```

Then use the same authenticated OpenAI v1 client pattern as `demo.py`:

```python
response = client.chat.completions.create(
    model="workshop-tuned",
    messages=[
        {"role": "system", "content": "Classify the support ticket. Reply with exactly BILLING, TECHNICAL, or ACCOUNT."},
        {"role": "user", "content": "Could you help me recover access to my profile?"},
    ],
)
print(response.choices[0].message.content)
```

This snippet belongs inside the existing client context. Deployment availability,
quota, and hosting charges are separate from training. This optional deployment
is deliberately not created before a trained model exists.

## Validate, costs, and cleanup

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep lint --file infra/main.bicep
python -m unittest discover -s . -p 'test_*.py' -v
```

Every run **without `--job-id` submits a new billable job**, including reruns of
the root all-demo runner. Files/jobs remain on the account for inspection.
403 usually indicates RBAC propagation or missing training permissions; a model
or training-region error requires checking fine-tuning access, not changing the
inference deployment name. Failed/cancelled jobs and polling timeouts exit nonzero.
Stopping the local process does not cancel cloud training.

To cancel a still-running job before deleting the dedicated group, use
`client.fine_tuning.jobs.cancel("<job-id>")` in the authenticated client context.
Then delete the group (including any optional trained deployment):

```bash
az group delete --name "$RESOURCE_GROUP" --yes
```

- [Supervised fine-tuning API and workflow](https://learn.microsoft.com/azure/foundry/openai/how-to/fine-tuning)
- [Supported training/deployment regions and costs](https://learn.microsoft.com/azure/foundry/fine-tuning/overview)
- [Feature F010](../BACKLOG.md#f010---microsoft-foundry-supervised-fine-tuning)

## Example 2: local Phi for Polish culture and dishes

Teach a small generative model regional food and cultural facts, rather than
returning classification labels. This example uses **Phi-3.5-mini-instruct,
3.8 billion parameters**, in a pinned community 4-bit MLX conversion, and trains
a QLoRA adapter locally on an Apple silicon Mac. It does **not** call Azure,
change the managed example, or demonstrate Foundry-hosted Phi fine-tuning.
It is a small model, not a sub-billion-parameter model.

The [source-backed dataset](./polish_culture.json) covers ten foods and six
traditions: fuczki, proziaki, siemieniotka, wodzionka, fafernuchy, cebularz,
rogal swietomarcinski, hreczanyki, kacapoly, oscypek, Krakow szopki, flower
carpets, Koniakow lace, Zalipie, Lajkonik and Dziady. Answers are original short
summaries, with source URLs from UNESCO, WIPO, Poland's agriculture ministry,
and cultural/food references. Questions and answers are in **English** to
isolate factual recall rather than Polish-language fluency.

### Run the complete experiment

Requires an Apple silicon Mac, a compatible MLX/macOS/Python installation,
internet for the first public-model download, and several GB of free disk/RAM.
Tested with Python 3.13 on an M5 Pro with 48 GB RAM; MLX reported about
**3.11 GB peak training memory**. No Azure credentials or paid service are used.
The model is downloaded from Hugging Face; training data and inference remain
local. Python remote model code is disabled.

From this lab folder, use a fresh output directory:

```bash
python3 -m venv .venv
source .venv/bin/activate
python -m pip install -r requirements.txt -r requirements-polish.txt
python polish_demo.py --stage all --output .azure/my-polish-run
```

This freezes inputs, runs the **unadapted baseline first**, trains, evaluates
the final adapter using identical questions/decoding, and checks improvement.
It exits nonzero unless concept-rubric accuracy improves by at least **25
percentage points** and control-question accuracy does not decrease. It does
not declare success merely because training loss decreases.

For individual stages or an interrupted run:

```bash
python polish_demo.py --stage prepare  --output .azure/my-polish-run
python polish_demo.py --stage baseline --output .azure/my-polish-run
python polish_demo.py --stage train    --output .azure/my-polish-run
python polish_demo.py --stage evaluate --output .azure/my-polish-run
python polish_demo.py --stage compare  --output .azure/my-polish-run
```

Do not run `prepare` again after `all`. Completed generation/training stages
refuse overwrites; use a new directory to retrain, including after a partial
training failure. A completed comparison can be rechecked without retraining.
If the dataset, rubric, config or generated training splits change, create a
new run rather than mixing incompatible reports.

### Experiment design

- **80 training rows:** 64 domain questions plus 16 independent
  [general-task replay examples](./polish_replay.json) to reduce forgetting.
- **20 validation rows:** 16 domain paraphrases plus four general tasks.
  Only these splits enter the trainer; no test JSONL is created.
- **Confirmation:** [16 different domain questions and six new controls](./polish_confirmation.json).
  They were frozen before the final training round. They test **new wording
  about taught entities/facts**, not unseen facts or broad cultural expertise.
- [Fixed training config](./polish_lora.json): rank 8, last eight layers, batch
  size 2, 160 steps, learning rate `5e-5`, assistant-only loss, seed 42.
  The final 160-step adapter is used, not a test-selected checkpoint.
- Base and adapted generation use the same system prompt, greedy decoding,
  100-token budget, and Phi end-of-turn stop token. The older conversion's
  config omits that stop token, so the script explicitly adds it.
- Reports retain input hashes, model revision, library versions, full answers,
  rubric details, adapter hash and training log. The base weights are frozen;
  approximately **3.146 million parameters (0.082%)** are trained.

### Verified results: 2026-11-05

Real training and inference completed locally. A fresh, one-command end-to-end
repeat produced **identical final answers** to the staged run. On this machine,
the final training took about **22 seconds** after downloading/loading the
model; the initial model download took about six minutes. These are observations,
not latency guarantees.

| Measure on the final confirmation set | Base Phi | Phi + adapter |
| --- | --- | --- |
| Automatic concept-rubric passes | 2/16 (12.5%) | 15/16 (93.75%) |
| Stricter whole-answer audit | 1/16 (6.25%) | 11/16 (68.75%) |
| General-task control rubric | 6/6 | 6/6 |

**The verified improvement is narrow, not 94% factual reliability.** The
automatic score checks required concepts and a few forbidden claims; it can
accept contradictions or extra hallucinations. The stricter
[answer-by-answer audit](./polish-results/answer-audit.json) rejects those
answers too. It is an assistant qualitative audit, not an independent human
or blinded evaluation. Six controls cannot establish general non-regression.

Examples from the retained real transcripts:

| Topic | Before | After |
| --- | --- | --- |
| Fuczki | Incorrectly describes filled dumplings | Sauerkraut pancakes, Lemko/Bieszczady tradition |
| Wodzionka | Incorrectly describes a distilled spirit | Silesian stale-bread soup |
| Cebularz | Invents cheese/herb toppings | Onion and poppy-seed flatbread from Lublin |
| Lajkonik | Misses the rider and animal | Tatar rider with a hobby horse |

Failures remain: fafernuchy is misidentified; the rogal answer invents unrelated
details; oscypek's region is wrong; the szopka answer adds an unsupported
wreath description; and the lace answer contradicts itself about a crochet
hook being a hand tool. These five answers fail the stricter audit.

The exploratory 120-step adapter improved its original rubric from 4/16 to
13/16 but regressed on a translation control (4/4 to 3/4), so it was **rejected**.
Its [failed comparison](./polish-results/exploratory-comparison.json) is retained.
Replay examples and reduced adaptation were then added; the final round used
the newly frozen confirmation questions, not a selected checkpoint from the
exploratory test. The end-to-end repeat is a reproducibility check, not another
independent benchmark.

A diagnostic [recheck of the four original controls](./polish-results/exploratory-control-recheck.json)
also passes 4/4 with the final adapter, including the previously broken
cat-to-Spanish translation. These are development controls, not additional
unseen confirmation cases.

### Inspect or reuse the trained adapter

Checked-in evidence, without model weights:

- [Baseline full answers](./polish-results/baseline.json)
- [Adapted full answers](./polish-results/tuned.json)
- [Computed comparison](./polish-results/comparison.json)
- [Whole-answer audit and reasons](./polish-results/answer-audit.json)
- [Frozen manifest](./polish-results/manifest.json),
  [training metadata](./polish-results/training.json) and
  [training log](./polish-results/training.log)

The verified local adapter remains in
`.azure/polish-final/adapters/adapters.safetensors` (about 12 MB), with the
configuration alongside it. Base weights remain in the Hugging Face cache.
The adapter alone is not a standalone model; load the same pinned base:

```python
from mlx_lm import generate, load
from mlx_lm.sample_utils import make_sampler

model, tokenizer = load(
    "mlx-community/Phi-3.5-mini-instruct-4bit",
    revision="7b2052fd882fe017300d4d42a4eb06a27b816af4",
    adapter_path=".azure/polish-final/adapters",
    tokenizer_config={"trust_remote_code": False},
)
tokenizer.add_eos_token("<|end|>")
prompt = tokenizer.apply_chat_template(
    [{"role": "system", "content": "Answer concisely in English."},
     {"role": "user", "content": "What is wodzionka?"}],
    tokenize=False, add_generation_prompt=True,
)
print(generate(model, tokenizer, prompt=prompt, max_tokens=100,
               sampler=make_sampler(temp=0.0)))
```

Use your own run's adapter directory after reproducing on another machine.
Do not delete the verified adapter if it is needed for a live presentation.
Local cleanup is limited to the particular run directory you created; there
is no hosted endpoint or Azure resource to stop for this example.

Run the combined offline regression suite: **21 tests passed**, including
recomputation of retained results and audit/transcript integrity checks.
MLX is not required for these tests:

```bash
python -m unittest discover -s . -p 'test_*.py' -v
```

This is a toy domain-adaptation exercise. For production factual knowledge,
use a larger, reviewed dataset, held-out entities and independent evaluation;
consider retrieval for knowledge that needs citations or frequent updates.

- [Microsoft base-model card](https://huggingface.co/microsoft/Phi-3.5-mini-instruct)
- [Pinned MLX conversion](https://huggingface.co/mlx-community/Phi-3.5-mini-instruct-4bit/tree/7b2052fd882fe017300d4d42a4eb06a27b816af4)
- [MLX LM training](https://github.com/ml-explore/mlx-lm)
