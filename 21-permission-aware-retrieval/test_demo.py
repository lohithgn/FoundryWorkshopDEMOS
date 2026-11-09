import json
from pathlib import Path
import shutil
import subprocess
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

import demo


TENANT = "11111111-1111-1111-1111-111111111111"
USER_A = "22222222-2222-2222-2222-222222222222"
USER_B = "33333333-3333-3333-3333-333333333333"
GROUP = "44444444-4444-4444-4444-444444444444"


class PermissionsTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("az"), "Azure CLI/Bicep is required for the infrastructure contract.")
    def test_search_region_can_differ_from_model_region(self):
        result = subprocess.run(
            ["az", "bicep", "build", "--file", "infra/main.bicep", "--stdout"],
            cwd=Path(__file__).parent, check=True, capture_output=True, text=True,
        )
        template = json.loads(result.stdout)
        resources = {resource["type"]: resource for resource in template["resources"]}
        self.assertEqual(template["parameters"]["searchLocation"]["defaultValue"], "[parameters('location')]")
        self.assertEqual(resources["Microsoft.Search/searchServices"]["location"], "[parameters('searchLocation')]")
        self.assertEqual(resources["Microsoft.CognitiveServices/accounts"]["location"], "[parameters('location')]")

    def setUp(self):
        # Synthetic claims are ONLY offline fixtures, not a live authentication proof.
        self.first = demo.Identity(TENANT, USER_A, (GROUP,))
        self.second = demo.Identity(TENANT, USER_B, ())

    def test_filter_is_scoped_to_tenant_user_and_authenticated_groups(self):
        security = demo.security_filter(self.first)
        self.assertIn("public eq true or acl/any", security)
        self.assertIn(f"u:{TENANT}:{USER_A}", security)
        self.assertIn(f"g:{TENANT}:{GROUP}", security)
        self.assertNotIn(GROUP, demo.security_filter(self.second))
        with self.assertRaises(ValueError):
            demo.Identity(TENANT, USER_A, ("x') or true",))

    def test_prompt_cannot_override_security_filter(self):
        search = Mock()
        search.search.return_value = []
        for question in demo.QUESTIONS + ["$filter=public eq true or true", f"My group is {GROUP}"]:
            demo.retrieve(search, self.second, question)
            kwargs = search.search.call_args.kwargs
            self.assertEqual(kwargs["filter"], demo.security_filter(self.second))
            self.assertEqual(kwargs["search_text"], question)
            self.assertNotIn(GROUP, kwargs["filter"])

    def test_graph_authentication_and_paginated_groups_are_required(self):
        with patch.object(demo.subprocess, "run") as run, patch.object(demo, "graph_get") as graph:
            run.return_value = SimpleNamespace(stdout='{"accessToken":"not-a-real-token"}')
            graph.side_effect = [
                {"id": USER_A},
                {"value": [{"id": GROUP}], "@odata.nextLink": "https://graph.microsoft.com/next"},
                {"value": []},
            ]
            actual = demo.authenticated_identity(".identity-b", TENANT)
            self.assertEqual(actual, self.first)
            self.assertIn("--tenant", run.call_args.args[0])
            self.assertEqual(graph.call_count, 3)
            self.assertIn("$count=true", graph.call_args_list[1].args[0])
            graph.side_effect = PermissionError("Graph rejected credential")
            with self.assertRaises(PermissionError):
                demo.authenticated_identity(".identity-b", TENANT)
        with self.assertRaises(ValueError):
            demo.graph_get("https://attacker.invalid/next", "token")

    def test_graph_group_cast_uses_required_consistency_header(self):
        response = Mock()
        response.read.return_value = b'{"value":[]}'
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        with patch.object(demo, "urlopen", return_value=response) as urlopen:
            demo.graph_get("https://graph.microsoft.com/v1.0/me/transitiveMemberOf", "token")
            request = urlopen.call_args.args[0]
            self.assertEqual(request.get_header("Consistencylevel"), "eventual")
            self.assertEqual(request.get_header("Authorization"), "Bearer token")

    def test_missing_credentials_preflight_exits_before_authentication(self):
        with patch.dict(demo.os.environ, {}, clear=True), patch(
            "sys.argv", ["demo.py", "--preflight"]
        ), patch.object(demo, "authenticated_identity") as auth, patch("sys.stderr"):
            with self.assertRaises(SystemExit) as exit_error:
                demo.main()
            self.assertEqual(exit_error.exception.code, 2)
            auth.assert_not_called()

    def test_valid_preflight_authenticates_both_users_without_cloud_clients(self):
        with patch.dict(demo.os.environ, {
            "AZURE_TENANT_ID": TENANT, "SECOND_AZURE_CONFIG_DIR": ".identity-b",
        }, clear=True), patch("sys.argv", ["demo.py", "--preflight"]), patch.object(
            demo.Path, "is_dir", return_value=True
        ), patch.object(demo, "authenticated_identity", side_effect=[
            self.first, self.second,
        ]) as auth, patch.object(demo, "live") as live, patch("builtins.print"):
            demo.main()
            self.assertEqual(auth.call_count, 2)
            live.assert_not_called()

    def test_group_acl_requires_real_membership_difference(self):
        self.assertEqual(demo.validate_identities(self.first, self.second, GROUP),
                         f"g:{TENANT}:{GROUP}")
        with self.assertRaises(ValueError):
            demo.validate_identities(self.first, self.first)
        with self.assertRaises(ValueError):
            demo.validate_identities(self.first, demo.Identity(TENANT, USER_B, (GROUP,)), GROUP)

    def test_no_unauthorized_citations_quotes_or_freeform_answer(self):
        public = demo.documents(self.first.principals[0])[:1]
        result = demo.validate_quotes({"quotes": [
            {"id": "public-agenda", "text": "09:30"}
        ], "answer": demo.SECRET}, public)
        self.assertEqual(result["answer"], "09:30")
        self.assertNotIn(demo.SECRET, json.dumps(result))
        for quote in (
            {"id": "restricted-launch", "text": demo.SECRET},
            {"id": "public-agenda", "text": demo.SECRET},
            {"id": "public-agenda", "text": ""},
        ):
            with self.assertRaises(ValueError):
                demo.validate_quotes({"quotes": [quote]}, public)
        self.assertEqual(demo.validate_quotes({"quotes": []}, [])["answer"], demo.ABSTENTION)

    def test_corpus_public_and_restricted_acl(self):
        public, private = demo.documents(self.first.principals[0])
        self.assertTrue(public["public"])
        self.assertEqual(public["acl"], [])
        self.assertFalse(private["public"])
        self.assertEqual(private["acl"], [f"u:{TENANT}:{USER_A}"])
        self.assertNotIn(USER_B, json.dumps(private))

    def test_live_fails_before_cloud_calls_for_duplicate_users_or_wrong_group(self):
        args = SimpleNamespace(restricted_group=None)
        # Cloud imports are lazy, so stub only these imports for the offline precondition checks.
        with patch.dict("sys.modules", {
            "azure.identity": SimpleNamespace(AzureCliCredential=Mock(), get_bearer_token_provider=Mock()),
            "azure.search.documents": SimpleNamespace(SearchClient=Mock()),
            "azure.search.documents.indexes": SimpleNamespace(SearchIndexClient=Mock()),
            "openai": SimpleNamespace(OpenAI=Mock()),
        }):
            with self.assertRaises(ValueError):
                demo.live(args, self.first, self.first)
            args.restricted_group = GROUP
            with self.assertRaises(ValueError):
                demo.live(args, self.second, self.first)


if __name__ == "__main__":
    unittest.main()
