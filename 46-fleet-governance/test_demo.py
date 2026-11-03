"""Offline fleet contracts, not simulated live security/telemetry evidence."""
import json
import os
from pathlib import Path
import unittest
from unittest.mock import Mock, patch
from uuid import uuid4

from azure.ai.projects.models import ExternalAgentDefinition
import httpx
import demo


class FleetTests(unittest.TestCase):
    def test_two_synthetic_projects(self):
        self.assertEqual(len(demo.DATA["projects"]), 2)
        self.assertEqual(len(demo.DATA["agents"]), 2)
        self.assertEqual(demo.DATA["label"], "SYNTHETIC WORKSHOP DATA")

    def test_external_native_definition(self):
        definition = ExternalAgentDefinition(otel_agent_id=demo.DATA["externalTelemetryId"]).as_dict()
        self.assertEqual(definition["kind"], "external")
        self.assertEqual(definition["otel_agent_id"], demo.DATA["externalTelemetryId"])

    def test_external_runtime_is_actual_function(self):
        self.assertEqual(demo.external_runtime(demo.DATA["question"]), "36 months")

    def test_external_unknown_question_not_invented(self):
        self.assertNotEqual(demo.external_runtime("Other business data?"), "36 months")

    def test_correlation_both_projects(self):
        run = str(uuid4())
        inventory = [
            {"projectResourceId": "p1", "agents": [{"name": "a", "kind": "prompt"}]},
            {"projectResourceId": "p2", "agents": [{"name": "b", "kind": "prompt"}]},
        ]
        rows = [{"ProjectId": "p1", "AgentName": "a", "RunId": run, "Traces": 1},
                {"ProjectId": "p2", "AgentName": "b", "RunId": run, "Traces": 2}]
        self.assertEqual([x["traceCount"] for x in demo.correlate(inventory, rows, run)], [1, 2])

    def test_correlation_excludes_other_runs(self):
        inventory = [{"projectResourceId": "p1", "agents": [{"name": "a", "kind": "prompt"}]}]
        rows = [{"ProjectId": "p1", "AgentName": "a", "RunId": "other", "Traces": 99}]
        self.assertEqual(demo.correlate(inventory, rows, "current")[0]["traceCount"], 0)

    def test_correlation_requires_registered_telemetry_id(self):
        inventory = [{"projectResourceId": "p1", "agents": [
            {"name": "a", "kind": "external", "telemetryId": "registered.id"},
        ]}]
        rows = [{"ProjectId": "p1", "AgentName": "a", "RunId": "run",
                 "OtelAgentId": "unregistered.id", "Traces": 99}]
        self.assertEqual(demo.correlate(inventory, rows, "run")[0]["traceCount"], 0)

    def test_correlation_does_not_merge_same_names_across_projects(self):
        inventory = [
            {"projectResourceId": "p1", "agents": [{"name": "a", "kind": "prompt"}]},
            {"projectResourceId": "p2", "agents": [{"name": "a", "kind": "prompt"}]},
        ]
        rows = [{"ProjectId": "p1", "AgentName": "a", "RunId": "run", "Traces": 1}]
        self.assertEqual([x["traceCount"] for x in demo.correlate(inventory, rows, "run")], [1, 0])

    def test_query_correlates_native_ids(self):
        run = str(uuid4())
        query = demo.telemetry_query(run)
        self.assertIn("gen_ai.agent.id", query)
        self.assertIn("workshop.project_id", query)
        self.assertIn(run, query)

    def test_query_rejects_injection(self):
        with self.assertRaises(ValueError):
            demo.telemetry_query('"; union business_content')

    def test_missing_outputs_block(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(Path, "exists", return_value=False):
            with self.assertRaises(demo.Blocked):
                demo.configuration()

    def test_errors_never_log_credentials(self):
        credential = Mock()
        credential.get_token.return_value.token = "SECRET"
        transport = httpx.MockTransport(lambda request: httpx.Response(
            403, json={"error": {"code": "Denied", "message": "SECRET"}}))
        with httpx.Client(transport=transport) as http:
            with self.assertRaises(demo.Blocked) as caught:
                demo.API(credential, http).request("GET", "https://example.test")
        self.assertNotIn("SECRET", str(caught.exception))

    def test_entra_failure_preserves_only_code(self):
        message = demo.safe_error(RuntimeError("AADSTS500014; private diagnostic"))
        self.assertIn("AADSTS500014", message)
        self.assertNotIn("private diagnostic", message)

    def test_policy_is_native_isolated_audit(self):
        template = (demo.HERE / "infra/main.bicep").read_text()
        self.assertIn(demo.POLICY_DEFINITION, template)
        self.assertIn("effect: { value: 'Audit' }", template)
        self.assertNotIn("Microsoft.Authorization/policyDefinitions@", template)
        self.assertNotIn("effect: { value: 'Deny' }", template)

    def test_child_writes_serialized(self):
        template = (demo.HERE / "infra/main.bicep").read_text()
        self.assertIn("dependsOn: [operations]", template)
        self.assertIn("dependsOn: [operationsConnection]", template)
        self.assertEqual(template.count("capacity: 5"), 1)

    def test_telemetry_enforces_entra(self):
        template = (demo.HERE / "infra/main.bicep").read_text()
        self.assertIn("DisableLocalAuth: true", template)
        source = (demo.HERE / "demo.py").read_text()
        self.assertIn("disable_offline_storage=True", source)
        self.assertIn("credential=api.credential", source)
        self.assertIn("set_tracer_provider(provider)", source)


if __name__ == "__main__":
    unittest.main()
