import unittest
from unittest.mock import Mock, patch
import wave

import httpx
import demo
from generate_assets import pdf_bytes


def response(modality="document"):
    fields = {
        "Workshop": {"valueString": "Cedar Workshop", "confidence": 0.98, "source": "D(1,1,1,2,1,2,2,1,2)"},
        "Room": {"valueString": "Blue Room", "confidence": 0.99, "source": "D(1,1,2,2,2,2,3,1,3)"},
        "Seats": {"valueInteger": 12, "confidence": 0.97, "source": "D(1,1,3,2,3,2,4,1,4)"},
    }
    content = {
        "markdown": "Cedar Workshop. Blue Room. 12 seats.", "fields": fields,
        "pages": [{"pageNumber": 1}],
    }
    if modality == "audio":
        content["transcriptPhrases"] = [
            {"startTimeMs": 0, "endTimeMs": 3000, "text": "Cedar Workshop."},
            {"startTimeMs": 3000, "endTimeMs": 5000, "text": "Blue Room."},
            {"startTimeMs": 5000, "endTimeMs": 6500, "text": "Twelve seats."},
        ]
        for field in fields.values():
            field.pop("confidence")
            field.pop("source")
    return {"status": "Succeeded", "id": "offline-unit-fixture-only", "result": {"apiVersion": demo.API_VERSION, "contents": [content]}}


class ContentTests(unittest.TestCase):
    def test_custom_schema_native_bases(self):
        for modality in ("document", "audio"):
            analyzer = demo.schema(modality, "gpt-5.4-mini", "text-embedding-3-large")
            self.assertEqual(analyzer["baseAnalyzerId"], f"prebuilt-{modality}")
            self.assertEqual(set(analyzer["fieldSchema"]["fields"]), set(demo.EXPECTED))

    def test_explicit_extraction_not_generation(self):
        fields = demo.schema("document", "x", "y")["fieldSchema"]["fields"]
        self.assertTrue(all(field["method"] == "extract" for field in fields.values()))

    def test_persisted_custom_schema(self):
        analyzer = demo.schema("document", "x", "y")
        demo.validate_analyzer(analyzer, analyzer)
        changed = demo.schema("document", "x", "y")
        changed["fieldSchema"]["fields"]["Seats"]["method"] = "generate"
        with self.assertRaisesRegex(ValueError, "custom extraction"):
            demo.validate_analyzer(changed, analyzer)

    def test_short_polygon_rejected(self):
        body = response()
        body["result"]["contents"][0]["fields"]["Seats"]["source"] = "D(1,1,2)"
        with self.assertRaisesRegex(ValueError, "polygon"):
            demo.validate_result(body, "document")

    def test_missing_operation_id_rejected(self):
        body = response()
        body.pop("id")
        with self.assertRaisesRegex(ValueError, "operation ID"):
            demo.validate_result(body, "document")

    def test_preflight_rejects_unsupported_location(self):
        with self.assertRaisesRegex(ValueError, "not documented"):
            demo.preflight("antarctica", "gpt-5.4-mini", "text-embedding-3-large")

    def test_preflight_rejects_unbounded_capacity(self):
        with self.assertRaisesRegex(ValueError, "between 1 and 10"):
            demo.preflight("eastus2", "gpt-5.4-mini", "text-embedding-3-large", 11)

    def test_document_confidence_enabled(self):
        self.assertTrue(demo.schema("document", "x", "y")["config"]["estimateFieldSourceAndConfidence"])

    def test_audio_no_fake_confidence(self):
        self.assertNotIn("estimateFieldSourceAndConfidence", demo.schema("audio", "x", "y")["config"])
        fields = demo.validate_result(response("audio"), "audio")
        self.assertIsNone(fields["Seats"]["confidence"])
        self.assertEqual(fields["Seats"]["confidenceAvailability"], "not-emitted-by-GA-audio")

    def test_audio_uses_service_supported_generation_method(self):
        analyzer = demo.schema("audio", "gpt-5.2", "text-embedding-3-large")
        self.assertTrue(all(field["method"] == "generate" for field in analyzer["fieldSchema"]["fields"].values()))
        demo.validate_analyzer(analyzer, analyzer)

    def test_invalid_modality(self):
        with self.assertRaises(ValueError):
            demo.schema("prebuilt-invoice", "x", "y")

    def test_grounded_document(self):
        fields = demo.validate_result(response(), "document")
        self.assertEqual(fields["Workshop"]["value"], "Cedar Workshop")

    def test_named_fact_case_does_not_fabricate_value(self):
        body = response("audio")
        body["result"]["contents"][0]["fields"]["Workshop"]["valueString"] = "Cedar workshop"
        fields = demo.validate_result(body, "audio")
        self.assertEqual(fields["Workshop"]["value"], "Cedar workshop")

    def test_known_fact_mismatch_rejected(self):
        body = response()
        body["result"]["contents"][0]["fields"]["Seats"]["valueInteger"] = 13
        with self.assertRaisesRegex(ValueError, "expected"):
            demo.validate_result(body, "document")

    def test_missing_field_rejected(self):
        body = response()
        del body["result"]["contents"][0]["fields"]["Room"]
        with self.assertRaises(ValueError):
            demo.validate_result(body, "document")

    def test_missing_confidence_rejected(self):
        body = response()
        del body["result"]["contents"][0]["fields"]["Seats"]["confidence"]
        with self.assertRaisesRegex(ValueError, "confidence"):
            demo.validate_result(body, "document")

    def test_low_confidence_rejected(self):
        body = response()
        body["result"]["contents"][0]["fields"]["Seats"]["confidence"] = 0.79
        with self.assertRaisesRegex(ValueError, "human review"):
            demo.validate_result(body, "document")

    def test_invalid_confidence_rejected(self):
        for value in (True, float("nan"), 1.1, -0.2):
            body = response()
            body["result"]["contents"][0]["fields"]["Seats"]["confidence"] = value
            with self.assertRaises(ValueError):
                demo.validate_result(body, "document")

    def test_missing_grounding_rejected(self):
        body = response()
        del body["result"]["contents"][0]["fields"]["Seats"]["source"]
        with self.assertRaisesRegex(ValueError, "grounding"):
            demo.validate_result(body, "document")

    def test_wrong_grounding_page_rejected(self):
        body = response()
        body["result"]["contents"][0]["fields"]["Seats"]["source"] = "D(2,1,1,2,1,2,2,1,2)"
        with self.assertRaisesRegex(ValueError, "missing service page"):
            demo.validate_result(body, "document")

    def test_missing_audio_timing_rejected(self):
        body = response("audio")
        body["result"]["contents"][0]["transcriptPhrases"][-1].pop("endTimeMs")
        with self.assertRaisesRegex(ValueError, "time-grounded"):
            demo.validate_result(body, "audio")

    def test_failed_and_preview_rejected(self):
        for change in ("status", "api"):
            body = response()
            if change == "status":
                body["status"] = "Failed"
            else:
                body["result"]["apiVersion"] = "2026-06-01-preview"
            with self.assertRaises(ValueError):
                demo.validate_result(body, "document")

    def test_pdf_fixture_deterministic(self):
        self.assertEqual((demo.ROOT / "assets/notice.pdf").read_bytes(), pdf_bytes((demo.ROOT / "assets/notice.txt").read_text()))

    def test_audio_fixture(self):
        with wave.open(str(demo.ROOT / "assets/notice.wav"), "rb") as audio:
            self.assertEqual((audio.getnchannels(), audio.getsampwidth(), audio.getframerate()), (1, 2, 24000))
            self.assertGreater(audio.getnframes(), 24000)
            self.assertLess(audio.getnframes(), 24000 * 30)

    def test_root_outputs_and_serialized_writes(self):
        with patch.dict(demo.os.environ, {}, clear=True), patch.object(demo, "saved_outputs", return_value={"endpoint": {"value": "https://example.services.ai.azure.com"}}):
            self.assertIn("example", demo.setting("CONTENT_UNDERSTANDING_ENDPOINT", "endpoint"))
        bicep = (demo.ROOT / "infra/main.bicep").read_text()
        self.assertIn("dependsOn: [completion]", bicep)
        self.assertIn("dependsOn: [embedding]", bicep)
        self.assertIn("disableLocalAuth: true", bicep)

    def test_untrusted_poll_url_rejected(self):
        credential = Mock()
        cu = demo.ContentUnderstanding("https://example.services.ai.azure.com", credential, Mock())
        with self.assertRaisesRegex(ValueError, "untrusted"):
            cu.request("GET", "https://other.example/steal")
        credential.get_token.assert_not_called()

    def test_native_binary_submission_and_entra_scope(self):
        credential = Mock()
        credential.get_token.return_value.token = "unit-test-only-not-a-real-token"
        requests = []
        def handler(request):
            requests.append(request)
            return httpx.Response(200, json={"status": "Succeeded"})
        with httpx.Client(transport=httpx.MockTransport(handler)) as http:
            cu = demo.ContentUnderstanding("https://example.services.ai.azure.com", credential, http)
            cu.request("POST", "analyzers/workshop_audio:analyzeBinary", content=b"RIFF-test", content_type="audio/wav")
        self.assertEqual(requests[0].headers["content-type"], "audio/wav")
        self.assertEqual(requests[0].content, b"RIFF-test")
        self.assertIn(":analyzeBinary", str(requests[0].url))
        self.assertIn("api-version=2025-11-01", str(requests[0].url))
        credential.get_token.assert_called_once_with("https://cognitiveservices.azure.com/.default")

    def test_existing_native_analyzer_is_reused(self):
        analyzer = demo.schema("document", "x", "y")
        credential = Mock()
        credential.get_token.return_value.token = "unit-test-only-not-a-real-token"
        requests = []
        def handler(request):
            requests.append(request.method)
            return httpx.Response(200, json=analyzer)
        with httpx.Client(transport=httpx.MockTransport(handler)) as http:
            cu = demo.ContentUnderstanding("https://example.services.ai.azure.com", credential, http)
            self.assertEqual(cu.ensure_analyzer("workshop_document", analyzer), analyzer)
        self.assertEqual(requests, ["GET"])

    def test_native_analyzer_created_only_when_missing(self):
        analyzer = demo.schema("document", "x", "y")
        credential = Mock()
        credential.get_token.return_value.token = "unit-test-only-not-a-real-token"
        requests = []
        def handler(request):
            requests.append(request.method)
            return httpx.Response(404, json={"error": "missing"}) if len(requests) == 1 else httpx.Response(201 if request.method == "PUT" else 200, json=analyzer)
        with httpx.Client(transport=httpx.MockTransport(handler)) as http:
            cu = demo.ContentUnderstanding("https://example.services.ai.azure.com", credential, http)
            self.assertEqual(cu.ensure_analyzer("workshop_document", analyzer), analyzer)
        self.assertEqual(requests, ["GET", "PUT", "GET"])

    def test_transient_transport_retried_but_not_simulated(self):
        credential = Mock()
        credential.get_token.return_value.token = "unit-test-only-not-a-real-token"
        calls = []
        def handler(request):
            calls.append(request.method)
            if len(calls) == 1:
                raise httpx.ReadError("unit-test connection reset", request=request)
            return httpx.Response(200, json={"status": "Succeeded"})
        with httpx.Client(transport=httpx.MockTransport(handler)) as http, patch.object(demo.time, "sleep"):
            cu = demo.ContentUnderstanding("https://example.services.ai.azure.com", credential, http)
            self.assertEqual(cu.request("GET", "defaults").status_code, 200)
        self.assertEqual(calls, ["GET", "GET"])

    def test_transport_retry_budget_is_bounded(self):
        credential = Mock()
        credential.get_token.return_value.token = "unit-test-only-not-a-real-token"
        calls = []
        def handler(request):
            calls.append(request.method)
            raise httpx.ReadError("unit-test connection reset", request=request)
        with httpx.Client(transport=httpx.MockTransport(handler)) as http, patch.object(demo.time, "sleep"):
            cu = demo.ContentUnderstanding("https://example.services.ai.azure.com", credential, http)
            with self.assertRaisesRegex(RuntimeError, "three real attempts"):
                cu.request("GET", "defaults")
        self.assertEqual(len(calls), 3)

    def test_poll_deadline_is_bounded(self):
        credential = Mock()
        credential.get_token.return_value.token = "unit-test-only-not-a-real-token"
        def handler(request):
            return httpx.Response(200, json={"status": "Running"})
        with httpx.Client(transport=httpx.MockTransport(handler)) as http, patch.object(demo.time, "sleep"), patch.object(demo.time, "monotonic", side_effect=[0, 0, 601]):
            cu = demo.ContentUnderstanding("https://example.services.ai.azure.com", credential, http)
            submitted = httpx.Response(202, headers={"Operation-Location": "https://example.services.ai.azure.com/contentunderstanding/analyzerResults/offline-test"})
            with self.assertRaisesRegex(TimeoutError, "600-second"):
                cu.poll(submitted)


if __name__ == "__main__":
    unittest.main()
