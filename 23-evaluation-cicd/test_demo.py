import json
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

import demo


class EvaluationTests(unittest.TestCase):
    def test_installed_workflow_matches_the_lab_example(self):
        installed = demo.HERE.parent / ".github/workflows/foundry-evaluation.yml"
        self.assertEqual(installed.read_text(), (demo.HERE / "workflow.yml").read_text())

    def responses(self):
        return [{"query": f"Question {index}", "response": f"Actual answer {index}",
                 "response_id": f"r{index}"} for index in range(3)]

    def client(self, status="completed", errored=0):
        client = Mock()
        counts = SimpleNamespace(total=3, passed=3, failed=0, errored=errored)
        run = Mock(id="run-1", status=status, result_counts=counts, error=None)
        run.model_dump.return_value = {"id": "run-1", "status": status}
        client.evals.create.return_value.id = "eval-1"
        client.evals.runs.create.return_value = run
        items = [Mock(status="completed") for _ in range(3)]
        for item, source in zip(items, self.responses()):
            item.model_dump.return_value = {
                "status": "completed", "datasource_item": source,
                "results": [{"name": "coherence", "status": "completed", "passed": True}],
            }
        client.evals.runs.output_items.list.return_value = items
        return client

    def test_actual_foundry_evaluation_contract(self):
        client = self.client()
        report = {}
        self.assertEqual(demo.cloud_evaluate(client, self.responses(), "judge", report), 1)
        criterion = client.evals.create.call_args.kwargs["testing_criteria"][0]
        self.assertEqual(criterion["evaluator_name"], "builtin.coherence")
        self.assertEqual(criterion["initialization_parameters"], {"model": "judge"})
        self.assertEqual(criterion["data_mapping"], {
            "query": "{{item.query}}", "response": "{{item.response}}",
        })
        config = client.evals.create.call_args.kwargs["data_source_config"]
        self.assertEqual(config["type"], "custom")
        self.assertEqual(config["item_schema"]["required"], ["query", "response", "response_id"])
        data = client.evals.runs.create.call_args.kwargs["data_source"]
        self.assertEqual(data["type"], "jsonl")
        self.assertEqual(data["source"]["type"], "file_content")
        self.assertEqual(data["source"]["content"], [{"item": item} for item in self.responses()])
        self.assertEqual(report["evaluation_id"], "eval-1")
        self.assertEqual(len(report["cloud_items"]), 3)

    def test_infrastructure_errors_never_pass(self):
        for client in (self.client("failed"), self.client(errored=1)):
            with self.subTest(client=client), self.assertRaises(RuntimeError):
                demo.cloud_evaluate(client, self.responses(), "judge", {})
        client = self.client()
        client.evals.runs.output_items.list.return_value = []
        with self.assertRaisesRegex(RuntimeError, "Missing"):
            demo.cloud_evaluate(client, self.responses(), "judge", {})
        with patch("demo.monotonic", side_effect=[0, 601]), self.assertRaises(TimeoutError):
            demo.cloud_evaluate(self.client("queued"), self.responses(), "judge", {})

    def test_missing_real_response_evidence_is_rejected(self):
        for items in ([], [{"query": "q", "response": "", "response_id": "r"}],
                      [{"query": "q", "response": "a", "response_id": ""}]):
            with self.subTest(items=items), self.assertRaises(ValueError):
                demo.cloud_evaluate(self.client(), items, "judge", {})

    def test_completed_items_still_require_real_matching_grader_results(self):
        for mutation in (
            lambda row: row.update(results=[]),
            lambda row: row["results"][0].update(status="failed"),
            lambda row: row["results"][0].update(passed=None),
            lambda row: row["results"][0].update(passed=False),
            lambda row: row.update(datasource_item={"response_id": "unrelated"}),
            lambda row: row.update(status="failed"),
        ):
            client = self.client()
            mutation(client.evals.runs.output_items.list.return_value[0].model_dump.return_value)
            with self.subTest(mutation=mutation), self.assertRaises(RuntimeError):
                demo.cloud_evaluate(client, self.responses(), "judge", {})

    def test_polished_regression_blocked_despite_perfect_coherence(self):
        dataset = json.loads((demo.HERE / "dataset.json").read_text())
        for answers, expected_pass in (
            (["14", "3", "help@example.invalid"], True),
            (["30", "7", "concierge@example.invalid"], False),
        ):
            report = {"coherence_rate": 1, "items": [
                {**row, "correct": demo.exact_answer(json.dumps({"answer": answer}), row["expected"])}
                for row, answer in zip(dataset, answers)
            ]}
            if expected_pass:
                demo.release_gate(report, 1, 1)
            else:
                with self.assertRaisesRegex(RuntimeError, "Release blocked"):
                    demo.release_gate(report, 1, 1)
            self.assertEqual(report["passed"], expected_pass)
        self.assertFalse(demo.exact_answer("not JSON", "14"))
        self.assertFalse(demo.exact_answer('{"answer":14}', "14"))
        self.assertFalse(demo.exact_answer('```json\n{"answer":"14"}\n```', "14"))

    def test_prompts_explicitly_request_raw_json_without_markdown(self):
        for name in ("baseline", "regression"):
            prompt = (demo.HERE / "prompts" / f"{name}.txt").read_text()
            with self.subTest(prompt=name):
                self.assertIn("Return raw JSON", prompt)
                self.assertIn("do not use Markdown code fences", prompt)

    def test_gate_thresholds_and_empty_data(self):
        with self.assertRaises(RuntimeError):
            demo.release_gate({"items": [], "coherence_rate": 1}, 1, 1)
        with self.assertRaises(RuntimeError):
            demo.release_gate({"items": [{"correct": True}], "coherence_rate": 0.5}, 1, 0.9)

    def test_infrastructure_exception_writes_failed_report_and_nonzero_exit(self):
        with patch("sys.argv", ["demo.py", "--project-endpoint", "https://project"]), \
                patch("demo.DefaultAzureCredential"), patch("demo.AIProjectClient"), \
                patch("demo.execute", side_effect=RuntimeError("evaluation unavailable")), \
                patch("demo.Path.write_text") as write:
            self.assertEqual(demo.main(), 1)
        report = json.loads(write.call_args.args[0])
        self.assertFalse(report["passed"])
        self.assertEqual(report["error"]["message"], "evaluation unavailable")
        self.assertEqual(len(report["dataset_sha256"]), 64)
        self.assertEqual(len(report["prompt_sha256"]), 64)
        self.assertEqual(report["evaluator"]["name"], "builtin.coherence")


if __name__ == "__main__":
    unittest.main()
