import io
import json
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from threading import Thread
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
from urllib.parse import parse_qs, urlsplit
import xml.etree.ElementTree as ET

import demo


class GatewayTests(unittest.TestCase):
    def test_prompts_use_a_verifiable_fact_for_the_semantic_cache_example(self):
        self.assertIn("2 + 2", demo.PROMPTS[0])
        self.assertIn("two plus two", demo.PROMPTS[1].lower())
        self.assertEqual(demo.PROMPTS[0], demo.PROMPTS[2])

    def test_wrong_arithmetic_answer_is_not_a_successful_cache_demo(self):
        client = Mock()
        client.chat.completions.create.return_value = self.response(content="5")
        with patch("demo.sleep"), patch("sys.stdout", new_callable=io.StringIO):
            with self.assertRaisesRegex(RuntimeError, "expected exactly 4"):
                demo.present(client, "chat")

    def response(self, response_id="chatcmpl-1", content="4", reason="stop"):
        return SimpleNamespace(id=response_id, choices=[
            SimpleNamespace(finish_reason=reason, message=SimpleNamespace(content=content)),
        ])

    def test_every_prompt_goes_to_gateway_even_when_response_is_reused(self):
        client = Mock()
        client.chat.completions.create.return_value = self.response()
        with patch("demo.sleep") as sleep, patch("sys.stdout", new_callable=io.StringIO) as output:
            demo.present(client, "chat")
        self.assertEqual(client.chat.completions.create.call_count, len(demo.PROMPTS))
        self.assertEqual(sleep.call_count, 2)
        self.assertEqual(output.getvalue().count("REUSED RESPONSE;"), 2)
        self.assertIn("repeated response IDs: 2", output.getvalue())
        client.embeddings.create.assert_not_called()
        for call, prompt in zip(client.chat.completions.create.call_args_list, demo.PROMPTS):
            self.assertEqual(call.kwargs, {
                "model": "chat", "messages": [{"role": "user", "content": prompt}],
                "max_completion_tokens": 120, "stream": False, "timeout": 60,
            })

    def test_identical_text_with_distinct_ids_does_not_claim_reuse(self):
        client = Mock()
        client.chat.completions.create.side_effect = [
            self.response(f"chatcmpl-{index}") for index in range(3)
        ]
        with patch("demo.sleep"), patch("sys.stdout", new_callable=io.StringIO) as output:
            with self.assertRaisesRegex(RuntimeError, "No response reuse"):
                demo.present(client, "chat")
        self.assertNotIn("REUSED RESPONSE;", output.getvalue())

    def test_rejects_incomplete_or_unobservable_answers(self):
        client = Mock()
        for response in (
            self.response(reason="length"), self.response(content=""),
            self.response(content=" "), self.response(response_id=""),
            SimpleNamespace(id="chatcmpl-1", choices=[]),
        ):
            with self.subTest(response=response), self.assertRaises(RuntimeError):
                client.chat.completions.create.return_value = response
                demo.answer(client, "chat", "Hello")

    def test_gateway_errors_are_not_hidden(self):
        client = Mock()
        client.chat.completions.create.side_effect = RuntimeError("Gateway unavailable")
        with self.assertRaisesRegex(RuntimeError, "Gateway unavailable"):
            demo.answer(client, "chat", "Hello")

    def test_real_sdk_routes_chat_to_apim_without_changing_request_shape(self):
        requests = []

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                requests.append((self.path, self.headers, body))
                response = json.dumps({
                    "id": "chatcmpl-cached", "object": "chat.completion",
                    "created": 1, "model": "chat",
                    "choices": [{"index": 0, "finish_reason": "stop",
                                 "message": {"role": "assistant", "content": "4"}}],
                }).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(response)))
                self.end_headers()
                self.wfile.write(response)

            def log_message(self, format, *args):
                pass

        with HTTPServer(("127.0.0.1", 0), Handler) as server:
            worker = Thread(target=server.serve_forever, daemon=True)
            worker.start()
            try:
                with demo.AzureOpenAI(
                    azure_endpoint=f"http://127.0.0.1:{server.server_port}/ai",
                    api_version="2024-10-21",
                    azure_ad_token_provider=lambda: "test-token",
                    default_headers={"x-workshop-cache-session": "workshop"},
                ) as client:
                    for prompt in demo.PROMPTS:
                        self.assertEqual(demo.answer(client, "chat", prompt),
                                         ("4", "chatcmpl-cached"))
            finally:
                server.shutdown()
                worker.join()
        self.assertEqual(len(requests), 3)
        for (path, headers, body), prompt in zip(requests, demo.PROMPTS):
            url = urlsplit(path)
            self.assertEqual(url.path, "/ai/openai/deployments/chat/chat/completions")
            self.assertEqual(parse_qs(url.query), {"api-version": ["2024-10-21"]})
            self.assertEqual(headers["Authorization"], "Bearer test-token")
            self.assertEqual(headers["x-workshop-cache-session"], "workshop")
            self.assertEqual(body["messages"], [{"role": "user", "content": prompt}])

    def test_client_uses_gateway_entra_auth_and_shared_session_header(self):
        with (
            patch("sys.argv", ["demo.py", "--endpoint", "https://gateway.azure-api.net/ai",
                              "--deployment", "chat", "--session", "workshop"]),
            patch("demo.AzureCliCredential"),
            patch("demo.get_bearer_token_provider", return_value="provider"),
            patch("demo.AzureOpenAI") as client,
            patch("demo.present") as present,
            patch("sys.stdout", new_callable=io.StringIO),
        ):
            demo.main()
        self.assertEqual(client.call_args.kwargs["azure_endpoint"], "https://gateway.azure-api.net/ai")
        self.assertEqual(client.call_args.kwargs["azure_ad_token_provider"], "provider")
        self.assertEqual(client.call_args.kwargs["default_headers"],
                         {"x-workshop-cache-session": "workshop"})
        present.assert_called_once_with(client.return_value.__enter__.return_value, "chat")


class PolicyTests(unittest.TestCase):
    def setUp(self):
        self.infra = Path(__file__).parent / "infra"
        self.policy = ET.fromstring((self.infra / "policy.xml").read_text())

    def test_authentication_precedes_lookup_and_backend_auth_follows_it(self):
        inbound = self.policy.find("inbound")
        tags = [element.tag for element in inbound]
        self.assertLess(tags.index("validate-azure-ad-token"), tags.index("llm-semantic-cache-lookup"))
        self.assertLess(tags.index("llm-semantic-cache-lookup"),
                        tags.index("authentication-managed-identity"))
        self.assertEqual(inbound.find(".//claim").attrib["name"], "oid")
        self.assertEqual(inbound.find(".//claim/value").text, "__PRINCIPAL_ID__")
        self.assertEqual(inbound.find(".//audience").text, "https://cognitiveservices.azure.com")

    def test_gateway_owns_semantic_lookup_store_and_partitioning(self):
        lookup = self.policy.find("inbound/llm-semantic-cache-lookup")
        self.assertEqual(lookup.attrib["embeddings-backend-id"], "workshop-embeddings")
        self.assertEqual(lookup.attrib["embeddings-backend-auth"], "system-assigned")
        self.assertEqual(lookup.attrib["ignore-system-messages"], "false")
        self.assertEqual(lookup.attrib["max-message-count"], "1")
        self.assertIn("__PRINCIPAL_ID__", lookup.find("vary-by").text)
        self.assertIn("__CHAT_DEPLOYMENT__", lookup.find("vary-by").text)
        self.assertIn("x-workshop-cache-session", lookup.findall("vary-by")[1].text)
        store = self.policy.find("outbound/choose/when/llm-semantic-cache-store")
        self.assertEqual(store.attrib, {"duration": "__CACHE_DURATION__"})
        condition = self.policy.find("outbound/choose/when").attrib["condition"]
        self.assertIn('context.Response.StatusCode == 200', condition)
        self.assertIn('["finish_reason"] == "stop"', condition)
        self.assertIn("!String.IsNullOrWhiteSpace", condition)
        self.assertIn("preserveContent: true", condition)

    def test_embeddings_use_global_standard_for_sweden_central_support(self):
        source = (self.infra / "main.bicep").read_text()
        embeddings = source.split("resource embeddings ", 1)[1].split("\nvar userRoleId", 1)[0]
        self.assertIn("name: 'GlobalStandard'", embeddings)
        self.assertNotIn("name: 'Standard'", embeddings)
        self.assertIn("name: 'text-embedding-3-small'", embeddings)
        self.assertIn("version: '1'", embeddings)

    def test_external_cache_resource_id_is_an_absolute_management_url(self):
        source = (self.infra / "main.bicep").read_text()
        cache = source.split("resource externalCache ", 1)[1].split(
            "\nresource embeddingsBackend ", 1,
        )[0]
        self.assertIn("resourceId: uri(environment().resourceManager, redis.id)", cache)
        self.assertNotIn("resourceId: redis.id", cache)

    def test_external_cache_identifier_matches_default_gateway_location(self):
        source = (self.infra / "main.bicep").read_text()
        cache = source.split("resource externalCache ", 1)[1].split(
            "\nresource embeddingsBackend ", 1,
        )[0]
        self.assertIn("name: 'default'", cache)
        self.assertIn("useFromLocation: 'default'", cache)

    def test_lowest_cost_skus_and_required_redis_configuration(self):
        source = (self.infra / "main.bicep").read_text()
        for setting in (
            "name: 'Consumption'", "capacity: 0",
            "param redisSku string = 'Balanced_B0'", "name: redisSku",
            "highAvailability: 'Disabled'", "name: 'RediSearch'",
            "clusteringPolicy: 'EnterpriseCluster'", "evictionPolicy: 'NoEviction'",
            "accessKeysAuthentication: 'Enabled'", "clientProtocol: 'Encrypted'",
            "ssl=True", "disableLocalAuth: true", "subscriptionRequired: false",
            "param scoreThreshold string = '0.15'", "param cacheDuration int = 300",
            "output endpoint string = '${gateway.properties.gatewayUrl}/ai'",
        ):
            with self.subTest(setting=setting):
                self.assertIn(setting, source)
        for placeholder in ("__TENANT_ID__", "__PRINCIPAL_ID__", "__SCORE_THRESHOLD__",
                            "__CACHE_DURATION__", "__CHAT_DEPLOYMENT__"):
            self.assertIn(placeholder, source)


if __name__ == "__main__":
    unittest.main()
