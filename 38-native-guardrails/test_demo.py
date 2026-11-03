import copy
import json
import unittest
from unittest.mock import patch

from demo import native_intervention, agent_intervention, settings


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

    def test_agent_wrong_blocklist_is_not_evidence(self):
        body = {"code": "content_filter", "innererror": {"code": "ContentFiltered"},
                "content_filters": [{"blocked": True, "source_type": "prompt",
                "content_filter_results": {"custom_blocklists": [{"filtered": True, "id": "other"}]}}]}
        self.assertFalse(agent_intervention(400, body))

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
