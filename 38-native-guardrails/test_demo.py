import copy
import json
import unittest
from unittest.mock import Mock, patch

from demo import (native_intervention, agent_intervention, request_restricted_with_propagation,
                  request_with_filter_readiness, settings, usable_unfiltered_response)


class NativeGuardrailTests(unittest.TestCase):
    def setUp(self):
        self.body = {"error": {
            "code": "content_filter",
            "innererror": {
                "code": "ResponsibleAIPolicyViolation",
                "content_filter_result": {"custom_blocklists": {
                    "filtered": True, "details": [{"filtered": True, "id": "workshop-synthetic-marker"}],
                }},
            },
        }}

    def test_real_native_shape(self):
        self.assertTrue(native_intervention(400, self.body))

    def test_wrong_http_status(self):
        self.assertFalse(native_intervention(403, self.body))

    def test_generic_filter_does_not_pass(self):
        self.assertFalse(native_intervention(400, {"error": {"code": "content_filter"}}))

    def test_classifier_does_not_pass(self):
        self.assertFalse(native_intervention(200, {"filtered": True}))

    def test_missing_details_does_not_pass(self):
        body = copy.deepcopy(self.body)
        body["error"]["innererror"]["content_filter_result"]["custom_blocklists"]["details"] = []
        self.assertFalse(native_intervention(400, body))

    def test_unfiltered_does_not_pass(self):
        body = copy.deepcopy(self.body)
        body["error"]["innererror"]["content_filter_result"]["custom_blocklists"]["filtered"] = False
        self.assertFalse(native_intervention(400, body))

    def test_observed_current_api_list_shape(self):
        body = copy.deepcopy(self.body)
        body["error"]["innererror"]["content_filter_result"]["custom_blocklists"] = [
            {"filtered": True, "id": "workshop-synthetic-marker"},
        ]
        self.assertTrue(native_intervention(400, body))

    def test_other_blocklist_does_not_pass(self):
        body = copy.deepcopy(self.body)
        body["error"]["innererror"]["content_filter_result"]["custom_blocklists"] = [
            {"filtered": True, "id": "some-other-policy"},
        ]
        self.assertFalse(native_intervention(400, body))

    def test_unfiltered_current_shape_does_not_pass(self):
        body = copy.deepcopy(self.body)
        body["error"]["innererror"]["content_filter_result"]["custom_blocklists"] = [
            {"filtered": False, "id": "workshop-synthetic-marker"},
        ]
        self.assertFalse(native_intervention(400, body))

    def test_native_agent_observed_intervention(self):
        body = {
            "code": "content_filter", "innererror": {"code": "ContentFiltered"},
            "content_filters": [{"blocked": True, "source_type": "prompt",
                "content_filter_results": {"custom_blocklists": [{"filtered": True, "id": "workshop-synthetic-marker"}]}}],
        }
        self.assertTrue(agent_intervention(400, body))
        body["content_filters"][0]["blocked"] = False
        self.assertFalse(agent_intervention(400, body))

    def test_model_gate_is_not_agent_gate(self):
        self.assertFalse(agent_intervention(400, self.body))

    def test_filter_error_retries_only_the_observed_service_condition(self):
        unavailable = Mock(status_code=200)
        unavailable.json.return_value = {"choices": [{
            "content_filter_result": {"error": {"code": "content_filter_error"}},
        }]}
        blocked = Mock(status_code=400)
        blocked.json.return_value = self.body
        session = Mock()
        session.post.side_effect = [unavailable, blocked]
        with patch("demo.time.monotonic", side_effect=[0, 1]), patch("demo.time.sleep") as sleep:
            response, body = request_with_filter_readiness(session, "https://synthetic.invalid", {}, "test", 30)
        self.assertIs(response, blocked)
        self.assertTrue(native_intervention(response.status_code, body))
        sleep.assert_called_once_with(25)
        self.assertEqual(session.post.call_count, 2)

        session.post.side_effect = [unavailable]
        with patch("demo.time.monotonic", side_effect=[0, 31]), patch("demo.time.sleep") as sleep:
            with self.assertRaisesRegex(RuntimeError, "filtering was not verified"):
                request_with_filter_readiness(session, "https://synthetic.invalid", {}, "test", 30)
            sleep.assert_not_called()

        unfiltered = Mock(status_code=200)
        unfiltered.json.return_value = {"choices": [{"content_filter_results": {}}]}
        session.post.side_effect = [unfiltered]
        with patch("demo.time.monotonic", return_value=0), patch("demo.time.sleep") as sleep:
            response, body = request_with_filter_readiness(session, "https://synthetic.invalid", {}, "test")
        self.assertFalse(native_intervention(response.status_code, body))
        sleep.assert_not_called()

    def test_agent_wrong_blocklist_is_not_evidence(self):
        body = {"code": "content_filter", "innererror": {"code": "ContentFiltered"},
                "content_filters": [{"blocked": True, "source_type": "prompt",
                "content_filter_results": {"custom_blocklists": [{"filtered": True, "id": "other"}]}}]}
        self.assertFalse(agent_intervention(400, body))

    def test_restricted_probe_waits_for_actual_blocklist_intervention(self):
        response = Mock(status_code=200)
        unblocked = {"choices": [{"finish_reason": "stop", "message": {"content": "hello"}}]}
        blocked = Mock(status_code=400)
        with patch("demo.request_with_filter_readiness",
                   side_effect=[(response, unblocked), (blocked, self.body)]) as send, \
             patch("demo.time.monotonic", return_value=0), patch("demo.time.sleep") as sleep:
            result, body, attempts = request_restricted_with_propagation(
                Mock(), "https://synthetic.invalid", {}, "test", 30,
            )
        self.assertIs(result, blocked)
        self.assertTrue(native_intervention(result.status_code, body))
        self.assertEqual(attempts, [{"status": 200, "intervention": False},
                                    {"status": 400, "intervention": True}])
        self.assertEqual(send.call_count, 2)
        sleep.assert_called_once_with(25)

    def test_restricted_probe_does_not_accept_persistent_or_unrelated_failure(self):
        unblocked = Mock(status_code=200)
        body = {"choices": [{"finish_reason": "stop", "message": {"content": "hello"}}]}
        with patch("demo.request_with_filter_readiness", return_value=(unblocked, body)) as send, \
             patch("demo.time.monotonic", side_effect=[0, 0, 31]), patch("demo.time.sleep") as sleep:
            response, returned, attempts = request_restricted_with_propagation(
                Mock(), "https://synthetic.invalid", {}, "test", 30,
            )
        self.assertFalse(native_intervention(response.status_code, returned))
        self.assertEqual(len(attempts), 1)
        self.assertEqual(send.call_count, 1)
        sleep.assert_not_called()
        error = Mock(status_code=403)
        with patch("demo.request_with_filter_readiness", return_value=(error, {})) as send, \
             patch("demo.time.monotonic", return_value=0), patch("demo.time.sleep") as sleep:
            request_restricted_with_propagation(Mock(), "https://synthetic.invalid", {}, "test")
        self.assertEqual(send.call_count, 1)
        sleep.assert_not_called()

    def test_unguarded_control_requires_a_real_completed_reply(self):
        self.assertTrue(usable_unfiltered_response(
            200, {"choices": [{"finish_reason": "stop", "message": {"content": "hello"}}]}))
        self.assertFalse(usable_unfiltered_response(
            200, {"choices": [{"finish_reason": "stop", "message": {"content": None}}]}))
        self.assertFalse(usable_unfiltered_response(
            200, {"choices": [{"finish_reason": "content_filter", "message": {"content": "hello"}}]}))
        self.assertFalse(usable_unfiltered_response(200, {"choices": [{"message": None}]}))
        self.assertFalse(usable_unfiltered_response(200, {"choices": {}}))

    def test_installed_sdk_native_rai_config(self):
        from azure.ai.projects.models import RaiConfig, PromptAgentDefinition
        policy = "/subscriptions/test/providers/Microsoft.CognitiveServices/accounts/account/raiPolicies/policy"
        value = PromptAgentDefinition(model="agent-baseline", rai_config=RaiConfig(rai_policy_name=policy)).as_dict()
        self.assertEqual(value["rai_config"]["rai_policy_name"], policy)

    @patch("demo.Path.exists", return_value=True)
    @patch("demo.Path.read_text", return_value=json.dumps({
        "subscription": "sub", "resourceGroup": "rg", "outputs": {
            "endpoint": {"value": "https://root-runner.invalid"},
            "deployment": {"value": "root-model"}, "accountResourceId": {"value": "/root/account"},
        },
    }))
    @patch("demo.os.getenv", return_value="wrong-environment")
    def test_root_outputs_override_environment(self, environ, read, exists):
        self.assertEqual(settings(), {"endpoint": "https://root-runner.invalid", "deployment": "root-model", "account": "/root/account"})


if __name__ == "__main__":
    unittest.main()
