"""Create a native managed voice prompt agent and verify a real spoken WebSocket turn."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import wave

from azure.ai.projects import AIProjectClient
from azure.ai.projects import models
from azure.core.exceptions import HttpResponseError
from azure.identity import AzureCliCredential

ROOT = Path(__file__).resolve().parent
STATE = ROOT / ".azure"
SAMPLE_RATE = 24000


def saved_outputs() -> dict:
    path = STATE / "deployment.json"
    return json.loads(path.read_text()).get("outputs", {}) if path.exists() else {}


def setting(environment: str, output: str, default: str = "") -> str:
    return os.getenv(environment, "").strip() or saved_outputs().get(output, {}).get("value", default)


def read_pcm(path: Path) -> bytes:
    with wave.open(str(path), "rb") as audio:
        if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate()) != (1, 2, SAMPLE_RATE):
            raise ValueError("Input must be mono PCM16 WAV at 24000 Hz.")
        pcm = audio.readframes(audio.getnframes())
    if not pcm or len(pcm) % 2 or not any(pcm):
        raise ValueError("Spoken fixture is empty, malformed or silent.")
    if len(pcm) > SAMPLE_RATE * 2 * 30:
        raise ValueError("Spoken smoke fixture must be at most 30 seconds.")
    return pcm


def contains_code(transcript: str) -> bool:
    return bool(re.search(r"\b(?:forty|40)\b", transcript.lower()))


def definition(model: str) -> models.VoiceAgentDefinition:
    return models.VoiceAgentDefinition(
        model_type=models.VoiceModelType.MANAGED,
        model=model,
        instructions="You are a friendly workshop voice assistant. Repeat the workshop code the caller tells you, then say a short greeting. Reply in one short sentence.",
        audio=models.VoiceAgentAudioConfig(
            input=models.VoiceAgentAudioInputConfig(
                format=models.RealtimeAudioFormatsAudioPcm(rate=SAMPLE_RATE),
                turn_detection=models.VoiceAgentServerVadTurnDetection(
                    threshold=0.5, prefix_padding_ms=300, silence_duration_ms=700,
                    create_response=True, interrupt_response=False,
                ),
                transcription=models.VoiceAgentInputTranscription(
                    model=models.VoiceAgentInputTranscriptionModel.AZURE_SPEECH,
                ),
            ),
            output=models.VoiceAgentAudioOutputConfig(
                voice="en-US-AvaNeural", voice_type=models.VoiceType.AZURE_STANDARD,
            ),
        ),
        output_modalities=[models.VoiceOutputModality.AUDIO],
        store=True,
    )


def preflight(location: str, model: str) -> dict:
    if model not in ("gpt-realtime", "gpt-realtime-2.1"):
        raise ValueError("Use a documented service-managed voice model: gpt-realtime or gpt-realtime-2.1.")
    identity = json.loads(subprocess.check_output(
        ["az", "account", "show", "--query", "{subscription:id,tenant:tenantId}", "-o", "json"], text=True,
    ))
    catalog = json.loads(subprocess.check_output(
        ["az", "cognitiveservices", "model", "list", "--location", location, "-o", "json"], text=True,
    ))
    available = [entry["model"] for entry in catalog if entry.get("model", {}).get("name") == model]
    if not available:
        raise RuntimeError(f"BLOCKED: {model} not in the actual {location} model catalog. Choose a documented supported region/model before provisioning.")
    read_pcm(ROOT / "assets/turn.wav")
    return {
        "status": "PASS", "stage": "local-and-model-preflight", "location": location, **identity,
        "model": model, "versions": sorted({entry["version"] for entry in available}),
        "previewAccess": "NOT_VERIFIED: no subscription-wide voice access probe is documented; actual native agent creation is authoritative.",
        "customerModelDeployment": False,
    }


def write_wav(path: Path, pcm: bytes) -> None:
    with wave.open(str(path), "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(SAMPLE_RATE)
        audio.writeframes(pcm)


def live() -> dict:
    endpoint = setting("FOUNDRY_PROJECT_ENDPOINT", "projectEndpoint")
    model = setting("FOUNDRY_VOICE_AGENT_MODEL", "deployment", "gpt-realtime")
    if not endpoint:
        raise RuntimeError("Deploy infra/main.bicep first; FOUNDRY_PROJECT_ENDPOINT or .azure/deployment.json is required.")
    agent_name = os.getenv("FOUNDRY_VOICE_AGENT_NAME", "workshop-managed-voice")
    pcm = read_pcm(ROOT / "assets/turn.wav")
    evidence = {"stage": "native-voice", "model": model, "agentName": agent_name, "inputSha256": hashlib.sha256(pcm).hexdigest()}
    with AzureCliCredential() as credential, AIProjectClient(
        endpoint=endpoint, credential=credential, allow_preview=True,
    ) as client:
        created = client.agents.create_version(agent_name=agent_name, definition=definition(model))
        evidence.update(agentVersion=created.version, agentKind=str(created.definition.kind))
        (STATE / "agent.json").write_text(json.dumps(evidence, indent=2))
        events, chunks = [], []
        input_transcript = output_transcript = conversation_id = session_id = ""
        with client.beta.voice_agents.realtime.connect(agent_name=agent_name, open_timeout=30) as conn:
            event = conn.recv(timeout=30)
            if not isinstance(event, models.RealtimeServerEventSessionCreated):
                raise RuntimeError(f"Expected session.created, received {event.type}.")
            events.append(str(event.type))
            conversation_id = event.conversation_id or ""
            session_id = event.session.get("id", "") if isinstance(event.session, dict) else (event.session.id or "")
            # This is real audio input, not an input_text message or client-side speech pipeline.
            frame_bytes = SAMPLE_RATE * 2 // 10
            for offset in range(0, len(pcm), frame_bytes):
                conn.input_audio_buffer.append(audio=pcm[offset:offset + frame_bytes])
                time.sleep(0.1)
            for _ in range(15):
                conn.input_audio_buffer.append(audio=b"\0" * frame_bytes)
                time.sleep(0.1)
            deadline = time.monotonic() + 90
            completed = False
            while time.monotonic() < deadline:
                event = conn.recv(timeout=max(0.1, deadline - time.monotonic()))
                events.append(str(event.type))
                if isinstance(event, models.RealtimeServerEventConversationItemInputAudioTranscriptionCompleted):
                    input_transcript = event.transcript
                elif isinstance(event, models.RealtimeServerEventResponseAudioDelta):
                    chunks.append(event.delta)
                elif isinstance(event, models.RealtimeServerEventResponseAudioTranscriptDone):
                    output_transcript = event.transcript
                elif isinstance(event, models.RealtimeServerEventError):
                    raise RuntimeError(f"Native voice service error: {event.error.message}")
                elif isinstance(event, models.RealtimeServerEventResponseDone):
                    if event.response.status != "completed":
                        raise RuntimeError(f"Native response status: {event.response.status}")
                    completed = True
                if completed and input_transcript and output_transcript:
                    break
            evidence.update(
                sessionId=session_id, conversationId=conversation_id, events=events,
                inputTranscript=input_transcript, outputTranscript=output_transcript,
                outputBytes=sum(map(len, chunks)), responseCompleted=completed,
            )
            (STATE / "session.json").write_text(json.dumps(evidence, indent=2))
        output_pcm = b"".join(chunks)
        if not all((completed, conversation_id, session_id, any(output_pcm), contains_code(input_transcript), contains_code(output_transcript))):
            raise RuntimeError("Missing real session/conversation/audio evidence or spoken code mismatch; see .azure/session.json.")
        write_wav(STATE / "reply.wav", output_pcm)
        deadline = time.monotonic() + 120
        while True:
            try:
                conversation = client.beta.voice_agents.conversations.get(agent_name, conversation_id)
                items = [
                    item.as_dict() if hasattr(item, "as_dict") else dict(item)
                    for item in client.beta.voice_agents.conversations.list_items(agent_name, conversation_id, order="asc")
                ]
                transcripts = {
                    role: " ".join(
                        part.get("transcript") or part.get("text") or ""
                        for item in items if item.get("role") == role
                        for part in item.get("content", [])
                    ) for role in ("user", "assistant")
                }
                if not all(contains_code(text) for text in transcripts.values()):
                    raise RuntimeError("Persisted user/assistant transcript has not finalized.")
                recording = client.beta.voice_agents.conversations.get_audio(agent_name, conversation_id)
                recorded_audio = b"".join(client.beta.voice_agents.conversations.download_audio(agent_name, conversation_id))
                if not recorded_audio.startswith(b"RIFF") or len(recorded_audio) <= 44:
                    raise RuntimeError("No persisted recording bytes returned.")
                break
            except (HttpResponseError, RuntimeError) as exc:
                if isinstance(exc, HttpResponseError) and exc.status_code not in (404, 409, 429, 503):
                    raise
                if time.monotonic() >= deadline:
                    raise
                time.sleep(5)
        (STATE / "conversation.json").write_text(json.dumps({"conversation": conversation.as_dict(), "items": items, "recording": recording.as_dict()}, indent=2))
        (STATE / "conversation.wav").write_bytes(recorded_audio)
        evidence.update(status="PASS", persistedTranscript=transcripts, persistedRecordingBytes=len(recorded_audio))
        return evidence


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight", action="store_true")
    parser.add_argument("--location", default="eastus2")
    args = parser.parse_args()
    STATE.mkdir(exist_ok=True)
    try:
        report = preflight(args.location, os.getenv("FOUNDRY_VOICE_AGENT_MODEL", "gpt-realtime")) if args.preflight else live()
    except Exception as exc:
        report = {"status": "BLOCKED", "stage": "preflight" if args.preflight else "native-voice", "errorType": type(exc).__name__, "error": str(exc)}
        (STATE / ("preflight.json" if args.preflight else "live-report.json")).write_text(json.dumps(report, indent=2))
        print(json.dumps(report, indent=2))
        return 1
    (STATE / ("preflight.json" if args.preflight else "live-report.json")).write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
