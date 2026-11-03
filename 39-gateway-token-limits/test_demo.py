import unittest
import json
import os
from pathlib import Path
from unittest.mock import Mock, patch
import xml.etree.ElementTree as ET

from demo import gateway_endpoint, intervention, model_success, parse_policy, run, apply_counter_run


class GatewayEvidenceTests(unittest.TestCase):
    def test_rate_native_intervention(self):
        self.assertTrue(intervention("rate", 429, {"X-Workshop-Policy": "workshop-token-limit", "X-Workshop-Reason": "OpenAITokenLimitExceeded"}))

    def test_quota_native_intervention(self):
        self.assertTrue(intervention("quota", 403, {"X-Workshop-Policy": "workshop-token-limit", "X-Workshop-Reason": "OpenAITokenQuotaExceeded"}))

    def test_backend_429_is_not_gateway_evidence(self):
        self.assertFalse(intervention("rate", 429, {"Retry-After": "60"}))

    def test_permission_403_is_not_quota_evidence(self):
        self.assertFalse(intervention("quota", 403, {}))

    def test_status_must_match_policy(self):
        self.assertFalse(intervention("quota", 429, {"X-Workshop-Policy": "workshop-token-limit", "X-Workshop-Reason": "RateLimitExceeded"}))

    def test_reason_must_be_native_token_limit(self):
        self.assertFalse(intervention("quota", 403, {"X-Workshop-Policy": "workshop-token-limit", "X-Workshop-Reason": "Unauthorized"}))

    def test_actual_backend_usage_required(self):
        self.assertFalse(model_success(200, {"choices": [{"message": {"content": "hello"}}]}))

    def test_allowed_model_response(self):
        self.assertTrue(model_success(200, {"usage": {"total_tokens": 18}, "choices": [{"message": {"content": "hello"}}]}))

    def test_empty_choices_cannot_pass(self):
        self.assertFalse(model_success(200, {"usage": {"total_tokens": 18}, "choices": []}))

    def test_observed_arm_xml_bom(self):
        self.assertEqual(parse_policy("\ufeff<policies><inbound /></policies>").tag, "policies")

    def test_documented_arm_json_policy(self):
        self.assertEqual(parse_policy('{"properties":{"value":"<policies />"}}').tag, "policies")

    def test_counter_rotation_preserves_real_limits_and_is_idempotent(self):
        for attrs in ('tokens-per-minute="100"', 'token-quota="100" token-quota-period="Hourly"'):
            policy = ET.fromstring(f'<policies><inbound><llm-token-limit {attrs} counter-key="old" /></inbound><outbound/><on-error/></policies>')
            limits = dict(policy.find("inbound/llm-token-limit").attrib)
            apply_counter_run(policy, "/project-a-rate", "one")
            apply_counter_run(policy, "/project-a-rate", "two")
            result = policy.find("inbound/llm-token-limit").attrib
            for key, value in limits.items():
                if key != "counter-key":
                    self.assertEqual(result[key], value)
            self.assertEqual(result["counter-key"], "/project-a-rate-run-two")
            for section in ("outbound", "on-error"):
                nodes = policy.findall(section + "/set-header")
                self.assertEqual(len(nodes), 1)
                self.assertEqual(nodes[0].find("value").text, "two")

    def test_real_rate_policy_template(self):
        template = (Path(__file__).parent / "infra" / "gateway-policy.xml").read_text()
        policy = ET.fromstring(template.replace("__LIMIT__", 'tokens-per-minute="100" remaining-tokens-header-name="X-Rate-Remaining"'))
        limit = policy.find("inbound/llm-token-limit")
        self.assertEqual(limit.attrib["tokens-per-minute"], "100")
        self.assertEqual(limit.attrib["id"], "workshop-token-limit")
        self.assertEqual(limit.attrib["estimate-prompt-tokens"], "false")
        self.assertNotIn("remaining-quota-tokens-header-name", limit.attrib)
        self.assertIsNotNone(policy.find("inbound/authentication-managed-identity"))
        self.assertIsNotNone(policy.find("inbound/validate-azure-ad-token"))

    def test_real_hourly_policy_template(self):
        template = (Path(__file__).parent / "infra" / "gateway-policy.xml").read_text()
        policy = ET.fromstring(template.replace("__LIMIT__", 'token-quota="100" token-quota-period="Hourly" remaining-quota-tokens-header-name="X-Quota-Remaining"'))
        limit = policy.find("inbound/llm-token-limit")
        self.assertEqual(limit.attrib["token-quota"], "100")
        self.assertEqual(limit.attrib["token-quota-period"], "Hourly")
        self.assertNotIn("remaining-tokens-header-name", limit.attrib)
        headers = [node.attrib["name"] for node in policy.findall("on-error/set-header")]
        self.assertIn("X-Workshop-Policy", headers)
        self.assertIn("X-Workshop-Reason", headers)

    @patch("demo.load_outputs", return_value={})
    @patch("demo.os.getenv", return_value="http://insecure.invalid")
    def test_https_required(self, _, outputs):
        with self.assertRaises(RuntimeError):
            gateway_endpoint()

    @patch("demo.load_outputs", return_value={"gatewayEndpoint": {"value": "https://metadata.invalid"}})
    @patch("demo.os.getenv", return_value="https://wrong-environment.invalid")
    def test_metadata_overrides_environment(self, _, outputs):
        self.assertEqual(gateway_endpoint(), "https://metadata.invalid")

    @patch("demo.Path.exists", return_value=True)
    @patch("demo.Path.read_text", return_value=json.dumps({
        "subscription": "sub", "resourceGroup": "rg",
        "outputs": {"gatewayEndpoint": {"value": "https://root-runner.invalid"}},
    }))
    def test_root_runner_output_shape(self, read, exists):
        from demo import load_outputs
        self.assertEqual(load_outputs()["gatewayEndpoint"]["value"], "https://root-runner.invalid")

    @patch("demo.rotate_policy_counters", return_value="offline-run")
    @patch("demo.time.sleep")
    @patch("demo.print")
    @patch("demo.verify_policy_configuration", return_value=[])
    @patch("demo.gateway_endpoint", return_value="https://gateway.invalid")
    @patch("demo.AzureCliCredential")
    @patch("demo.requests.Session")
    def test_eight_bounded_service_requests(self, session_class, credential, endpoint, policy_check, printer, sleep, rotate):
        allowed = {"usage": {"total_tokens": 20}, "choices": [{"message": {"content": "hello"}}]}
        statuses = [200, 200, 429, 200, 200, 200, 403, 200]
        responses = []
        for status in statuses:
            response = Mock(status_code=status)
            response.headers = {
                "X-Workshop-Policy": "workshop-token-limit",
                "X-Workshop-Reason": "OpenAITokenLimitExceeded" if status == 429 else "OpenAITokenQuotaExceeded",
            } if status != 200 else {}
            response.headers["X-Workshop-Counter-Run"] = "offline-run"
            response.json.return_value = allowed if status == 200 else {"message": "Service limit"}
            responses.append(response)
        credential.return_value.get_token.return_value.token = "offline-fixture"
        session_class.return_value.post.side_effect = responses
        self.assertEqual(run()["status"], "PASS")
        self.assertEqual(session_class.return_value.post.call_count, 8)
        for call in session_class.return_value.post.call_args_list:
            self.assertEqual(call.kwargs["json"]["max_tokens"], 16)
        self.assertIn("project-b-quota", session_class.return_value.post.call_args_list[-1].args[0])
        self.assertEqual(sleep.call_count, 7)

    @patch("demo.rotate_policy_counters", return_value="offline-run")
    @patch("demo.time.sleep")
    @patch("demo.verify_policy_configuration", return_value=[])
    @patch("demo.gateway_endpoint", return_value="https://gateway.invalid")
    @patch("demo.AzureCliCredential")
    @patch("demo.requests.Session")
    def test_startup_404_retries_are_bounded_and_do_not_pass(self, session, credential, endpoint, policy, sleep, rotate):
        credential.return_value.get_token.return_value.token = "offline-fixture"
        response = Mock(status_code=404, headers={})
        response.json.return_value = {"error": {"code": "404", "message": "Resource not found"}}
        session.return_value.post.return_value = response
        with self.assertRaisesRegex(RuntimeError, "Gateway has not adopted"):
            run()
        self.assertEqual(session.return_value.post.call_count, 3)
        self.assertEqual(sleep.call_count, 2)
        sleep.assert_called_with(15)


if __name__ == "__main__":
    unittest.main()
