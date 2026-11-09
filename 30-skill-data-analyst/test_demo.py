from contextlib import redirect_stderr, redirect_stdout
from copy import deepcopy
import csv
from io import BytesIO, StringIO
import json
from pathlib import Path
import shutil
import struct
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
from uuid import uuid4
from zipfile import ZipFile
import zlib

from azure.ai.projects.models import CodeInterpreterTool, PromptAgentDefinition
from azure.ai.projects import AIProjectClient
from azure.identity import AzureCliCredential
from openai.types.responses import Response

import demo


def png():
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    # A real small RGB PNG, not just a magic-number mock.
    rows = b"".join(b"\0" + bytes([i, 100, 200]) * 64 for i in range(64))
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", 64, 64, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(rows))
        + chunk(b"IEND", b"")
    )


def response_payload():
    return {
        "id": "resp-test", "object": "response", "created_at": 0, "model": "test",
        "status": "completed", "parallel_tool_calls": False,
        "tool_choice": "required", "tools": [],
        "output": [
            {"type": "code_interpreter_call", "id": "ci-test", "container_id": "cntr-test",
             "status": "completed", "code": "print('CSV_ANALYSIS_EXECUTED')",
             "outputs": [{"type": "logs", "logs": "CSV_ANALYSIS_EXECUTED\n"}]},
            {"type": "message", "id": "msg-test", "role": "assistant", "status": "completed",
             "content": [{"type": "output_text", "text": "55 attendance instances, 95000 cents.",
                          "annotations": [
                              {"type": "container_file_citation", "container_id": "cntr-test",
                               "file_id": file_id, "filename": name, "start_index": 0, "end_index": 1}
                              for name, file_id in [
                                  ("/mnt/data/report.json", "cfile-report"),
                                  ("attendance-cost.png", "cfile-chart"),
                              ]
                          ]}]},
        ],
    }


class AnalystTests(unittest.TestCase):
    def setUp(self):
        self.project, self.client = Mock(), Mock()
        self.stdout, self.stderr = StringIO(), StringIO()
        self.payload = response_payload()
        self.client.responses.create.side_effect = lambda **_: Response.model_validate(self.payload)
        self.project.agents.create_version.side_effect = lambda **kw: SimpleNamespace(
            name=kw["agent_name"], version="7",
        )
        self.client.files.create.return_value.id = "file-input"
        self.client.containers.create.return_value.id = "cntr-test"
        self.downloads = {
            "cfile-report": json.dumps(demo.EXPECTED).encode(),
            "cfile-chart": png(),
        }
        self.client.containers.files.content.retrieve.side_effect = lambda **kw: BytesIO(
            self.downloads[kw["file_id"]],
        )

        def upload(*, name, content):
            filename, stream = content.as_dict()["files"][0]
            self.assertEqual(filename, "csv-analyst.zip")
            self.package = stream.read()
            return SimpleNamespace(name=name, version="42")

        self.project.beta.skills.create_from_files.side_effect = upload
        self.project.beta.skills.download_version.side_effect = lambda *_: iter([
            self.package[:10], self.package[10:],
        ])

    def run_demo(self, **kwargs):
        with redirect_stdout(self.stdout), redirect_stderr(self.stderr):
            demo.demonstrate(self.project, self.client, "test-deployment", **kwargs)

    def assert_all_deleted(self):
        name = self.project.beta.skills.create_from_files.call_args.kwargs["name"]
        self.project.beta.skills.delete.assert_called_once_with(name=name)
        self.client.files.delete.assert_called_once_with("file-input")
        self.client.containers.delete.assert_called_once_with("cntr-test")
        self.project.agents.delete_version.assert_called_once_with(
            agent_name=self.project.agents.create_version.call_args.kwargs["agent_name"],
            agent_version="7",
        )
        self.client.responses.delete.assert_called_once_with("resp-test")

    def test_initial_skill_upload_retries_only_project_not_found(self):
        def not_found(code, message, status=404):
            response = Mock(status_code=status, reason="Not Found")
            response.text.return_value = json.dumps({"error": {"code": code, "message": message}})
            return demo.ResourceNotFoundError(response=response)

        upload = self.project.beta.skills.create_from_files
        upload.side_effect = [not_found("NotFound", "Project not found"), SimpleNamespace(version="1")]
        with patch.object(demo, "monotonic", side_effect=[0, 1]), \
                patch.object(demo, "sleep") as sleep, redirect_stdout(self.stdout):
            skill = demo.upload_skill(self.project, "unique-skill", b"archive", timeout=30)
        self.assertEqual(skill.version, "1")
        sleep.assert_called_once_with(15)
        for call in upload.call_args_list:
            self.assertEqual(call.kwargs["name"], "unique-skill")
            self.assertEqual(call.kwargs["content"].as_dict()["files"][0][1].read(), b"archive")

        for error, now in (
            (not_found("NotFound", "A different resource was not found"), 1),
            (not_found("OtherCode", "Project not found"), 1),
            (not_found("NotFound", "Project not found", status=403), 1),
            (not_found("NotFound", "Project not found"), 31),
        ):
            upload.side_effect = error
            with self.subTest(error=str(error), now=now), \
                    patch.object(demo, "monotonic", side_effect=[0, now]), \
                    patch.object(demo, "sleep") as sleep:
                with self.assertRaises(demo.ResourceNotFoundError):
                    demo.upload_skill(self.project, "unique-skill", b"archive", timeout=30)
                sleep.assert_not_called()

    def test_happy_path_pins_injects_executes_downloads_and_deletes(self):
        with patch.object(Path, "write_bytes") as write:
            self.run_demo()
        write.assert_not_called()
        name = self.project.beta.skills.create_from_files.call_args.kwargs["name"]
        self.project.beta.skills.download_version.assert_called_once_with(name, "42")
        definition = self.project.agents.create_version.call_args.kwargs["definition"]
        self.assertIsInstance(definition, PromptAgentDefinition)
        self.assertEqual(definition.model, "test-deployment")
        self.assertIn("CSV-ANALYST-V1", definition.instructions)
        self.assertIn(f"name: {name}", definition.instructions)
        self.assertEqual(definition.as_dict()["tools"], [
            {"type": "code_interpreter", "container": "cntr-test"},
        ])
        self.client.containers.create.assert_called_once_with(
            name=self.client.containers.create.call_args.kwargs["name"],
            file_ids=["file-input"], expires_after={"anchor": "last_active_at", "minutes": 20},
        )
        request = self.client.responses.create.call_args.kwargs
        self.assertEqual(request["tool_choice"], "required")
        self.assertEqual(request["include"], ["code_interpreter_call.outputs"])
        self.assertEqual(request["timeout"], 180.0)
        self.assertEqual(request["extra_body"]["agent_reference"]["version"], "7")
        self.assertIn("After saving both files", request["input"])
        self.assertIn("print CSV_ANALYSIS_EXECUTED in the Code Interpreter execution log", request["input"])
        self.assertNotIn("95000", request["input"])
        self.assertNotIn("95000", definition.instructions)
        self.assertEqual(self.client.files.create.call_args.kwargs["file"][1], demo.CSV_DATA)
        self.assertEqual(self.client.containers.files.content.retrieve.call_count, 2)
        for call in self.client.containers.files.content.retrieve.call_args_list:
            self.assertEqual(call.kwargs["container_id"], "cntr-test")
        self.assertIn("Executed code:", self.stdout.getvalue())
        self.assertIn("File citation:", self.stdout.getvalue())
        self.assertIn("SHA256=", self.stdout.getvalue())
        self.assert_all_deleted()

    def test_requests_code_interpreter_outputs_required_for_the_sandbox_marker(self):
        def create(**kwargs):
            payload = deepcopy(self.payload)
            if "code_interpreter_call.outputs" not in kwargs.get("include", []):
                payload["output"][0]["outputs"] = None
            return Response.model_validate(payload)

        self.client.responses.create.side_effect = create
        self.run_demo()
        self.assertEqual(self.client.responses.create.call_args.kwargs["include"],
                         ["code_interpreter_call.outputs"])
        self.assertIn("Sandbox logs:", self.stdout.getvalue())

    def test_fixture_oracle_is_independent_and_correct(self):
        rows = list(csv.DictReader(StringIO(demo.CSV_DATA.decode())))
        self.assertEqual(len(rows), demo.EXPECTED["row_count"])
        self.assertEqual(sum(int(row["attendees"]) for row in rows), 55)
        self.assertEqual(sum(int(row["cost_cents"]) for row in rows), 95000)
        for expected in demo.EXPECTED["by_workshop"]:
            selected = [r for r in rows if r["workshop"] == expected["workshop"]]
            self.assertEqual(sum(int(r["attendees"]) for r in selected), expected["attendees"])
            self.assertEqual(sum(int(r["cost_cents"]) for r in selected), expected["cost_cents"])

    def test_skill_roundtrip_and_rejects_tampered_or_extra_files(self):
        package, expected = demo.skill_package("csv-analyst-test")
        self.assertEqual(demo.downloaded_instructions(package, expected), expected.decode())
        self.assertIn(b"name: csv-analyst-test", expected)
        for content in (expected + b"x", expected.replace(b"CSV-ANALYST-V1", b"CSV-ANALYST-V2")):
            with self.assertRaisesRegex(RuntimeError, "differs"):
                demo.downloaded_instructions(package, content)
        stream = BytesIO()
        with ZipFile(stream, "w") as archive:
            archive.writestr("SKILL.md", expected)
            archive.writestr("run.py", "not permitted")
        with self.assertRaisesRegex(RuntimeError, "exactly one"):
            demo.downloaded_instructions(stream.getvalue(), expected)

    def test_missing_execution_failed_or_wrong_container_rejected(self):
        variants = []
        for status in ("incomplete", "failed"):
            payload = response_payload()
            payload["status"] = status
            variants.append(payload)
        payload = response_payload()
        payload["output"].pop(0)
        variants.append(payload)
        for key, value in [
            ("status", "failed"), ("container_id", "cntr-other"), ("code", ""),
            ("outputs", []), ("outputs", [{"type": "logs", "logs": "fabricated prose"}]),
        ]:
            payload = response_payload()
            payload["output"][0][key] = value
            variants.append(payload)
        for payload in variants:
            with self.subTest(payload=payload):
                self.setUp()
                self.payload = payload
                with self.assertRaises(RuntimeError):
                    self.run_demo()
                self.client.containers.files.content.retrieve.assert_not_called()
                self.assert_all_deleted()

    def test_missing_conflicting_wrong_container_and_alias_citations_rejected(self):
        for mutation in ("missing", "conflict", "container", "alias", "empty"):
            with self.subTest(mutation=mutation):
                self.setUp()
                citations = self.payload["output"][1]["content"][0]["annotations"]
                if mutation == "missing":
                    citations.pop()
                elif mutation == "conflict":
                    citations.append({**citations[0], "file_id": "different"})
                elif mutation == "container":
                    citations[0]["container_id"] = "cntr-foreign"
                elif mutation == "alias":
                    citations[0]["file_id"] = citations[1]["file_id"]
                else:
                    citations[0]["file_id"] = ""
                with self.assertRaises(RuntimeError):
                    self.run_demo()
                self.assert_all_deleted()

    def test_report_rejects_wrong_extra_missing_duplicate_and_noninteger_values(self):
        variants = [b"not json", b"\xff", b"[]", b"x" * 1_000_001]
        for key in demo.EXPECTED:
            value = deepcopy(demo.EXPECTED)
            del value[key]
            variants.append(json.dumps(value).encode())
        for key, value in [
            ("total_attendees", 54), ("total_cost_cents", 950.00), ("row_count", 6.0),
            ("skill_code", "wrong"), ("extra", 1), ("by_workshop", []),
        ]:
            variants.append(json.dumps({**demo.EXPECTED, key: value}).encode())
        variants.append(json.dumps(demo.EXPECTED).replace('"row_count": 6', '"row_count": 0, "row_count": 6').encode())
        for content in variants:
            with self.subTest(content=content[:100]), self.assertRaises(RuntimeError):
                demo.verify_report(content)

    def test_downloaded_wrong_report_and_invalid_chart_fail_and_cleanup(self):
        for file_id, content in [
            ("cfile-report", b"{}"), ("cfile-chart", b"not a chart"),
            ("cfile-chart", png()[:-12]), ("cfile-chart", png() + b"trailing"),
            ("cfile-chart", png()[:40] + b"corrupt" + png()[47:]),
        ]:
            with self.subTest(file_id=file_id):
                self.setUp()
                self.downloads[file_id] = content
                with self.assertRaises(RuntimeError):
                    self.run_demo()
                self.assert_all_deleted()
        demo.verify_png(png())

    def test_artifacts_persist_only_verified_bytes_under_ignored_azure(self):
        # Unique scratch storage stays within this lab's ignored .azure directory.
        root = demo.HERE / ".azure" / f"test-{uuid4().hex}"
        root.mkdir(parents=True)
        try:
            with patch.object(demo, "HERE", root):
                self.run_demo(save_artifacts=True)
            saved = list(root.glob(".azure/artifacts/*"))
            self.assertEqual(len(saved), 1)
            self.assertEqual((saved[0] / "report.json").read_bytes(), self.downloads["cfile-report"])
            self.assertEqual((saved[0] / "attendance-cost.png").read_bytes(), png())
            self.assert_all_deleted()
        finally:
            shutil.rmtree(root)

    def test_no_persistence_after_validation_failure(self):
        self.downloads["cfile-report"] = b"{}"
        with patch.object(Path, "mkdir") as mkdir:
            with self.assertRaises(RuntimeError):
                self.run_demo(save_artifacts=True)
        mkdir.assert_not_called()
        self.assert_all_deleted()

    def test_failures_at_each_creation_boundary_clean_only_created_objects(self):
        boundaries = [
            self.project.beta.skills.create_from_files,
            self.project.beta.skills.download_version,
            self.client.files.create, self.client.containers.create,
            self.project.agents.create_version, self.client.responses.create,
            self.client.containers.files.content.retrieve,
        ]
        for index in range(len(boundaries)):
            with self.subTest(index=index):
                self.setUp()
                operations = [
                    self.project.beta.skills.create_from_files,
                    self.project.beta.skills.download_version,
                    self.client.files.create, self.client.containers.create,
                    self.project.agents.create_version, self.client.responses.create,
                    self.client.containers.files.content.retrieve,
                ]
                operations[index].side_effect = RuntimeError("service unavailable")
                with self.assertRaisesRegex(RuntimeError, "service unavailable"):
                    self.run_demo()
                self.assertEqual(self.project.beta.skills.delete.call_count, int(index > 0))
                self.assertEqual(self.client.files.delete.call_count, int(index > 2))
                self.assertEqual(self.client.containers.delete.call_count, int(index > 3))
                self.assertEqual(self.project.agents.delete_version.call_count, int(index > 4))
                self.assertEqual(self.client.responses.delete.call_count, int(index > 5))

    def test_cleanup_failures_do_not_skip_other_deletions(self):
        self.client.responses.delete.side_effect = RuntimeError("delete forbidden")
        self.project.agents.delete_version.side_effect = RuntimeError("delete forbidden")
        with self.assertRaisesRegex(RuntimeError, "Cleanup incomplete"):
            self.run_demo()
        self.assert_all_deleted()
        self.assertIn("Cleanup failed", self.stderr.getvalue())

    def test_primary_failure_not_hidden_by_cleanup_error(self):
        self.client.responses.create.side_effect = RuntimeError("primary inference failure")
        self.client.containers.delete.side_effect = RuntimeError("cleanup failure")
        with self.assertRaisesRegex(RuntimeError, "primary inference failure"):
            self.run_demo()
        self.client.files.delete.assert_called_once()
        self.project.beta.skills.delete.assert_called_once()

    def test_unique_resource_names_across_runs(self):
        self.run_demo()
        first = self.project.beta.skills.create_from_files.call_args.kwargs["name"]
        self.run_demo()
        second = self.project.beta.skills.create_from_files.call_args.kwargs["name"]
        self.assertNotEqual(first, second)

    def test_cli_enforces_endpoint_and_deployment_without_cloud_access(self):
        with patch.dict("os.environ", {}, clear=True), redirect_stderr(StringIO()):
            for args in ([], ["--project-endpoint", "http://bad"], [
                "--project-endpoint", "https://example.com", "--deployment", " ",
            ]):
                with self.subTest(args=args), self.assertRaises(SystemExit):
                    demo.main(args)

    def test_cli_uses_entra_preview_timeouts_and_explicit_failure(self):
        credential, project, client = Mock(), Mock(), Mock()
        credential.__enter__ = Mock(return_value=credential)
        credential.__exit__ = Mock(return_value=False)
        project.__enter__ = Mock(return_value=project)
        project.__exit__ = Mock(return_value=False)
        client.__enter__ = Mock(return_value=client)
        client.__exit__ = Mock(return_value=False)
        project.get_openai_client.return_value = client
        with (
            patch.object(demo, "AzureCliCredential", return_value=credential) as cred,
            patch.object(demo, "AIProjectClient", return_value=project) as factory,
            patch.object(demo, "demonstrate", side_effect=RuntimeError("404 preview unsupported")),
        ):
            with self.assertRaisesRegex(RuntimeError, "no local-computation fallback.*404 preview unsupported"):
                demo.main(["--project-endpoint", "https://example.com", "--deployment", "model"])
        cred.assert_called_once_with(process_timeout=30)
        self.assertTrue(factory.call_args.kwargs["allow_preview"])
        self.assertEqual(factory.call_args.kwargs["retry_total"], 0)
        project.get_openai_client.assert_called_once_with(timeout=180.0, max_retries=0)

    def test_installed_sdk_accepts_payload_shapes_without_network(self):
        import inspect
        from openai.resources.containers import Containers
        from openai.resources.containers.files.content import Content
        from openai.resources.responses import Responses
        for method, names in [
            (Containers.create, {"name", "file_ids", "expires_after"}),
            (Containers.delete, {"container_id"}),
            (Content.retrieve, {"file_id", "container_id"}),
            (Responses.create, {"extra_body", "include", "tool_choice", "timeout"}),
            (Responses.delete, {"response_id"}),
        ]:
            self.assertTrue(names <= set(inspect.signature(method).parameters))
        self.assertEqual(CodeInterpreterTool(container="cntr-test").as_dict(), {
            "type": "code_interpreter", "container": "cntr-test",
        })
        parsed = Response.model_validate(response_payload())
        self.assertEqual(parsed.output[0].container_id, "cntr-test")
        self.assertEqual(parsed.output[1].content[0].annotations[0].file_id, "cfile-report")
        # This constructs clients only; no credential/token/network request occurs.
        with AzureCliCredential() as credential, AIProjectClient(
            "https://example.com/api/projects/test", credential, allow_preview=True,
        ) as project:
            self.assertIn("version", inspect.signature(project.beta.skills.download_version).parameters)
            with project.get_openai_client(timeout=180.0, max_retries=0) as client:
                self.assertEqual(client.max_retries, 0)


if __name__ == "__main__":
    unittest.main()
