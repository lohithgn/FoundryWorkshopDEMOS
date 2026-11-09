import json
from pathlib import Path
import shutil
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock, patch

import demo


class RedTeamTests(unittest.IsolatedAsyncioTestCase):
    @unittest.skipUnless(shutil.which("az"), "Azure CLI/Bicep is required for the infrastructure contract.")
    def test_project_deployment_waits_for_model(self):
        result = subprocess.run(
            ["az", "bicep", "build", "--file", "infra/main.bicep", "--stdout"],
            cwd=Path(__file__).parent, check=True, capture_output=True, text=True,
        )
        resources = json.loads(result.stdout)["resources"]
        project = next(item for item in resources
                       if item["type"] == "Microsoft.CognitiveServices/accounts/projects")
        self.assertTrue(any("Microsoft.CognitiveServices/accounts/deployments" in dependency
                            for dependency in project["dependsOn"]))

    def test_target_calls_real_model_with_safety_instructions(self):
        client = Mock()
        client.chat.completions.create.return_value = SimpleNamespace(choices=[
            SimpleNamespace(finish_reason="stop", message=SimpleNamespace(content="A safe answer."))
        ])
        self.assertEqual(demo.tutor(client, "tutor", "What is Foundry?"), "A safe answer.")
        messages = client.chat.completions.create.call_args.kwargs["messages"]
        self.assertEqual(messages[0]["role"], "system")
        self.assertEqual(messages[1]["content"], "What is Foundry?")

    def test_target_rejects_incomplete_output(self):
        client = Mock()
        client.chat.completions.create.return_value = SimpleNamespace(choices=[
            SimpleNamespace(finish_reason="length", message=SimpleNamespace(content="partial"))
        ])
        with self.assertRaises(RuntimeError):
            demo.tutor(client, "tutor", "What is Foundry?")

    async def test_managed_scan_writes_and_validates_report(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "scan"

            payload = {
                "scorecard": {"summary": []},
                "AOAI_Compatible_Summary": {"status": "completed", "result_counts": {
                    "total": 1, "passed": 1, "failed": 0, "errored": 0, "skipped": 0,
                }},
            }
            result = SimpleNamespace(to_json=lambda: json.dumps(payload))
            scanner = Mock(scan=AsyncMock(return_value=result))
            with (
                patch("demo.AzureCliCredential"),
                patch("demo.OpenAI"),
                patch("demo.get_bearer_token_provider"),
                patch("demo.RedTeam", return_value=scanner) as constructor,
            ):
                await demo.scan("https://project", "https://account", "tutor", output)
            self.assertEqual(constructor.call_args.kwargs["num_objectives"], 1)
            self.assertEqual(len(constructor.call_args.kwargs["risk_categories"]), 1)
            self.assertEqual(constructor.call_args.kwargs["output_dir"], str(output))
            self.assertTrue(callable(scanner.scan.call_args.kwargs["target"]))
            self.assertNotIn("attack_strategies", scanner.scan.call_args.kwargs)
            self.assertEqual(json.loads((output / "report.json").read_text()), payload)

    async def test_existing_report_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "report.json"
            output.write_text("{}")
            with self.assertRaises(FileExistsError):
                await demo.scan("https://project", "https://account", "tutor", output)

    async def test_missing_result_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            with (
                patch("demo.AzureCliCredential"), patch("demo.OpenAI"),
                patch("demo.get_bearer_token_provider"),
                patch("demo.RedTeam", return_value=Mock(scan=AsyncMock(return_value=None))),
            ):
                with self.assertRaisesRegex(RuntimeError, "no result"):
                    await demo.scan("https://project", "https://account", "tutor",
                                    Path(directory) / "scan")

    async def test_scorecard_does_not_hide_failed_evaluations(self):
        for counts in ({"total": 0}, {"total": 1, "errored": 1}, {
                "total": 1, "passed": 0, "failed": 0, "errored": 0, "skipped": 1}):
            with self.subTest(counts=counts), tempfile.TemporaryDirectory() as directory:
                payload = {"scorecard": {"summary": []}, "AOAI_Compatible_Summary": {
                    "status": "completed", "result_counts": counts,
                }}
                result = SimpleNamespace(to_json=lambda: json.dumps(payload))
                with (
                    patch("demo.AzureCliCredential"), patch("demo.OpenAI"),
                    patch("demo.get_bearer_token_provider"),
                    patch("demo.RedTeam", return_value=Mock(scan=AsyncMock(return_value=result))),
                ):
                    with self.assertRaisesRegex(RuntimeError, "errored results"):
                        await demo.scan("https://project", "https://account", "tutor",
                                        Path(directory) / "scan")


if __name__ == "__main__":
    unittest.main()
