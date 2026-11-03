from copy import deepcopy
from datetime import datetime, timezone
import json
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

import demo
from azure.core.credentials import AccessToken
from azure.core.exceptions import ServiceResponseError
from azure.ai.projects.models import TimerRoutineTrigger, RoutineAuthorization


ROUTINE = {
    "name": "once", "enabled": False, "authorization": {"identity": "creator"},
    "triggers": {"once": {"type": "timer", "at": "2026-11-03T11:00:00Z"}},
}
RUN = {"phase": "completed", "trigger_type": "timer", "attempt_source": "timer_delivery",
       "response_id": "resp_123", "conversation_id": "conv_123"}


class NativeRoutineTests(unittest.TestCase):
    def test_root_no_argument_default_is_creator(self):
        self.assertEqual(demo.argument_parser().parse_args([]).dispatch_identity, "creator")

    def test_main_no_argument_passes_creator_to_producer(self):
        with patch.object(demo, "settings", return_value={"projectEndpoint": "https://offline.invalid"}), \
                patch.object(demo, "AzureCliCredential"), patch.object(demo, "AIProjectClient"), \
                patch.object(demo, "demonstrate") as producer, patch.object(demo, "save"), \
                patch("builtins.print"):
            self.assertEqual(demo.main([]), 0)
        self.assertEqual(producer.call_args.kwargs["dispatch_identity"], "creator")

    def test_updating_arm_project_blocks_before_native_creation(self):
        with patch.object(demo, "settings", return_value={"projectResourceId": "/project"}), \
                patch.object(demo.subprocess, "run", return_value=SimpleNamespace(
                    stdout='{"provisioningState":"Updating"}')), \
                patch.object(demo, "demonstrate") as producer, patch.object(demo, "save"), \
                patch("builtins.print"):
            self.assertEqual(demo.main([]), 1)
        producer.assert_not_called()

    def test_identity_failure_diagnostics_exclude_authorization_headers(self):
        error = MagicMock()
        error.response = SimpleNamespace(status_code=400, headers={
            "x-ms-request-id": "native-request", "Authorization": "never-record",
        })
        report = {"agent": {"blueprint": {"client_id": "blueprint"}}}
        with patch.object(demo.subprocess, "run", return_value=SimpleNamespace(
                returncode=1, stdout="", stderr="Directory read forbidden.")), patch.object(demo, "save"):
            demo.identity_failure_evidence(error, report)
        self.assertEqual(report["service_failure"]["request_ids"], {"x-ms-request-id": "native-request"})
        self.assertEqual(report["identity_directory_readback"]["blueprint"]["returncode"], 1)

    def test_agent_identity_is_explicit_opt_in(self):
        self.assertEqual(demo.argument_parser().parse_args(
            ["--dispatch-identity", "agent"]).dispatch_identity, "agent")
        body = demo.routine_body("agent", "conv", datetime.now(timezone.utc), "agent")
        self.assertEqual(body["authorization"], {"identity": "agent"})
        value = deepcopy(ROUTINE)
        value["authorization"]["identity"] = "agent"
        demo.verify_routine(value, "once", False, "agent")

    def test_default_creator_reaches_actual_sdk_wire_body(self):
        credential = MagicMock(spec=["get_token"])
        credential.get_token.return_value = AccessToken("offline-test-token", 2147483647)
        captured = {}

        def capture(request):
            captured.update(json.loads(request.http_request.body))
            raise ServiceResponseError("Offline hook stops before transport.")

        body = demo.routine_body("agent", "conv", datetime(2026, 11, 3, tzinfo=timezone.utc))
        with demo.AIProjectClient("https://offline.invalid/api/projects/test", credential,
                                  allow_preview=True) as project:
            with self.assertRaises(ServiceResponseError):
                project.beta.routines.create_or_update(
                    "once", body=body, raw_request_hook=capture, retry_total=0)
        self.assertEqual(captured, body)
        self.assertEqual(captured["authorization"], {"identity": "creator"})
        self.assertFalse(captured["enabled"])

    def test_invalid_identity_cannot_select_service_default(self):
        with self.assertRaises(ValueError):
            demo.routine_body("agent", "conv", datetime.now(timezone.utc), "automatic")

    def test_explicit_agent_identity_failure_never_falls_back(self):
        project = MagicMock()
        project.agents.get.return_value.as_dict.return_value = {
            "agent_endpoint": {"authorization_schemes": [{"type": "Entra"}]},
        }
        project.beta.routines.create_or_update.side_effect = RuntimeError("Agent identity issuance blocked.")
        project.beta.routines.list.return_value = []
        report = {}
        with patch.object(demo, "save"), self.assertRaisesRegex(RuntimeError, "issuance blocked"):
            demo.demonstrate(project, {"deployment": "test"}, report, dispatch_identity="agent")
        project.beta.routines.create_or_update.assert_called_once()
        self.assertEqual(project.beta.routines.create_or_update.call_args.kwargs["body"]["authorization"],
                         {"identity": "agent"})
        project.beta.routines.enable.assert_not_called()
        self.assertTrue(report["routine_deleted"])

    def test_ignored_creator_identity_is_rejected_before_enable(self):
        project = MagicMock()
        project.agents.get.return_value.as_dict.return_value = {
            "agent_endpoint": {"authorization_schemes": [{"type": "Entra"}]},
        }

        def ignored_identity(routine_name, body):
            value = MagicMock()
            value.as_dict.return_value = {**body, "name": routine_name, "authorization": {"identity": "agent"}}
            return value

        project.beta.routines.create_or_update.side_effect = ignored_identity
        project.beta.routines.list.return_value = []
        report = {}
        with patch.object(demo, "save"), self.assertRaisesRegex(RuntimeError, "creator dispatch identity"):
            demo.demonstrate(project, {"deployment": "test"}, report)
        project.beta.routines.enable.assert_not_called()
        self.assertTrue(report["routine_deleted"])

    def test_native_timer_discriminator(self):
        self.assertEqual(TimerRoutineTrigger().as_dict()["type"], "timer")

    def test_explicit_creator_authorization(self):
        self.assertEqual(RoutineAuthorization(identity="creator").as_dict()["identity"], "creator")

    def test_disabled_native_readback(self):
        demo.verify_routine(ROUTINE, "once", False)

    def test_enabled_native_readback(self):
        value = deepcopy(ROUTINE)
        value["enabled"] = True
        demo.verify_routine(value, "once", True)

    def test_identity_mismatch_rejected(self):
        value = deepcopy(ROUTINE)
        value["authorization"]["identity"] = "agent"
        with self.assertRaises(RuntimeError):
            demo.verify_routine(value, "once", False)

    def test_local_cron_is_not_timer_evidence(self):
        value = deepcopy(ROUTINE)
        value["triggers"]["once"]["type"] = "schedule"
        with self.assertRaises(RuntimeError):
            demo.verify_routine(value, "once", False)

    def test_real_timer_run(self):
        demo.verify_run(RUN)

    def test_native_timer_can_return_response_without_conversation(self):
        value = {key: value for key, value in RUN.items() if key != "conversation_id"}
        demo.verify_run(value)
        demo.verify_response(value, {"id": "resp_123", "status": "completed"}, "marker", "marker")

    def test_manual_dispatch_is_not_timer_delivery(self):
        value = {**RUN, "attempt_source": "manual_dispatch"}
        with self.assertRaises(RuntimeError):
            demo.verify_run(value)

    def test_response_link_missing_rejected(self):
        with self.assertRaises(RuntimeError):
            demo.verify_run({**RUN, "response_id": None})

    def test_unique_response_link(self):
        response = {"id": "resp_123", "status": "completed", "conversation": {"id": "conv_123"}}
        demo.verify_response(RUN, response, "marker", "marker")

    def test_unrelated_response_rejected(self):
        with self.assertRaises(RuntimeError):
            demo.verify_response(RUN, {"id": "other", "status": "completed"}, "marker", "marker")

    def test_wrong_marker_rejected(self):
        with self.assertRaises(RuntimeError):
            demo.verify_response(RUN, {"id": "resp_123", "status": "completed"}, "wrong", "marker")

    def test_failed_lifecycle_deletes_routine_and_agent(self):
        project = MagicMock()
        project.agents.get.return_value.as_dict.return_value = {
            "agent_endpoint": {"authorization_schemes": [{"type": "Entra"}]},
        }
        def created(routine_name, body):
            value = MagicMock()
            value.as_dict.return_value = {**body, "name": routine_name}
            return value

        project.beta.routines.create_or_update.side_effect = created
        project.beta.routines.get.return_value.as_dict.return_value = {}
        project.beta.routines.list.return_value = []
        report = {}
        with patch.object(demo, "save"), self.assertRaises(RuntimeError):
            demo.demonstrate(project, {"deployment": "test"}, report)
        project.beta.routines.delete.assert_called_once()
        project.agents.delete.assert_called_once()
        self.assertTrue(report["routine_deleted"])
        submitted = project.beta.routines.create_or_update.call_args.kwargs["body"]
        self.assertEqual(submitted["authorization"], {"identity": "creator"})
        self.assertEqual(report["submitted_routine_body"], submitted)
        project.beta.routines.create_or_update.assert_called_once()

    def test_unknown_post_outcome_still_deletes_named_native_routine(self):
        project = MagicMock()
        project.agents.get.return_value.as_dict.return_value = {
            "agent_endpoint": {"authorization_schemes": [{"type": "Entra"}]},
        }
        project.beta.routines.create_or_update.side_effect = ServiceResponseError("Connection closed after POST.")
        project.beta.routines.list.return_value = []
        report = {}
        with patch.object(demo, "save"), self.assertRaises(ServiceResponseError):
            demo.demonstrate(project, {"deployment": "test"}, report)
        project.beta.routines.delete.assert_called_once()
        project.agents.delete.assert_called_once()
        self.assertTrue(report["routine_deleted"])


if __name__ == "__main__":
    unittest.main()
