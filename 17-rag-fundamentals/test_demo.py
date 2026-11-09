import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

import demo


class RagTests(unittest.TestCase):
    def test_chunking_preserves_all_text_and_changes_boundaries(self):
        for size in (12, 48):
            items = demo.chunks(demo.DOCUMENTS, size)
            self.assertEqual(len(items), len({item["id"] for item in items}))
            for source, text in demo.DOCUMENTS.items():
                self.assertEqual(" ".join(i["content"] for i in items if i["source"] == source), text)
                self.assertTrue(all(len(i["content"].split()) <= size for i in items))
        self.assertGreater(len(demo.chunks(demo.DOCUMENTS, 12)),
                           len(demo.chunks(demo.DOCUMENTS, 48)))
        with self.assertRaises(ValueError):
            demo.chunks(demo.DOCUMENTS, 0)

    def test_fixed_labels_and_document_metrics(self):
        self.assertEqual(demo.metrics([{"source": "x"}, {"source": "cleanup"}], "cleanup"), (1, .5))
        self.assertEqual(demo.metrics([], "cleanup"), (0, 0))
        self.assertIsNone(demo.metrics([], None))
        self.assertEqual(sum(label is None for _, label in demo.QUESTIONS), 1)

    def test_citations_and_quotes_must_be_retrieved(self):
        hits = [{"id": "a-0", "content": "Starts at 09:30.", "source": "a"}]
        valid = {"answer": "09:30", "citations": ["a-0"],
                 "quotes": [{"id": "a-0", "text": "09:30"}]}
        self.assertEqual(demo.validate_answer(valid, hits), valid)
        for bad in (
            {**valid, "citations": ["secret-0"]},
            {**valid, "quotes": [{"id": "a-0", "text": "10:30"}]},
            {**valid, "quotes": []},
            {"answer": demo.ABSTENTION, "citations": ["a-0"], "quotes": []},
        ):
            with self.assertRaises(ValueError):
                demo.validate_answer(bad, hits)
        self.assertEqual(demo.validate_answer(
            {"answer": demo.ABSTENTION, "citations": [], "quotes": []}, [])["answer"],
            demo.ABSTENTION)

    def test_model_receives_only_question_and_retrieved_evidence(self):
        client = Mock()
        client.chat.completions.create.return_value = SimpleNamespace(choices=[
            SimpleNamespace(finish_reason="stop", message=SimpleNamespace(content=(
                '{"answer":"Insufficient evidence.","citations":[],"quotes":[]}')))])
        demo.grounded_answer(client, "deployment", "Unknown?", [])
        call = client.chat.completions.create.call_args.kwargs
        self.assertEqual(call["model"], "deployment")
        self.assertIn('"evidence": []', call["messages"][1]["content"])
        self.assertNotIn("expected", call["messages"][1]["content"])

    def test_retrieval_modes_use_same_top_k_and_vector(self):
        search = Mock()
        search.search.return_value = []
        vector_type = Mock()
        with patch.dict("sys.modules", {
            "azure.search.documents.models": SimpleNamespace(VectorizedQuery=vector_type),
        }):
            for mode in ("keyword", "vector", "hybrid"):
                demo.retrieve(search, mode, "question", [1.0])
                kwargs = search.search.call_args.kwargs
                self.assertEqual(kwargs["top"], 3)
                self.assertEqual("vector_queries" in kwargs, mode != "keyword")
                self.assertEqual(kwargs["search_text"], None if mode == "vector" else "question")

    def test_index_wait_is_bounded(self):
        search = Mock()
        search.get_document_count.side_effect = [0, 4]
        with patch.object(demo, "sleep"):
            demo.wait_for_index(search, 4)
        with self.assertRaises(TimeoutError):
            demo.wait_for_index(search, 4, timeout=0)

    def test_initial_embeddings_retry_only_missing_deployment_within_deadline(self):
        class FakeNotFoundError(Exception):
            def __init__(self, code):
                self.code = code

        client = Mock()
        response = SimpleNamespace(data=[SimpleNamespace(embedding=[1.0])])
        client.embeddings.create.side_effect = [
            FakeNotFoundError("DeploymentNotFound"),
            *[response for _ in demo.QUESTIONS],
        ]
        with patch.dict("sys.modules", {"openai": SimpleNamespace(NotFoundError=FakeNotFoundError)}), \
                patch.object(demo, "monotonic", side_effect=[0, 1]), \
                patch.object(demo, "sleep") as sleep:
            vectors = demo.initial_query_vectors(client, "embedding", timeout=30)
        self.assertEqual(len(vectors), len(demo.QUESTIONS))
        sleep.assert_called_once_with(15)

        for code, now in (("OtherNotFound", 1), ("DeploymentNotFound", 31)):
            client.embeddings.create.side_effect = FakeNotFoundError(code)
            with self.subTest(code=code, now=now), \
                    patch.dict("sys.modules", {"openai": SimpleNamespace(NotFoundError=FakeNotFoundError)}), \
                    patch.object(demo, "monotonic", side_effect=[0, now]), \
                    patch.object(demo, "sleep") as sleep:
                with self.assertRaises(FakeNotFoundError):
                    demo.initial_query_vectors(client, "embedding", timeout=30)
                sleep.assert_not_called()


if __name__ == "__main__":
    unittest.main()
