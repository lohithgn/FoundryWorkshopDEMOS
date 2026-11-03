from copy import deepcopy
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

import demo
from azure.ai.projects.models import FileDatasetVersion


def candidates():
    evidence = {"score": 0.5, "eval_id": "eval-a", "eval_run_id": "run-a"}
    return [
        {"candidate_id": "baseline", "status": "completed", "evaluation": evidence},
        {"candidate_id": "new", "status": "completed",
         "evaluation": {**evidence, "score": 0.9, "eval_run_id": "run-b"},
         "output": {"mutations": [{"type": "instructions", "value": "Service-generated prompt."}]}},
    ]


SUMMARY = {"baseline_id": "baseline", "best_id": "new"}


def validation(baseline_score=0.5, candidate_score=0.9):
    return {
        "eval_id": "heldout-eval", "eval_run_id": "heldout-run", "query": demo.HELDOUT[0]["query"],
        "baseline": {"candidate_id": "baseline", "response_id": "baseline-response", "score": baseline_score},
        "candidate": {"candidate_id": "new", "response_id": "candidate-response", "score": candidate_score},
    }


class NativeEvaluationOptimizerTests(unittest.TestCase):
    def test_generated_camelcase_tags_repaired_via_native_metadata_update(self):
        original = FileDatasetVersion({
            "id": "native-id", "name": "generated", "version": "1", "type": "uri_file",
            "data_uri": "azureai://source", "tags": {"securityControl": "Ignore", "lab": "44"},
        })
        updated = FileDatasetVersion({**original.as_dict(), "tags": {
            **original.tags, **demo.SECURITY_TAGS,
        }})
        project = MagicMock()
        project.datasets.get.side_effect = [original, updated]
        report = {}
        with patch.object(demo, "save"):
            result = demo.tag_generated_dataset(project, "generated", "1", report)
        project.datasets.create_or_update.assert_called_once_with(
            name="generated", version="1", dataset_version=original)
        self.assertEqual(result["tags"]["SecurityControl"], "Ignore")
        self.assertEqual(result["tags"]["SecurityExemption"], "StorageAccountKeyAccess")
        self.assertEqual(result["id"], "native-id")
        self.assertEqual(result["data_uri"], "azureai://source")
        self.assertNotIn("SecurityControl", report["generated_dataset_before_tag_update"]["tags"])

    def test_nonpersisted_security_tags_remain_blocked(self):
        original = FileDatasetVersion({
            "id": "native-id", "name": "generated", "version": "1", "type": "uri_file",
            "data_uri": "azureai://source", "tags": {"securityControl": "Ignore"},
        })
        project = MagicMock()
        project.datasets.get.side_effect = [original, deepcopy(original)]
        with patch.object(demo, "save"), self.assertRaisesRegex(RuntimeError, "after native metadata update"):
            demo.tag_generated_dataset(project, "generated", "1", {})

    def test_already_tagged_dataset_does_not_mutate_metadata(self):
        asset = FileDatasetVersion({
            "id": "native-id", "name": "generated", "version": "1", "type": "uri_file",
            "data_uri": "azureai://source", "tags": demo.SECURITY_TAGS,
        })
        project = MagicMock()
        project.datasets.get.return_value = asset
        with patch.object(demo, "save"):
            demo.tag_generated_dataset(project, "generated", "1", {})
        project.datasets.create_or_update.assert_not_called()

    def test_metadata_tag_update_cannot_change_dataset_content_reference(self):
        original = FileDatasetVersion({
            "id": "native-id", "name": "generated", "version": "1", "type": "uri_file",
            "data_uri": "azureai://source",
        })
        changed = FileDatasetVersion({
            **original.as_dict(), "data_uri": "azureai://different", "tags": demo.SECURITY_TAGS,
        })
        project = MagicMock()
        project.datasets.get.side_effect = [original, changed]
        with patch.object(demo, "save"), self.assertRaisesRegex(RuntimeError, "immutable data_uri"):
            demo.tag_generated_dataset(project, "generated", "1", {})

    def test_failed_candidate_reports_actual_native_screening_error(self):
        values = candidates()
        values[1] = {"candidate_id": "new", "status": "failed", "error": {
            "code": "candidate_screening_stalled", "message": "No accepted improvement.",
        }}
        with self.assertRaisesRegex(RuntimeError, "candidate_screening_stalled"):
            demo.native_heldout_evaluation(MagicMock(), {}, {}, values, SUMMARY)

    def test_unoptimized_baseline_retains_real_catalog_facts(self):
        self.assertIn("reply only 'Please consult the catalog.'", demo.BASELINE_INSTRUCTIONS)
        self.assertIn(demo.POLICY, demo.BASELINE_INSTRUCTIONS)

    def test_tiny_native_budget(self):
        config = demo.optimization_config("judge").as_dict()
        self.assertEqual(config["candidate_search_configuration"]["max_candidates"], 1)
        self.assertEqual(config["evaluation_configuration"]["max_concurrent_agent_runs"], 1)
        self.assertEqual(len(config["evaluation_configuration"]["training_set"]["source"]["test_cases"]), 2)
        self.assertEqual(len(config["evaluation_configuration"]["validation_set"]["source"]["test_cases"]), 1)

    def test_real_native_comparative_discriminator(self):
        self.assertEqual(demo.optimization_config("judge").as_dict()["type"], "agent_optimization")

    def test_versioned_dataset_is_pinned(self):
        config = demo.optimization_config("judge", {"name": "generated", "version": "7"}).as_dict()
        source = config["evaluation_configuration"]["training_set"]["source"]
        self.assertEqual(source, {"type": "dataset_reference", "name": "generated", "version": "7"})

    def test_holdout_is_not_training(self):
        queries = {r["query"] for r in demo.TRAINING}
        self.assertFalse(queries & {r["query"] for r in demo.HELDOUT})

    def test_diverse_native_evaluators(self):
        values = demo.criteria("judge")
        self.assertEqual({c["evaluator_name"] for c in values},
                         {"builtin.groundedness", "builtin.relevance", "builtin.tool_call_accuracy"})

    def test_groundedness_maps_native_optional_tool_definitions(self):
        criterion = next(c for c in demo.criteria("judge") if c["name"] == "groundedness")
        self.assertEqual(criterion["data_mapping"]["tool_definitions"], "{{item.tool_definitions}}")

    def test_read_only_synthetic_lookup(self):
        self.assertEqual(demo.lookup_price({"workshop": "Atlas"})["price"], 25)

    def test_unknown_lookup_rejected(self):
        with self.assertRaises(ValueError):
            demo.lookup_price({"workshop": "unknown"})

    def test_actual_tool_trace_uses_service_call_id_and_deployed_model(self):
        ai = MagicMock()
        ai.responses.create.side_effect = [
            SimpleNamespace(id="native-initial", output=[
                SimpleNamespace(type="function_call", name="lookup_price",
                                arguments='{"workshop":"Atlas"}', call_id="native-call"),
            ]),
            SimpleNamespace(id="native-final", status="completed", output_text="Atlas costs USD 25."),
        ]
        row = demo.actual_tool_trace(ai, "deployed-model", demo.TRAINING[0])
        self.assertEqual(row["tool_calls"][0]["tool_call_id"], "native-call")
        self.assertEqual(row["response_id"], "native-final")
        self.assertEqual(ai.responses.create.call_args.kwargs["previous_response_id"], "native-initial")
        self.assertEqual(ai.responses.create.call_args.kwargs["model"], "deployed-model")

    def test_holdout_gate_approves_native_improvement(self):
        gate = demo.heldout_gate(candidates(), SUMMARY, validation())
        self.assertEqual(gate["decision"], "APPROVE")
        self.assertFalse(gate["promoted"])

    def test_tied_candidate_is_not_promoted(self):
        values = candidates()
        values[1]["evaluation"]["score"] = 0.5
        self.assertEqual(demo.heldout_gate(values, SUMMARY, validation(candidate_score=0.5))["decision"], "REJECT")

    def test_training_screen_does_not_pass_heldout_gate(self):
        self.assertEqual(demo.heldout_gate(candidates(), SUMMARY, validation(candidate_score=0.4))["decision"], "REJECT")

    def test_gate_requires_separate_native_holdout(self):
        with self.assertRaises(RuntimeError):
            demo.heldout_gate(candidates(), SUMMARY, {})

    def test_missing_baseline_rejected(self):
        with self.assertRaises(RuntimeError):
            demo.heldout_gate(candidates()[1:], SUMMARY, validation())

    def test_missing_cloud_eval_link_rejected(self):
        values = candidates()
        del values[1]["evaluation"]["eval_run_id"]
        with self.assertRaises(RuntimeError):
            demo.heldout_gate(values, SUMMARY, validation())

    def test_missing_native_mutation_rejected(self):
        values = candidates()
        values[1]["output"]["mutations"] = []
        with self.assertRaises(RuntimeError):
            demo.heldout_gate(values, SUMMARY, validation())

    def test_nan_candidate_score_rejected(self):
        values = candidates()
        values[1]["evaluation"]["score"] = float("nan")
        with self.assertRaises(RuntimeError):
            demo.heldout_gate(values, SUMMARY, validation())

    def test_missing_evaluator_score_rejected(self):
        with self.assertRaises(RuntimeError):
            demo.verify_evaluator_results([{"results": [{"name": "groundedness", "score": 4}]}], 1)

    def test_all_real_evaluator_results(self):
        item = {"results": [{"name": name, "score": 4} for name in
                            ("groundedness", "relevance", "tool_call_accuracy")]}
        demo.verify_evaluator_results([item, deepcopy(item)], 2)

    def test_invalid_native_evaluator_score_cannot_claim_pass(self):
        for value in (None, "5", float("nan")):
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                demo.verify_evaluator_results([{"results": [
                    {"name": name, "score": value} for name in
                    ("groundedness", "relevance", "tool_call_accuracy")
                ]}], 1)

    def test_timeout_cancels_native_job_without_resubmission(self):
        cancel = MagicMock()
        with patch.object(demo, "save"), self.assertRaises(TimeoutError):
            demo.poll_job(MagicMock(), cancel, "one-job", {}, "optimizer", timeout=0)
        cancel.assert_called_once_with("one-job")


if __name__ == "__main__":
    unittest.main()
