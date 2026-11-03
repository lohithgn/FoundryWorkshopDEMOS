"""Offline contracts; these tests never stand in for live access validation."""
import json
import os
from pathlib import Path
import unittest
from unittest.mock import Mock, patch

import httpx
import demo


def settings() -> dict:
    return {
        "endpoint": "https://synthetic.openai.azure.com",
        "deployment": "gpt-4.1-mini", "embeddingDeployment": "text-embedding-3-small",
        "storageResourceId": "/subscriptions/s/resourceGroups/rg-foundry-governed-iq-demo/providers/Microsoft.Storage/storageAccounts/synthetic",
        "storageEndpoint": "https://synthetic.blob.core.windows.net",
        "searchEndpoint": "https://synthetic.search.windows.net",
        "searchResourceId": "/subscriptions/s/search",
        "projectResourceId": "/subscriptions/s/resourceGroups/rg-foundry-governed-iq-demo/providers/Microsoft.CognitiveServices/accounts/a/projects/p",
        "projectEndpoint": "https://synthetic.services.ai.azure.com/api/projects/p",
        "containerResourceId": "/subscriptions/s/container",
        "tenantId": "a1d6c791-d37f-46f2-860a-105cc2a00fe3",
        "principalId": "2953e18f-c581-471a-b474-35522c43acab",
    }


class GovernedIQTests(unittest.TestCase):
    def test_native_source_ingests_rbac(self):
        source = demo.source_definition(settings())
        self.assertEqual(source["azureBlobParameters"]["ingestionParameters"]["ingestionPermissionOptions"],
                         ["rbacScope"])

    def test_source_uses_managed_identity_no_key(self):
        source = demo.source_definition(settings())
        self.assertTrue(source["azureBlobParameters"]["connectionString"].startswith("ResourceId="))
        self.assertNotIn("apiKey", json.dumps(source))

    def test_blob_not_adls(self):
        self.assertFalse(demo.source_definition(settings())["azureBlobParameters"]["isADLSGen2"])

    def test_query_has_no_application_filter(self):
        body = demo.retrieval_body()
        self.assertNotIn("$filter", json.dumps(body))
        self.assertNotIn('"filter"', json.dumps(body))

    def test_actual_identity_header(self):
        api = Mock()
        api.request.return_value = {}
        demo.retrieve(api, settings(), "in-memory-test-value")
        self.assertEqual(api.request.call_args.kwargs["headers"],
                         {"x-ms-query-source-authorization": "in-memory-test-value"})

    def test_missing_identity_omits_header(self):
        api = Mock()
        api.request.return_value = {}
        demo.retrieve(api, settings(), None)
        self.assertEqual(api.request.call_args.kwargs["headers"], {})

    def test_missing_secondary_is_blocked_not_faked(self):
        api = Mock()
        with patch.dict(os.environ, {}, clear=True), patch.object(demo, "retrieve", side_effect=[
            {"response": demo.MARKER}, {},
        ]):
            result = demo.test_iq_access(api, settings())
        self.assertEqual(result["authorizedCaller"], "PASS")
        self.assertEqual(result["missingSourceIdentityDenied"], "PASS")
        self.assertEqual(result["twoRealUsers"], "BLOCKED")

    def test_unauthenticated_leak_fails_closed(self):
        with patch.object(demo, "retrieve", side_effect=[
            {"response": demo.MARKER}, {"response": demo.MARKER},
        ]):
            with self.assertRaisesRegex(RuntimeError, "SECURITY FAILURE"):
                demo.test_iq_access(Mock(), settings())

    def test_unauthenticated_reference_leak_fails_closed(self):
        with patch.object(demo, "retrieve", side_effect=[
            {"response": demo.MARKER}, {"references": [{"id": "restricted-document"}]},
        ]):
            with self.assertRaisesRegex(RuntimeError, "SECURITY FAILURE"):
                demo.test_iq_access(Mock(), settings())

    def test_positive_missing_content_blocks(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(demo, "retrieve", return_value={}):
            result = demo.test_iq_access(Mock(), settings())
        self.assertEqual(result["authorizedCaller"], "BLOCKED")
        self.assertEqual(result["missingSourceIdentityDenied"], "PASS")

    def test_fabric_connection_stores_only_guids(self):
        workspace = "b0cd853b-1c15-4bce-a0f1-71461a93f2c9"
        artifact = "62c8bb78-478a-41d2-ade7-0f5493369ecb"
        body = demo.fabric_connection_body(workspace, artifact)
        self.assertEqual(body["properties"]["credentials"]["keys"],
                         {"workspace_id": workspace, "artifact_id": artifact})
        self.assertNotIn("secret", json.dumps(body).lower())

    def test_fabric_invalid_id_rejected(self):
        with self.assertRaises(ValueError):
            demo.fabric_connection_body("not-a-guid", "not-a-guid")

    def test_native_fabric_tool_sdk_serialization(self):
        value = demo.fabric_definition("gpt-4.1-mini", "/subscriptions/s/connections/f").as_dict()
        self.assertEqual(value["tools"][0]["type"], "fabric_dataagent_preview")
        self.assertIn("/subscriptions/s/connections/f", json.dumps(value))

    def test_service_failure_does_not_log_body_or_token(self):
        credential = Mock()
        credential.get_token.return_value.token = "SECRET-TOKEN"
        transport = httpx.MockTransport(lambda request: httpx.Response(
            403, json={"error": {"code": "Forbidden", "message": "SECRET-TOKEN"}},
            headers={"x-ms-request-id": "request-45"}))
        with httpx.Client(transport=transport) as client:
            with self.assertRaises(demo.Blocked) as caught:
                demo.API(credential, client).request("GET", "https://example.test", "scope")
        self.assertIn("Forbidden", str(caught.exception))
        self.assertNotIn("SECRET-TOKEN", str(caught.exception))

    def test_identity_blocker_retains_only_safe_code(self):
        error = RuntimeError("AADSTS500014: service principal disabled; PRIVATE-DETAIL")
        message = demo.safe_error(error)
        self.assertIn("AADSTS500014", message)
        self.assertNotIn("PRIVATE-DETAIL", message)

    def test_missing_configuration_fails(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(Path, "exists", return_value=False):
            with self.assertRaises(demo.Blocked):
                demo.config()

    def test_all_documents_synthetic(self):
        self.assertIn("SYNTHETIC WORKSHOP DATA", (demo.HERE / "data/restricted.txt").read_text())
        self.assertIn("SYNTHETIC WORKSHOP DATA", (demo.HERE / "data/fabric-sales.csv").read_text())

    def test_small_capacity_and_keyless_infra(self):
        template = (demo.HERE / "infra/main.bicep").read_text()
        self.assertEqual(template.count("capacity: 1"), 1)
        self.assertEqual(template.count("capacity: 5"), 1)
        self.assertIn("disableLocalAuth: true", template)
        self.assertIn("allowSharedKeyAccess: false", template)


if __name__ == "__main__":
    unittest.main()
