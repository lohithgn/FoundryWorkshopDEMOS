import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch
import wave

import demo


class VoiceTests(unittest.TestCase):
    def test_real_pcm_fixture(self):
        pcm = demo.read_pcm(demo.ROOT / "assets/turn.wav")
        self.assertGreater(len(pcm), 24000)

    def test_fixture_is_bounded(self):
        self.assertLess(len(demo.read_pcm(demo.ROOT / "assets/turn.wav")), 24000 * 2 * 30)

    def test_synthetic_prompt(self):
        text = (demo.ROOT / "assets/turn.txt").read_text()
        self.assertIn("forty", text)
        self.assertNotIn("@", text)

    def test_code_words(self):
        self.assertTrue(demo.contains_code("The workshop code is forty. Hello!"))

    def test_code_digits(self):
        self.assertTrue(demo.contains_code("Workshop code: 40. Welcome!"))

    def test_wrong_code_rejected(self):
        self.assertFalse(demo.contains_code("Welcome to workshop fourteen."))
        self.assertFalse(demo.contains_code("Code 400."))

    def test_native_managed_definition(self):
        definition = demo.definition("gpt-realtime").as_dict()
        self.assertEqual(definition["kind"], "voice")
        self.assertEqual(definition["model_type"], "managed")
        self.assertEqual(definition["model"], "gpt-realtime")

    def test_conversation_storage(self):
        self.assertTrue(demo.definition("gpt-realtime").store)

    def test_server_orchestration(self):
        definition = demo.definition("gpt-realtime")
        self.assertTrue(definition.audio.input.turn_detection.create_response)
        self.assertEqual(definition.audio.input.transcription.model, "azure-speech")

    def test_audio_output(self):
        definition = demo.definition("gpt-realtime")
        self.assertEqual(definition.output_modalities, ["audio"])
        self.assertEqual(definition.audio.output.voice, "en-US-AvaNeural")

    def test_sdk_session_is_mapping(self):
        event = demo.models.RealtimeServerEventSessionCreated(
            event_id="offline-test", session={"id": "offline-session"}, conversation_id="offline-conversation",
        )
        self.assertIsInstance(event.session, dict)
        self.assertEqual(event.session.get("id"), "offline-session")

    def test_sdk_item_serialization(self):
        item = demo.models.RealtimeConversationItemMessageUser(
            type="message", content=[demo.models.RealtimeConversationItemMessageUserContent(type="input_audio", transcript="Workshop forty")],
        )
        serialized = json.loads(json.dumps(item.as_dict()))
        self.assertEqual(serialized["content"][0]["transcript"], "Workshop forty")

    def test_root_environment_contract(self):
        with patch.dict(demo.os.environ, {"FOUNDRY_PROJECT_ENDPOINT": "https://example.services.ai.azure.com/api/projects/voice"}):
            self.assertIn("/api/projects/", demo.setting("FOUNDRY_PROJECT_ENDPOINT", "projectEndpoint"))

    def test_saved_output_contract(self):
        with patch.dict(demo.os.environ, {}, clear=True), patch.object(demo, "saved_outputs", return_value={"deployment": {"value": "gpt-realtime"}}):
            self.assertEqual(demo.setting("FOUNDRY_VOICE_AGENT_MODEL", "deployment"), "gpt-realtime")

    def test_empty_audio_rejected(self):
        buffer = io.BytesIO()
        with wave.open(buffer, "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(24000)
        with patch.object(demo.wave, "open", return_value=wave.open(io.BytesIO(buffer.getvalue()), "rb")):
            with self.assertRaisesRegex(ValueError, "empty"):
                demo.read_pcm(Path("unused.wav"))

    def test_no_customer_model_or_keys(self):
        bicep = (demo.ROOT / "infra/main.bicep").read_text()
        self.assertNotIn("accounts/deployments", bicep)
        self.assertIn("disableLocalAuth: true", bicep)
        self.assertIn("output endpoint", bicep)
        self.assertIn("output projectEndpoint", bicep)


if __name__ == "__main__":
    unittest.main()
