from contextlib import redirect_stdout
from io import BytesIO, StringIO
import json
from types import SimpleNamespace
from unittest import TestCase
from unittest.mock import Mock
from zipfile import BadZipFile, ZipFile

from openai.types.responses import Response

import demo


def response(payload, status="completed"):
    return Response.model_validate({
        "id": "resp-test", "object": "response", "created_at": 0, "model": "test",
        "status": status, "parallel_tool_calls": False, "tool_choice": "none", "tools": [],
        "output": [{
            "id": "msg-test", "type": "message", "role": "assistant", "status": "completed",
            "content": [{"type": "output_text", "text": json.dumps(payload), "annotations": []}],
        }],
    })


class ReleaseTests(TestCase):
    def setUp(self):
        self.project = Mock()
        self.bodies = {}
        self.default = None
        self.events = []
        self.agent_definitions = []
        self.client = Mock()
        self.project.get_openai_client.return_value.__enter__ = Mock(return_value=self.client)
        self.project.get_openai_client.return_value.__exit__ = Mock(return_value=False)

        def create(*, name, default, inline_content):
            version = str(len(self.bodies) + 7)
            self.bodies[version] = inline_content.instructions
            if default:
                self.default = version
            self.events.append(("create", version, default))
            return SimpleNamespace(name=name, version=version)

        def update(*, name, default_version):
            self.default = default_version
            self.events.append(("default", default_version))

        def download(name, version):
            buffer = BytesIO()
            with ZipFile(buffer, "w") as archive:
                archive.writestr("SKILL.md", self.bodies[version])
            return iter([buffer.getvalue()])

        def create_agent(**kwargs):
            self.agent_definitions.append(kwargs["definition"])
            return SimpleNamespace(name=kwargs["agent_name"], version="1")

        def answer(**kwargs):
            body = self.agent_definitions[-1].instructions
            workshop = "advanced" if "advanced" in kwargs["input"] else "intro"
            price = demo.PRICES[workshop]
            if workshop == "intro" and "intro workshop: USD 40" in body:
                price = 40
            return response({
                "workshop": workshop, "unit_price": price, "currency": "USD",
                "skill_code": "PRICING-V2" if "PRICING-V2" in body else "PRICING-V1",
            })

        self.project.beta.skills.create.side_effect = create
        self.project.beta.skills.update.side_effect = update
        self.project.beta.skills.get.side_effect = lambda **_: SimpleNamespace(default_version=self.default)
        self.project.beta.skills.download_version.side_effect = download
        self.project.agents.create_version.side_effect = create_agent
        self.client.responses.create.side_effect = answer

    def run_demo(self, **kwargs):
        with redirect_stdout(StringIO()):
            demo.demonstrate(self.project, "test-deployment", **kwargs)

    def test_gate_promotion_pinned_isolation_and_verified_rollback(self):
        self.run_demo()
        self.assertEqual(self.events, [
            ("create", "7", True), ("default", "7"), ("create", "8", False),
            ("default", "8"), ("default", "7"),
        ])
        self.assertEqual(self.client.responses.create.call_count, 8)
        versions = [call.args[1]
                    for call in self.project.beta.skills.download_version.call_args_list]
        self.assertEqual(versions, ["7", "7", "8", "8", "8", "7", "7", "7"])
        self.assertEqual(self.project.agents.delete_version.call_count, 8)
        self.project.beta.skills.delete.assert_called_once()
        self.assertTrue(all(item.model == "test-deployment" and item.tools == []
                            for item in self.agent_definitions))
        for call in self.client.responses.create.call_args_list:
            self.assertNotIn("previous_response_id", call.kwargs)
            self.assertIn("Echo the workshop ID exactly", call.kwargs["input"])
            self.assertNotIn("25", call.kwargs["input"])
            self.assertNotIn("50", call.kwargs["input"])
        for version in ("v1", "v2"):
            self.assertIn('the exact requested ID: "intro" or "advanced"', demo.skill_body(version))

    def test_regression_is_not_promoted_and_rolls_back_before_cleanup(self):
        self.run_demo()
        self.setUp()
        with self.assertRaisesRegex(RuntimeError, "Release gate failed"):
            self.run_demo(regression=True)
        self.assertNotIn(("default", "8"), self.events)
        self.assertEqual(self.default, "7")
        self.assertEqual(self.project.agents.delete_version.call_count, 3)
        self.project.beta.skills.delete.assert_called_once()

    def test_inference_failure_cleans_agent_restores_default_and_deletes_skill(self):
        self.client.responses.create.side_effect = RuntimeError("inference unavailable")
        with self.assertRaisesRegex(RuntimeError, "inference unavailable"):
            self.run_demo()
        self.project.agents.delete_version.assert_called_once()
        self.project.beta.skills.delete.assert_called_once()
        self.assertEqual(self.events[-1], ("default", "7"))

    def test_failure_after_promotion_still_rolls_back(self):
        original = self.client.responses.create.side_effect
        count = 0

        def failing(**kwargs):
            nonlocal count
            count += 1
            if count == 5:
                raise RuntimeError("post-promotion failure")
            return original(**kwargs)

        self.client.responses.create.side_effect = failing
        with self.assertRaisesRegex(RuntimeError, "post-promotion failure"):
            self.run_demo()
        self.assertIn(("default", "8"), self.events)
        self.assertEqual(self.events[-1], ("default", "7"))
        self.project.beta.skills.delete.assert_called_once()

    def test_candidate_creation_failure_does_not_leave_default_changed(self):
        original = self.project.beta.skills.create.side_effect
        count = 0

        def failing(**kwargs):
            nonlocal count
            count += 1
            if count == 2:
                raise RuntimeError("candidate unavailable")
            return original(**kwargs)

        self.project.beta.skills.create.side_effect = failing
        with self.assertRaisesRegex(RuntimeError, "candidate unavailable"):
            self.run_demo()
        self.assertEqual(self.events[-1], ("default", "7"))
        self.project.beta.skills.delete.assert_called_once()

    def test_bad_archive_changed_body_and_incomplete_output_fail(self):
        self.project.beta.skills.download_version.side_effect = None
        self.project.beta.skills.download_version.return_value = iter([b"not a ZIP"])
        with self.assertRaises(BadZipFile):
            demo.download_body(self.project, "name", "unknown", "expected")
        buffer = BytesIO()
        with ZipFile(buffer, "w") as archive:
            archive.writestr("SKILL.md", "different")
        self.project.beta.skills.download_version.side_effect = lambda *_: [buffer.getvalue()]
        with self.assertRaisesRegex(RuntimeError, "differs"):
            demo.download_body(self.project, "name", "1", "expected")
        self.setUp()
        self.client.responses.create.side_effect = lambda **_: response({}, status="incomplete")
        with self.assertRaisesRegex(RuntimeError, "Pricing agent failed"):
            self.run_demo()
        self.project.agents.delete_version.assert_called_once()
        self.project.beta.skills.delete.assert_called_once()

    def test_wrong_version_marker_extra_fields_or_boolean_price_fail_gate(self):
        for payload in (
            {"workshop": "intro workshop", "unit_price": 25, "currency": "USD",
             "skill_code": "PRICING-V1"},
            {"workshop": "intro", "unit_price": 25, "currency": "USD", "skill_code": "WRONG"},
            {"workshop": "intro", "unit_price": 25, "currency": "USD",
             "skill_code": "PRICING-V1", "extra": True},
            {"workshop": "intro", "unit_price": True, "currency": "USD", "skill_code": "PRICING-V1"},
        ):
            self.setUp()
            self.client.responses.create.side_effect = lambda **_: response(payload)
            with self.subTest(payload=payload), self.assertRaisesRegex(RuntimeError, "Release gate failed"):
                self.run_demo()
            self.project.agents.delete_version.assert_called_once()

    def test_default_readback_mismatch_is_not_accepted(self):
        self.project.beta.skills.get.side_effect = lambda **_: SimpleNamespace(default_version="wrong")
        with self.assertRaisesRegex(RuntimeError, "readback mismatch"):
            self.run_demo()
        self.project.beta.skills.delete.assert_called_once()

    def test_missing_or_changed_archive_shape_is_rejected(self):
        for files in ([], ["other.md"], ["SKILL.md", "extra.md"]):
            buffer = BytesIO()
            with ZipFile(buffer, "w") as archive:
                for file in files:
                    archive.writestr(file, "expected")
            self.project.beta.skills.download_version.side_effect = lambda *_: [buffer.getvalue()]
            with self.subTest(files=files), self.assertRaisesRegex(RuntimeError, "one SKILL.md"):
                demo.download_body(self.project, "name", "1", "expected")
