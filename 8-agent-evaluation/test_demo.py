import unittest
from types import SimpleNamespace
from unittest.mock import MagicMock, Mock, patch

import demo


class EvaluationTests(unittest.TestCase):
    def test_foundry_question_includes_product_context(self):
        self.assertIn("Azure platform", demo.QUESTIONS[0])
        self.assertIn("AI applications and agents", demo.QUESTIONS[0])

    def test_training_question_specifies_supervised_learning(self):
        self.assertIn("supervised model training using labeled data", demo.QUESTIONS[1])

    def responses(self):
        return [
            {"query": f"Question {index}", "response": f"Actual answer {index}",
             "response_id": f"response-{index}"}
            for index in range(2)
        ]

    def client(self, status="completed", errored=0):
        client = Mock()
        counts = SimpleNamespace(total=2, passed=2, failed=0, errored=errored)
        run = Mock(id="run-1", status=status, result_counts=counts)
        client.evals.create.return_value.id = "eval-1"
        client.evals.runs.create.return_value = run
        items = [Mock(status="completed") for _ in range(2)]
        for item, source in zip(items, self.responses()):
            item.model_dump.return_value = {
                "datasource_item": source,
                "results": [{"name": "coherence", "status": "completed", "passed": True}],
            }
        client.evals.runs.output_items.list.return_value = items
        return client

    def test_submits_actual_answers_to_cloud_evaluator(self):
        client = self.client()
        demo.evaluate(client, self.responses(), "judge")
        criterion = client.evals.create.call_args.kwargs["testing_criteria"][0]
        self.assertEqual(criterion["evaluator_name"], "builtin.coherence")
        self.assertEqual(criterion["initialization_parameters"], {"model": "judge"})
        source = client.evals.runs.create.call_args.kwargs["data_source"]
        self.assertEqual(criterion["data_mapping"], {
            "query": "{{item.query}}", "response": "{{item.response}}",
        })
        config = client.evals.create.call_args.kwargs["data_source_config"]
        self.assertEqual(config["type"], "custom")
        self.assertEqual(config["item_schema"]["required"], ["query", "response", "response_id"])
        self.assertEqual(source, {
            "type": "jsonl",
            "source": {"type": "file_content", "content": [
                {"item": item} for item in self.responses()
            ]},
        })

    def test_failed_or_incomplete_results_are_errors(self):
        for client in (self.client(status="failed"), self.client(errored=1)):
            with self.subTest(client=client), self.assertRaises(RuntimeError):
                demo.evaluate(client, self.responses(), "judge")
        client = self.client()
        client.evals.runs.output_items.list.return_value = []
        with self.assertRaisesRegex(RuntimeError, "missing"):
            demo.evaluate(client, self.responses(), "judge")
        client = self.client()
        client.evals.runs.output_items.list.return_value[0].status = "failed"
        with self.assertRaisesRegex(RuntimeError, "errored"):
            demo.evaluate(client, self.responses(), "judge")
        client = self.client()
        client.evals.runs.create.return_value.result_counts.total = 1
        with self.assertRaisesRegex(RuntimeError, "Incomplete"):
            demo.evaluate(client, self.responses(), "judge")

    def test_polling_is_bounded(self):
        client = self.client(status="in_progress")
        with patch("demo.monotonic", side_effect=[0, 601]):
            with self.assertRaises(TimeoutError):
                demo.evaluate(client, self.responses(), "judge")

    def test_missing_answers_and_duplicate_ids_are_rejected(self):
        for responses in ([], [{"response_id": "a"}],
                          [{**self.responses()[0], "response": ""}],
                          [self.responses()[0], self.responses()[0]]):
            with self.subTest(responses=responses), self.assertRaises(ValueError):
                demo.evaluate(Mock(), responses, "judge")

    def test_completed_items_require_matching_real_coherence_results(self):
        for mutation in (
            lambda row: row.update(results=[]),
            lambda row: row["results"][0].update(status="failed"),
            lambda row: row["results"][0].update(passed=None),
            lambda row: row["results"][0].update(passed=False),
            lambda row: row.update(datasource_item={"response_id": "unrelated"}),
            lambda row: row["datasource_item"].update(response="substituted answer"),
        ):
            client = self.client()
            mutation(client.evals.runs.output_items.list.return_value[0].model_dump.return_value)
            with self.subTest(mutation=mutation), self.assertRaises(RuntimeError):
                demo.evaluate(client, self.responses(), "judge")

    def test_completed_low_score_is_valid_not_infrastructure_failure(self):
        client = self.client()
        client.evals.runs.create.return_value.result_counts.passed = 1
        client.evals.runs.create.return_value.result_counts.failed = 1
        client.evals.runs.output_items.list.return_value[0].model_dump.return_value["results"][0]["passed"] = False
        demo.evaluate(client, self.responses(), "judge")

    def test_duplicate_output_item_is_rejected(self):
        client = self.client()
        items = client.evals.runs.output_items.list.return_value
        items[1].model_dump.return_value = items[0].model_dump.return_value
        with self.assertRaisesRegex(RuntimeError, "mismatched"):
            demo.evaluate(client, self.responses(), "judge")

    def test_main_forwards_exact_answers_and_cleans_agent_on_evaluation_failure(self):
        for failure in (False, True):
            project = MagicMock()
            project.agents.create_version.return_value = SimpleNamespace(name="agent", version="1")
            agent_client = project.get_openai_client.return_value.__enter__.return_value
            responses = [
                SimpleNamespace(status="completed", output_text=f"Actual answer {index}",
                                id=f"response-{index}")
                for index in range(2)
            ]
            agent_client.responses.create.side_effect = responses
            with (
                patch("sys.argv", ["demo.py", "--project-endpoint", "https://project"]),
                patch("demo.AzureCliCredential"),
                patch("demo.AIProjectClient") as factory,
                patch("demo.evaluate") as evaluate,
            ):
                factory.return_value.__enter__.return_value = project
                if failure:
                    evaluate.side_effect = RuntimeError("evaluation failed")
                    with self.assertRaisesRegex(RuntimeError, "evaluation failed"):
                        demo.main()
                else:
                    demo.main()
            self.assertEqual(evaluate.call_args.args[1], [
                {"query": question, "response": response.output_text, "response_id": response.id}
                for question, response in zip(demo.QUESTIONS, responses)
            ])
            project.agents.delete_version.assert_called_once_with(
                agent_name="agent", agent_version="1",
            )


if __name__ == "__main__":
    unittest.main()
