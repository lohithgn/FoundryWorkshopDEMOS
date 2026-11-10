# Lab 40: Native managed voice prompt agent

Creates a saved **`kind: voice` prompt agent**, using `VoiceAgentDefinition`,
`model_type=managed`, and service-hosted `gpt-realtime`. Foundry manages listening,
turn detection, transcription, model orchestration, speech synthesis and
conversation storage. This is **not** a Speech SDK attached to a chat agent.
No customer realtime deployment is created; `deployment` is the managed model name.

The default smoke is bounded and noninteractive: it streams the committed
[synthetic spoken turn](assets/turn.wav) over the native voice-agent WebSocket,
waits for real response audio and both transcripts, verifies the spoken code
`forty`, then reads the persisted conversation and downloads its recording.
No microphone, speaker, audio device or local server is required.
Missing response, code mismatch, preview denial or missing persistence fails
with exit status 1; text-only or simulated execution never passes this smoke.

## Prerequisites and preview

- Python 3.10+, Azure CLI/Bicep and `az login` as a user with resource creation
  and role-assignment permissions. Entra `AzureCliCredential` only; local auth
  is disabled and no API keys, tokens or OAuth secrets are stored.
- **Subscription access to the prompt voice agent preview**, a supported
  project region, and a managed voice model available to that project.
  A model appearing in the regional catalog does **not** prove preview access.
  The official quickstart does not document a subscription-wide preview access
  check; actual native agent creation is authoritative.
- `--preflight` checks login, the real regional model catalog and audio fixture
  before provisioning. It explicitly leaves preview access unverified.
  If rejected, review the service error and request the documented preview
  access; don't substitute ordinary realtime chat or local speech orchestration.
- Native APIs use the official `azure-ai-projects[voice]==2.10.0` SDK.
  The lab installs its own `.venv`; no shared environment is changed.

## Run

From the repository root:

```bash
dotnet run --file deploy.cs -- --demo 40 --location eastus2
```

The root runner installs requirements, runs offline tests, deploys and calls
`demo.py` without microphone interaction. Standard mappings are
`endpoint -> AZURE_OPENAI_ENDPOINT`, `projectEndpoint -> FOUNDRY_PROJECT_ENDPOINT`,
and `deployment -> AZURE_OPENAI_DEPLOYMENT`. Optional `FOUNDRY_VOICE_AGENT_MODEL`
overrides the managed model; `FOUNDRY_VOICE_AGENT_NAME` overrides the saved name.
The script also reads root-shaped `.azure/deployment.json`, so replay needs no
manual output exports:

```bash
cd 40-voice-agent
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
.venv/bin/python -m unittest -v
.venv/bin/python demo.py --preflight --location eastus2
az bicep build --file infra/main.bicep --stdout
az bicep lint --file infra/main.bicep
mkdir -p .azure
az group create --name rg-foundry-voice-agent-demo --location eastus2 --tags SecurityControl=Ignore SecurityExemption=StorageAccountKeyAccess
az deployment group create --resource-group rg-foundry-voice-agent-demo --name voice-agent-demo --template-file infra/main.bicep --parameters location=eastus2 principalId="$(az ad signed-in-user show --query id -o tsv)" -o json > .azure/arm-deployment.json
.venv/bin/python - <<'PY'
import json, subprocess
from pathlib import Path
arm = json.loads(Path(".azure/arm-deployment.json").read_text())
subscription = subprocess.check_output(["az", "account", "show", "--query", "id", "-o", "tsv"], text=True).strip()
Path(".azure/deployment.json").write_text(json.dumps({"subscription": subscription, "resourceGroup": "rg-foundry-voice-agent-demo", "outputs": arm["properties"]["outputs"]}, indent=2))
PY
.venv/bin/python demo.py
```

The standalone commands save outputs; the root runner saves the required
`{subscription,resourceGroup,outputs}` envelope automatically.
Standalone replay can instead supply `FOUNDRY_PROJECT_ENDPOINT`.
Bicep exposes `location`, `principalId`, `accountName`, `projectName` and
`managedModel` (`gpt-realtime` or documented `gpt-realtime-2.1`).

## Evidence and cost

Verified in this subscription on 2026-11-03: **16 offline tests passed**,
Bicep build/lint and EastUS2 ARM deployment succeeded, and the native managed
spoken WebSocket turn, both transcripts, persisted conversation and stereo
recording all passed live. The authoritative replay result is `.azure/live-report.json`.

All runtime evidence stays in ignored `.azure/`: `deployment.json`,
`preflight.json`, `agent.json`, `session.json`, `conversation.json`,
`reply.wav`, `conversation.wav`, `live-report.json` and command logs.
Only service responses can produce live `PASS`. Offline fixtures are explicitly
unit-test data, not evidence of a cloud session.

Resources and the saved agent **remain deployed**. Managed voice inference,
transcription/synthesis and retained conversation audio can be billable;
preview availability is not a promise of free usage or an SLA. There is no
reserved model capacity in this template. Delete the isolated RG when finished:
`az group delete --name rg-foundry-voice-agent-demo --yes --no-wait`.

The committed synthetic WAV was generated locally with macOS `say`, Samantha,
145 words/minute, mono PCM16 24 kHz. `python3 generate_audio.py` regenerates it
on macOS; the fixed committed bytes run unchanged on Linux/Windows. No personal
data, cloud TTS service or microphone recording is included.

## Official implementation sources

- [Native voice prompt agent quickstart](https://learn.microsoft.com/en-us/azure/foundry/agents/quickstarts/prompt-voice-agent)
- [Managed model, VAD and transcription configuration](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/configure-voice-agent)
- [Official spoken-audio SDK sample](https://github.com/Azure/azure-sdk-for-python/blob/main/sdk/ai/azure-ai-projects/samples/agents/voice/sample_voice_agent_realtime_audio_conversation_async.py)
- [Native recording download](https://learn.microsoft.com/en-us/azure/foundry/agents/quickstarts/prompt-voice-agent#read-the-conversation-back)

The official SDK sample and current Learn pages were fetched before implementing
the APIs; installed SDK signatures, actual unit tests and live service responses
are the validation sources. Pylance was unavailable in this session.
