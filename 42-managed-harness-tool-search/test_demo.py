import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch
import zipfile

import demo
from azure.ai.projects.models import PromptAgentDefinition, MicrosoftCopilotHarness, SkillReference, ToolSearchToolboxTool


class HarnessToolSearchTests(unittest.TestCase):
    def test_native_sdk_serialization(self):
        definition = PromptAgentDefinition(
            model="test", harness=MicrosoftCopilotHarness(), skills=[SkillReference(name="brief", version="1")],
        ).as_dict()
        demo.verify_definition(definition, "brief", "1")

    def test_default_runtime_is_rejected(self):
        with self.assertRaises(RuntimeError):
            demo.verify_definition({"skills": [{"name": "brief", "version": "1"}]}, "brief", "1")

    def test_wrong_skill_version_is_rejected(self):
        with self.assertRaises(RuntimeError):
            demo.verify_definition({"harness": {"type": "microsoft_copilot_preview"},
                                    "skills": [{"name": "brief", "version": "2"}]}, "brief", "1")

    def test_real_native_search_discriminator(self):
        self.assertEqual(ToolSearchToolboxTool().as_dict()["type"], "toolbox_search")

    def test_meta_tools_only(self):
        demo.verify_meta_tools({"tool_search", "call_tool"})

    def test_upfront_tool_dump_is_rejected(self):
        with self.assertRaises(RuntimeError):
            demo.verify_meta_tools({"tool_search", "call_tool", "learn.microsoft_docs_search"})

    def test_missing_meta_tool_is_rejected(self):
        with self.assertRaises(RuntimeError):
            demo.verify_meta_tools({"call_tool"})

    def test_native_skill_package(self):
        with zipfile.ZipFile(io.BytesIO(demo.skill_package("brief-123"))) as package:
            self.assertEqual(package.namelist(), ["SKILL.md"])
            text = package.read("SKILL.md").decode()
            self.assertIn("name: brief-123", text)
            self.assertIn("NATIVE-SKILL-42", text)

    def test_tool_discovery_nested_result(self):
        tool = demo.find_tool([{"tools": [{"name": "learn.microsoft_docs_search", "inputSchema": {}}]}])
        self.assertEqual(tool["name"], "learn.microsoft_docs_search")

    def test_unrelated_discovery_is_rejected(self):
        with self.assertRaises(RuntimeError):
            demo.find_tool([{"tools": [{"name": "delete_resource"}]}])

    def test_settings_root_contract(self):
        outputs = {k: {"value": v} for k, v in {
            "endpoint": "https://a.openai.azure.com", "projectEndpoint": "https://a.services.ai.azure.com/api/projects/p",
            "deployment": "model", "projectResourceId": "/subscriptions/s/resourceGroups/r/providers/p",
        }.items()}
        with patch.dict("os.environ", {}, clear=True), patch.object(Path, "exists", return_value=True), \
             patch.object(Path, "read_text", return_value=json.dumps({"subscription": "s", "resourceGroup": "r", "outputs": outputs})):
            self.assertEqual(demo.settings()["deployment"], "model")

    def test_no_native_skill_body_in_agent_instructions(self):
        source = Path(demo.__file__).read_text()
        self.assertNotIn("downloaded_instructions", source)
        self.assertNotIn('instructions=skill_package', source)


if __name__ == "__main__":
    unittest.main()
