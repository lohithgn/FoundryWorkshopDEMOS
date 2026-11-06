import contextlib
import io
import json
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest.mock import patch

import polish_demo as demo


class PolishFineTuningTests(unittest.TestCase):
    def test_sourced_references_pass_and_split_prompts_are_disjoint(self):
        data = demo.load_data()
        self.assertEqual(len(data["topics"]), 16)
        self.assertEqual(sum(len(topic["train"]) for topic in data["topics"]), 64)
        self.assertEqual({topic["category"] for topic in data["topics"]}, {"food", "culture"})
        self.assertEqual(len(data["controls"]), 4)

    def test_normalization_handles_polish_diacritics(self):
        self.assertEqual(demo.normalize("Kraków, Łódź, Śląsk!"), "krakow lodz slask")
        self.assertTrue(demo.score("Poznań: white poppy seeds.",
                                   [["poznan"], ["white poppy"]], [])["passed"])

    def test_rubric_requires_every_concept_and_rejects_contradictions(self):
        required = [["pancake", "fritter"], ["sauerkraut"]]
        self.assertTrue(demo.score("Sauerkraut pancakes.", required, [])["passed"])
        self.assertFalse(demo.score("Pancakes.", required, [])["passed"])
        self.assertFalse(demo.score("Sauerkraut pancakes, a sweet dessert.",
                                    required, ["sweet dessert"])["passed"])
        self.assertFalse(demo.score("Not known.", required, [])["passed"])
        self.assertFalse(demo.score("560", [["56"]], [])["passed"])
        self.assertFalse(demo.score("hempstead", [["hemp seed"]], [])["passed"])

    def test_preparation_excludes_test_and_control_questions(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            demo.prepare(output)
            self.assertEqual(sorted(path.name for path in (output / "data").iterdir()),
                             ["train.jsonl", "valid.jsonl"])
            train = [json.loads(line) for line in (output / "data" / "train.jsonl").read_text().splitlines()]
            valid = [json.loads(line) for line in (output / "data" / "valid.jsonl").read_text().splitlines()]
            self.assertEqual((len(train), len(valid)), (80, 20))
            prompts = {row["messages"][1]["content"] for row in train + valid}
            for topic in demo.load_data()["topics"]:
                self.assertNotIn(topic["test"], prompts)
            for control in demo.load_data()["controls"]:
                self.assertNotIn(control["prompt"], prompts)
            for item in demo.evaluation_items():
                self.assertNotIn(item.get("test", item.get("prompt")), prompts)
            demo.check_manifest(output)
            with self.assertRaises(FileExistsError):
                demo.prepare(output)

    def test_changed_training_input_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            demo.prepare(output)
            with (output / "data" / "train.jsonl").open("a") as stream:
                stream.write("{}\n")
            with self.assertRaisesRegex(ValueError, "inputs changed"):
                demo.check_manifest(output)

    def test_overlapping_split_question_is_rejected(self):
        data = demo.read_json(demo.DATA)
        data["topics"][0]["test"] = data["topics"][0]["train"][0]
        with patch.object(demo, "read_json", return_value=data):
            with self.assertRaisesRegex(ValueError, "overlapping split"):
                demo.load_data()

    def reports(self, output, improve=True, regress_control=False):
        demo.prepare(output)
        manifest = demo.read_json(output / "manifest.json")
        for tuned, filename in ((False, "baseline.json"), (True, "tuned.json")):
            rows = []
            for item in demo.evaluation_items():
                control = "prompt" in item
                if control:
                    answer = item["required"][0][0] if not (tuned and regress_control) else "unknown"
                else:
                    answer = item["answer"] if tuned and improve else "unknown"
                rows.append({
                    "id": item["id"], "question": item.get("test", item.get("prompt")),
                    "category": item.get("category", "control"), "answer": answer,
                    **demo.score(answer, item["required"], item["forbidden"]),
                })
            demo.write_json(output / filename, {
                "manifest": manifest, "adapter_sha256": "adapter-hash" if tuned else None,
                "results": rows,
            })
        demo.write_json(output / "training.json", {"adapter_sha256": "adapter-hash"})

    def test_comparison_requires_measurable_gain_and_retains_result(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            output = Path(directory)
            self.reports(output)
            result = demo.compare(output)
            self.assertTrue(result["verified_improvement"])
            self.assertEqual(result["accuracy_gain"], 1.0)
            self.assertEqual(demo.read_json(output / "comparison.json"), result)
            self.assertEqual(demo.compare(output), result)

    def test_no_gain_or_control_regression_fails_explicitly(self):
        for improve, regress in ((False, False), (True, True)):
            with self.subTest(improve=improve, regress=regress):
                with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
                    output = Path(directory)
                    self.reports(output, improve=improve, regress_control=regress)
                    with self.assertRaisesRegex(RuntimeError, "threshold"):
                        demo.compare(output)
                    self.assertFalse(demo.read_json(output / "comparison.json")["verified_improvement"])

    def test_mismatched_adapter_and_missing_rows_are_rejected(self):
        for mismatch in ("adapter", "rows", "score"):
            with self.subTest(mismatch=mismatch), tempfile.TemporaryDirectory() as directory:
                output = Path(directory)
                self.reports(output)
                report = demo.read_json(output / "tuned.json")
                if mismatch == "adapter":
                    report["adapter_sha256"] = "other"
                elif mismatch == "rows":
                    report["results"].pop()
                else:
                    report["results"][0]["passed"] = False
                # Deliberately corrupt test evidence to exercise the verifier.
                (output / "tuned.json").write_text(json.dumps(report))
                with self.assertRaises(ValueError):
                    demo.compare(output)

    def test_training_without_baseline_or_over_existing_adapter_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            demo.prepare(output)
            with self.assertRaises(FileNotFoundError):
                demo.train(output)
            demo.write_json(output / "baseline.json", {
                "manifest": demo.read_json(output / "manifest.json"), "adapter_sha256": None,
            })
            (output / "adapters").mkdir()
            with self.assertRaises(FileExistsError):
                demo.train(output)

    def test_confirmation_does_not_overlap_training_or_exploratory_questions(self):
        items = demo.evaluation_items()
        self.assertEqual(len(items), 22)
        self.assertEqual(sum("prompt" in item for item in items), 6)
        data = demo.read_json(demo.CONFIRMATION)
        data["questions"][0]["question"] = demo.load_data()["topics"][0]["train"][0]
        original_read = demo.read_json
        with patch.object(demo, "read_json",
                          side_effect=lambda path: data if path == demo.CONFIRMATION else original_read(path)):
            with self.assertRaisesRegex(ValueError, "leaks"):
                demo.evaluation_items()

    def test_retained_live_reports_recompute_against_current_frozen_inputs(self):
        evidence = demo.ROOT / "polish-results"
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            output = Path(directory)
            demo.prepare(output)
            self.assertEqual(demo.read_json(output / "manifest.json"),
                             demo.read_json(evidence / "manifest.json"))
            for name in ("baseline.json", "tuned.json", "training.json", "comparison.json"):
                shutil.copyfile(evidence / name, output / name)
            self.assertEqual(demo.compare(output), demo.read_json(evidence / "comparison.json"))

    def test_stricter_audit_is_bound_to_actual_transcripts_and_counts(self):
        evidence = demo.ROOT / "polish-results"
        audit = demo.read_json(evidence / "answer-audit.json")
        for label in ("baseline", "tuned"):
            self.assertEqual(audit[f"{label}_sha256"], demo.digest(evidence / f"{label}.json"))
            self.assertEqual(audit[f"{label}_passed"],
                             sum(row[f"{label}_passed"] for row in audit["results"]))
        self.assertEqual(audit["total"], len(audit["results"]))
        self.assertEqual([row["id"] for row in audit["results"]],
                         [topic["id"] for topic in demo.load_data()["topics"]])
        self.assertEqual((audit["baseline_passed"], audit["tuned_passed"]), (1, 11))

    def test_final_adapter_repairs_the_original_control_regression(self):
        evidence = demo.ROOT / "polish-results"
        recheck = demo.read_json(evidence / "exploratory-control-recheck.json")
        self.assertEqual(recheck["adapter_sha256"],
                         demo.read_json(evidence / "training.json")["adapter_sha256"])
        controls = demo.load_data()["controls"]
        self.assertEqual([row["id"] for row in recheck["results"]], [item["id"] for item in controls])
        for row, item in zip(recheck["results"], controls):
            self.assertTrue(demo.score(row["answer"], item["required"], item["forbidden"])["passed"])


if __name__ == "__main__":
    unittest.main()
