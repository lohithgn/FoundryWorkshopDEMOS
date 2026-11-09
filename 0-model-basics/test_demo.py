import json
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path
import shutil
import subprocess
import tempfile
from types import SimpleNamespace as NS
import unittest
from unittest.mock import MagicMock, Mock, patch

import httpx
from openai import OpenAI

import demo


def completion(content="Workshop Sparks", finish="stop"):
    return NS(
        model="fixture-model", usage=NS(model_dump=lambda: {"completion_tokens": 5}),
        choices=[NS(finish_reason=finish, message=NS(content=content, refusal=None))],
    )


def output_rows():
    return [
        {"custom_id": f"task-{index}", "error": None,
         "response": {"status_code": 200, "body": {
             "model": "fixture-model", "usage": {"completion_tokens": 5},
             "choices": [{"finish_reason": "stop", "message": {"content": f"Fixture {index}"}}],
         }}}
        for index in range(3)
    ]


def jsonl(rows):
    return "".join(json.dumps(row) + "\n" for row in rows)


class BasicsTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("az"), "Azure CLI/Bicep is required for the infrastructure contract.")
    def test_reasoning_deployment_uses_current_gpt5_mini_version(self):
        result = subprocess.run(
            ["az", "bicep", "build", "--file", "infra/main.bicep", "--stdout"],
            cwd=Path(__file__).parent, check=True, capture_output=True, text=True,
        )
        deployments = [
            item for item in json.loads(result.stdout)["resources"]
            if item["type"] == "Microsoft.CognitiveServices/accounts/deployments"
        ]
        reasoning = next(item for item in deployments if "'basics-reasoning'" in item["name"])
        self.assertEqual(reasoning["properties"]["model"], {
            "format": "OpenAI", "name": "gpt-5-mini", "version": "2025-08-07",
        })
        self.assertEqual(reasoning["sku"]["name"], "GlobalStandard")

    def test_temperature_exact_distribution_and_greedy_limit(self):
        probabilities = [0.5, 0.3, 0.15, 0.05]
        self.assertEqual(demo.temperature_distribution(probabilities, 0), [1, 0, 0, 0])
        for actual, expected in zip(demo.temperature_distribution(probabilities, 1), probabilities):
            self.assertAlmostEqual(actual, expected)
        self.assertGreater(demo.temperature_distribution(probabilities, 0.2)[0], 0.9)
        self.assertLess(demo.temperature_distribution(probabilities, 2)[0], 0.5)
        self.assertAlmostEqual(sum(demo.temperature_distribution(probabilities, 2)), 1)

    def test_nucleus_cumulative_boundary_not_percentage_of_candidates(self):
        self.assertEqual(demo.nucleus([0.05, 0.5, 0.3, 0.15], 0.5), [1])
        self.assertEqual(demo.nucleus([0.05, 0.5, 0.3, 0.15], 0.8), [1, 2])
        self.assertEqual(demo.nucleus([0.05, 0.5, 0.3, 0.15], 1), [1, 2, 3, 0])

    def test_invalid_toy_parameters_raise_explicitly(self):
        for probabilities in ([], [0, 1], [-0.5, 1.5], [float("nan")], [0.3, 0.4]):
            with self.subTest(probabilities=probabilities), self.assertRaises(ValueError):
                demo.temperature_distribution(probabilities, 1)
        for temperature in (-1, float("nan"), float("inf")):
            with self.assertRaises(ValueError):
                demo.temperature_distribution([0.5, 0.5], temperature)
        for top_p in (0, -1, 1.1, float("nan")):
            with self.assertRaises(ValueError):
                demo.nucleus([0.5, 0.5], top_p)

    def test_concepts_needs_no_azure_configuration(self):
        with patch.dict("os.environ", {}, clear=True), redirect_stdout(StringIO()) as output:
            self.assertEqual(demo.main(["--section", "concepts"]), 0)
        self.assertIn("LOCAL TOY EXAMPLE", output.getvalue())
        self.assertIn("batch is asynchronous", output.getvalue())

    def test_configuration_fails_before_cloud_imports(self):
        with patch.dict("os.environ", {}, clear=True), redirect_stdout(StringIO()):
            for arguments in (
                ["--endpoint", ""], ["--wait-seconds", "-1"], ["--samples", "0"],
                ["--endpoint", "https://example.org", "--deployment", ""],
                ["--endpoint", "https://example.org", "--reasoning-deployment", ""],
                ["--endpoint", "https://example.org", "--batch-deployment", ""],
                ["--batch-id", ""], ["--request-interval-seconds", "-1"],
                ["--request-interval-seconds", "nan"], ["--request-interval-seconds", "inf"],
            ):
                with self.subTest(arguments=arguments), self.assertRaises(SystemExit):
                    demo.main(arguments)

    def test_sampling_varies_one_knob_and_repeats_identical_prompt(self):
        client = Mock()
        client.chat.completions.create.return_value = completion()
        with redirect_stdout(StringIO()):
            demo.sampling(client, "chat-alias", 2)
        calls = client.chat.completions.create.call_args_list
        self.assertEqual(len(calls), 12)
        settings = [(call.kwargs["temperature"], call.kwargs["top_p"]) for call in calls]
        self.assertEqual(settings, [(0, 1)] * 2 + [(0.7, 1)] * 2 + [(1.4, 1)] * 2
                         + [(1, 0.2)] * 2 + [(1, 0.8)] * 2 + [(1, 1)] * 2)
        for call in calls:
            self.assertEqual(call.kwargs["model"], "chat-alias")
            self.assertEqual(call.kwargs["messages"][0]["content"], demo.SAMPLING_PROMPT)

    def test_pacing_bounds_request_starts_without_delaying_slow_calls(self):
        pacer = demo.RequestPacer(7)
        with patch("demo.time.monotonic", side_effect=[100, 100, 102, 107, 116, 116]), \
                patch("demo.time.sleep") as sleep:
            pacer.wait()
            pacer.wait()
            pacer.wait()
        sleep.assert_called_once_with(5)
        for value in (-1, float("nan"), float("inf")):
            with self.assertRaises(ValueError):
                demo.RequestPacer(value)

    def test_shared_pacer_covers_sampling_and_comparison(self):
        client = Mock()
        client.chat.completions.create.return_value = completion('{"minutes":11}')
        pacer = Mock()
        with redirect_stdout(StringIO()):
            demo.sampling(client, "chat", 2, pacer)
            demo.compare(client, "chat", "reasoning", "medium", pacer)
        self.assertEqual(pacer.wait.call_count, 14)

    def test_chat_sdk_retries_a_transient_rate_limit_without_changing_request(self):
        requests = []

        def respond(request):
            requests.append(json.loads(request.content))
            if len(requests) == 1:
                return httpx.Response(429, headers={"retry-after-ms": "1"}, json={
                    "error": {"code": "rate_limit_exceeded", "message": "Synthetic rate limit"},
                })
            return httpx.Response(200, json={
                "id": "fixture", "object": "chat.completion", "created": 0, "model": "fixture",
                "choices": [{"index": 0, "finish_reason": "stop",
                             "message": {"role": "assistant", "content": "Workshop Sparks"}}],
                "usage": {"prompt_tokens": 2, "completion_tokens": 2, "total_tokens": 4},
            })

        http_client = httpx.Client(transport=httpx.MockTransport(respond))
        with OpenAI(api_key="synthetic", base_url="https://example.test/v1",
                    max_retries=0, http_client=http_client) as client, \
                patch("openai._base_client.time.sleep"), redirect_stdout(StringIO()):
            demo.sampling(client.with_options(max_retries=demo.CHAT_MAX_RETRIES), "chat-alias", 1)
        self.assertEqual(len(requests), 7)
        self.assertEqual(requests[0], requests[1])

    def test_reasoning_omits_unsupported_sampling_and_token_parameters(self):
        client = Mock()
        client.chat.completions.create.return_value = completion('{"minutes":11}')
        with redirect_stdout(StringIO()) as output:
            demo.compare(client, "chat-alias", "reasoning-alias", "high")
        chat, reasoning = [call.kwargs for call in client.chat.completions.create.call_args_list]
        self.assertEqual(chat["messages"], reasoning["messages"])
        self.assertIn("B and C can run at the same time", chat["messages"][0]["content"])
        self.assertNotIn("11", chat["messages"][0]["content"])
        self.assertEqual(reasoning["model"], "reasoning-alias")
        self.assertEqual(reasoning["reasoning_effort"], "high")
        self.assertEqual(reasoning["max_completion_tokens"], 4096)
        self.assertEqual(chat["response_format"], reasoning["response_format"])
        self.assertEqual(chat["response_format"]["type"], "json_schema")
        self.assertTrue(chat["response_format"]["json_schema"]["strict"])
        self.assertEqual(chat["response_format"]["json_schema"]["schema"], {
            "type": "object", "properties": {"minutes": {"type": "integer"}},
            "required": ["minutes"], "additionalProperties": False,
        })
        for parameter in ("temperature", "top_p", "max_tokens"):
            self.assertNotIn(parameter, reasoning)
        self.assertIn("Matches reference answer 11: True", output.getvalue())

    def test_comparison_rejects_malformed_or_noninteger_structured_answers(self):
        client = Mock()
        for answer in ("{", "11", "{}", '{"minutes":"11"}', '{"minutes":true}',
                       '{"minutes":-1}', '{"minutes":11,"extra":1}'):
            client.chat.completions.create.return_value = completion(answer)
            with self.subTest(answer=answer), redirect_stdout(StringIO()), self.assertRaises(RuntimeError):
                demo.compare(client, "chat", "reasoning", "medium")

    def test_comparison_schema_does_not_make_an_incorrect_answer_pass(self):
        client = Mock()
        client.chat.completions.create.side_effect = [
            completion('{"minutes":10}'), completion('{"minutes":11}'),
        ]
        with redirect_stdout(StringIO()) as output:
            demo.compare(client, "chat", "reasoning", "medium")
        self.assertEqual(output.getvalue().count("Matches reference answer 11: True"), 1)
        self.assertEqual(output.getvalue().count("Matches reference answer 11: False"), 1)

    def test_incomplete_answers_fail_and_absent_usage_is_not_fabricated(self):
        for content, finish in (("", "stop"), (None, "stop"), ("partial", "length"),
                                ("refused", "content_filter")):
            with self.assertRaises(RuntimeError):
                demo.show_completion(completion(content, finish), 1)
        with self.assertRaises(RuntimeError):
            demo.show_completion(NS(choices=[]), 1)
        response = completion()
        response.usage = None
        with redirect_stdout(StringIO()) as output:
            demo.show_completion(response, 1)
        self.assertIn("Not returned by service", output.getvalue())

    def test_batch_jsonl_uses_batch_alias_unique_ids_and_correct_route(self):
        rows = demo.batch_rows("batch-alias")
        for row in rows[:2]:
            self.assertIn("language-model", row["body"]["messages"][0]["content"])
        self.assertEqual(len(rows), 3)
        self.assertEqual(len({row["custom_id"] for row in rows}), 3)
        for row, prompt in zip(rows, demo.BATCH_PROMPTS):
            self.assertEqual(row["url"], "/v1/chat/completions")
            self.assertEqual(row["method"], "POST")
            self.assertEqual(row["body"]["model"], "batch-alias")
            self.assertEqual(row["body"]["messages"][0]["content"], prompt)

    def test_batch_results_correlate_by_id_not_output_order(self):
        rows = output_rows()
        self.assertEqual(demo.validate_batch_results(jsonl(list(reversed(rows)))), rows)

    def test_batch_results_reject_missing_duplicate_errors_and_incomplete_answers(self):
        cases = [[], output_rows()[:2], [output_rows()[0]] * 3]
        for replacement in (
            {"custom_id": "foreign"}, {"error": {"code": "BadRequest"}},
            {"response": {"status_code": 429}},
            {"response": {"status_code": 200, "body": {"choices": []}}},
            {"response": {"status_code": 200, "body": {
                "choices": [{"finish_reason": "length", "message": {"content": "partial"}}]}}},
            {"response": {"status_code": 200, "body": {
                "choices": [{"finish_reason": "stop", "message": {"content": ""}}]}}},
        ):
            rows = output_rows()
            rows[0].update(replacement)
            cases.append(rows)
        for rows in cases:
            with self.subTest(rows=rows), self.assertRaises(RuntimeError):
                demo.validate_batch_results(jsonl(rows))

    def test_pending_batch_persists_id_and_resumes_without_resubmission(self):
        client = Mock()
        client.files.create.return_value = NS(id="input-123")
        client.batches.create.return_value = NS(id="batch-123")
        client.batches.retrieve.return_value = NS(status="in_progress", request_counts=None)
        with tempfile.TemporaryDirectory() as directory, redirect_stdout(StringIO()):
            state_dir = Path(directory)
            for _ in range(2):
                with self.assertRaisesRegex(RuntimeError, "NOT a completed live test"):
                    demo.run_batch(client, "https://example.org", "batch-alias", state_dir, None, 0)
            state = json.loads((state_dir / "batch-state.json").read_text())
            self.assertEqual(state["batch_id"], "batch-123")
            self.assertEqual(state["input_file_id"], "input-123")
            client.files.create.assert_called_once()
            client.batches.create.assert_called_once_with(
                input_file_id="input-123", endpoint="/chat/completions", completion_window="24h",
            )
            uploaded = client.files.create.call_args.kwargs["file"][1].decode()
            self.assertEqual([json.loads(line) for line in uploaded.splitlines()],
                             demo.batch_rows("batch-alias"))
            with self.assertRaisesRegex(ValueError, "different endpoint"):
                demo.run_batch(client, "https://other.org", "batch-alias", state_dir, None, 0)

    def test_completed_explicit_batch_downloads_and_validates_without_submission(self):
        client = Mock()
        client.batches.retrieve.return_value = NS(
            status="completed", request_counts=NS(total=3), output_file_id="output-123",
            error_file_id=None,
        )
        client.files.content.return_value = NS(text=jsonl(output_rows()))
        with tempfile.TemporaryDirectory() as directory, redirect_stdout(StringIO()) as output:
            state_dir = Path(directory)
            demo.run_batch(client, "https://example.org", "batch-alias", state_dir, "batch-123", 0)
            self.assertTrue((state_dir / "batch-output.jsonl").is_file())
        client.batches.create.assert_not_called()
        client.files.create.assert_not_called()
        self.assertIn("all three native batch requests completed", output.getvalue())

    def test_failed_batch_and_error_files_are_not_success(self):
        for status in ("failed", "expired", "cancelled", "completed"):
            with tempfile.TemporaryDirectory() as directory, redirect_stdout(StringIO()):
                client = Mock()
                client.batches.retrieve.return_value = NS(
                    status=status, errors={"code": "FixtureError"}, request_counts=None,
                    output_file_id="output-123", error_file_id="error-123",
                )
                client.files.content.return_value = NS(text='{"error":"fixture"}\n')
                with self.assertRaises(RuntimeError):
                    demo.run_batch(client, "https://example.org", "batch", Path(directory), "batch-123", 0)
                if status == "completed":
                    self.assertTrue((Path(directory) / "batch-errors.jsonl").is_file())

    def test_polling_is_bounded_and_can_observe_completion(self):
        client = Mock()
        client.batches.retrieve.side_effect = [
            NS(status="validating", request_counts=None),
            NS(status="completed", request_counts=None, error_file_id=None, output_file_id="output"),
        ]
        client.files.content.return_value = NS(text=jsonl(output_rows()))
        with (
            tempfile.TemporaryDirectory() as directory, redirect_stdout(StringIO()),
            patch("demo.time.monotonic", side_effect=[0, 1]), patch("demo.time.sleep") as sleep,
        ):
            demo.run_batch(client, "https://example.org", "batch", Path(directory), "batch-123", 5)
        sleep.assert_called_once_with(4)

    def test_main_wires_environment_keyless_client_and_all_sections(self):
        environment = {
            "AZURE_OPENAI_ENDPOINT": "https://example.org/",
            "AZURE_OPENAI_DEPLOYMENT": "chat-alias",
            "AZURE_OPENAI_REASONING_DEPLOYMENT": "reasoning-alias",
            "AZURE_OPENAI_BATCH_DEPLOYMENT": "batch-alias",
        }
        with (
            patch.dict("os.environ", environment, clear=True), redirect_stdout(StringIO()),
            patch("azure.identity.DefaultAzureCredential") as credential,
            patch("azure.identity.get_bearer_token_provider", return_value="token") as provider,
            patch("openai.OpenAI") as factory,
            patch("demo.sampling") as sampling, patch("demo.compare") as compare,
            patch("demo.run_batch") as batch,
        ):
            factory.return_value = MagicMock()
            self.assertEqual(demo.main([
                "--samples", "2", "--wait-seconds", "0", "--request-interval-seconds", "7",
            ]), 0)
            client = factory.return_value.__enter__.return_value
            chat_client = client.with_options.return_value
            client.with_options.assert_called_once_with(max_retries=demo.CHAT_MAX_RETRIES)
            pacer = sampling.call_args.args[3]
            self.assertIsInstance(pacer, demo.RequestPacer)
            self.assertEqual(pacer.interval_seconds, 7)
            sampling.assert_called_once_with(chat_client, "chat-alias", 2, pacer)
            compare.assert_called_once_with(chat_client, "chat-alias", "reasoning-alias", "medium", pacer)
            self.assertEqual(batch.call_args.args[:3], (client, "https://example.org", "batch-alias"))
            factory.assert_called_once_with(
                base_url="https://example.org/openai/v1/", api_key="token", max_retries=0, timeout=120,
            )
            provider.assert_called_once_with(
                credential.return_value.__enter__.return_value,
                "https://cognitiveservices.azure.com/.default",
            )

    def test_live_service_errors_propagate(self):
        client = Mock()
        client.chat.completions.create.side_effect = RuntimeError("service unavailable")
        with redirect_stdout(StringIO()), self.assertRaisesRegex(RuntimeError, "service unavailable"):
            demo.sampling(client, "chat-alias", 1)


if __name__ == "__main__":
    unittest.main()
