import io
import json
import os
from pathlib import Path
import re
import runpy
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from unittest.mock import patch

import deploy


class DeployTests(unittest.TestCase):
    def test_required_security_tag_values(self) -> None:
        self.assertEqual(deploy.SECURITY_TAGS, {
            "SecurityControl": "Ignore",
            "SecurityExemption": "StorageAccountKeyAccess",
        })

    def test_every_taggable_infrastructure_resource_has_security_tags(self) -> None:
        unsupported = {
            "Microsoft.Authorization/roleAssignments",
            "Microsoft.Authorization/policyAssignments",
            "Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials",
            "Microsoft.Web/sites/functions",
            "Microsoft.Storage/storageAccounts/blobServices",
            "Microsoft.Storage/storageAccounts/blobServices/containers",
            "Microsoft.Storage/storageAccounts/managementPolicies",
            "Microsoft.Cache/redisEnterprise/databases",
            "Microsoft.ApiManagement/service/caches",
            "Microsoft.ApiManagement/service/backends",
            "Microsoft.ApiManagement/service/apis",
            "Microsoft.ApiManagement/service/apis/policies",
            "Microsoft.ApiManagement/service/apis/operations",
            "Microsoft.ApiManagement/service/apis/operations/policies",
            "Microsoft.CognitiveServices/accounts/projects/connections",
        }
        templates = sorted(Path(__file__).parent.glob("*/infra/*.bicep"))
        self.assertTrue(templates)
        for template in templates:
            with self.subTest(template=str(template)):
                source = template.read_text()
                tags = re.search(r"^var securityTags = \{([^}]+)\}", source, re.MULTILINE)
                self.assertIsNotNone(tags)
                self.assertEqual(
                    dict(re.findall(r"(\w+): '([^']+)'", tags.group(1))),
                    deploy.SECURITY_TAGS,
                )
                resources = list(re.finditer(
                    r"^resource \w+ '([^'@]+)@[^']+'( existing)? = [^\n]+\n",
                    source, re.MULTILINE,
                ))
                self.assertTrue(resources)
                for resource in resources:
                    tagged = source[resource.end():].startswith("  tags: securityTags\n")
                    self.assertEqual(
                        tagged,
                        not resource.group(2) and resource.group(1) not in unsupported,
                        resource.group(),
                    )

    def test_manual_resource_group_commands_have_security_tags(self) -> None:
        for demo in deploy.discover(Path(__file__).parent):
            source = (demo / "README.md").read_text()
            commands = re.findall(r"^az group create[^\n]*", source, re.MULTILINE)
            self.assertTrue(commands, demo.name)
            for command in commands:
                with self.subTest(demo=demo.name, command=command):
                    self.assertIn("--tags", command)
                    for key, value in deploy.SECURITY_TAGS.items():
                        self.assertIn(f"{key}={value}", command)

    def test_standalone_runners_add_security_tags_to_existing_owned_groups(self) -> None:
        scripts = sorted(Path(__file__).parent.glob("*/deploy_lab.py"))
        self.assertTrue(scripts)
        for script in scripts:
            with self.subTest(lab=script.parent.name), tempfile.TemporaryDirectory() as directory:
                with patch.object(sys, "path", sys.path.copy()):
                    namespace = runpy.run_path(str(script))
                folder = Path(directory) / script.parent.name
                (folder / "infra").mkdir(parents=True)
                for required in ("infra/main.bicep", "requirements.txt", "test_demo.py", "demo.py"):
                    (folder / required).touch()
                state = folder / ".azure" / "lab-deployment.json"
                state.parent.mkdir()
                name = re.sub(r"^\d+-", "", folder.name)
                group = f"rg-foundry-student-{name}-demo"
                previous = {"subscription": "sub", "resourceGroup": group}
                if script.parent.name == "13-agent-orchestrations":
                    previous["accountName"] = "foundry-patterns-" + "0" * 32
                state.write_text(json.dumps(previous))
                commands = []
                owner = namespace["OWNER"]

                def fake_run(command, **kwargs):
                    commands.append(command)
                    if command[:3] == ["az", "account", "show"]:
                        return json.dumps({
                            "id": "sub", "name": "test", "tenantId": "tenant",
                            "user": {"type": "user"},
                        })
                    if command[:3] == ["az", "group", "exists"]:
                        return "true"
                    if command[:3] == ["az", "group", "show"]:
                        return json.dumps(owner)
                    if command[:3] == ["az", "bicep", "build"]:
                        return '{"resources": []}'
                    if command[:4] == ["az", "deployment", "group", "create"]:
                        return '{"endpoint": {"value": "https://example/"}}'
                    return ""

                with (
                    patch.dict(namespace["main"].__globals__, {
                        "LAB": folder, "STATE": state,
                        "identity_settings": lambda: ("tenant", "agent"),
                    }),
                    patch("deploy.run", side_effect=fake_run),
                    patch("deploy.run_live"),
                    patch("deploy.subprocess.run", return_value=subprocess.CompletedProcess(
                        [], 0, stdout="Tests passed\n",
                    )),
                    redirect_stdout(io.StringIO()),
                ):
                    namespace["main"](["--principal-id", "presenter"])
                command = next(command for command in commands
                               if command[:3] == ["az", "group", "create"])
                tags = dict(item.split("=", 1) for item in
                            command[command.index("--tags") + 1:command.index("--subscription")])
                self.assertEqual(tags, {**owner, **deploy.SECURITY_TAGS})

    def test_discovers_all_demo_templates_and_requires_entrypoints(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaisesRegex(ValueError, "No demo templates"):
                deploy.discover(root)
            for name in ("second", "first"):
                demo = root / name
                (demo / "infra").mkdir(parents=True)
                (demo / "infra/main.bicep").touch()
                for entrypoint in ("demo.py", "requirements.txt", "test_demo.py"):
                    (demo / entrypoint).touch()
            self.assertEqual([p.name for p in deploy.discover(root)], ["first", "second"])
            (root / "first/demo.py").unlink()
            with self.assertRaisesRegex(ValueError, "first is missing demo.py"):
                deploy.discover(root)

    def test_incomplete_demo_is_not_silently_skipped(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "incomplete").mkdir()
            (root / "incomplete/requirements.txt").touch()
            with self.assertRaisesRegex(ValueError, "incomplete is missing infra/main.bicep"):
                deploy.discover(root)

    def test_outputs_override_stale_environment(self) -> None:
        result = deploy.demo_environment(
            {"endpoint": {"value": "https://new.example/"}, "deployment": {"value": "new"}},
            {"AZURE_OPENAI_ENDPOINT": "stale", "PATH": "/bin"},
        )
        self.assertEqual(result["AZURE_OPENAI_ENDPOINT"], "https://new.example/")
        self.assertEqual(result["AZURE_OPENAI_DEPLOYMENT"], "new")
        self.assertEqual(result["PATH"], "/bin")
        for outputs in ({}, {"endpoint": {"value": ""}}):
            with self.assertRaises(ValueError):
                deploy.demo_environment(outputs, {})

    def test_project_and_tool_outputs_override_stale_environment(self) -> None:
        outputs = {
            "endpoint": {"value": "https://account.openai.azure.com/"},
            "deployment": {"value": "model"},
            "projectEndpoint": {"value": "https://account.services.ai.azure.com/api/projects/demo"},
            "toolEndpoint": {"value": "https://function.azurewebsites.net/api"},
        }
        result = deploy.demo_environment(outputs, {
            "FOUNDRY_PROJECT_ENDPOINT": "stale", "WORKSHOP_TOOL_ENDPOINT": "stale",
        })
        self.assertEqual(result["FOUNDRY_PROJECT_ENDPOINT"], outputs["projectEndpoint"]["value"])
        self.assertEqual(result["WORKSHOP_TOOL_ENDPOINT"], outputs["toolEndpoint"]["value"])
        for name in ("projectEndpoint", "toolEndpoint"):
            invalid = {**outputs, name: {"value": ""}}
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, name):
                deploy.demo_environment(invalid, {})

    def test_all_listed_demos_have_runner_entrypoints(self) -> None:
        self.assertEqual(
            [demo.name for demo in deploy.discover(Path(__file__).parent)],
            ["1-model-router", "2-workflow-agent", "3-webiq-agent", "4-agent-tools",
             "5-decision-model", "6-content-safety", "7-semantic-cache",
             "8-agent-evaluation", "9-agent-red-teaming", "10-fine-tuning",
             "11-foundry-iq", "12-entra-agent-id", "13-agent-orchestrations",
             "14-foundry-fundamentals", "15-model-benchmark", "16-structured-outputs",
             "17-rag-fundamentals", "18-agent-tracing", "19-conversation-state",
             "20-human-approval", "21-permission-aware-retrieval", "22-prompt-injection",
             "23-evaluation-cicd", "24-agent-lifecycle", "25-resilience-cost",
             "26-multimodal", "27-FoundryIQwithKB", "28-agent-skills",
             "29-toolbox-skill-discovery", "30-skill-data-analyst", "31-native-agent-memory",
             "32-browser-runbook", "33-skill-release-governance", "34-document-intelligence",
             "35-work-iq", "36-hosted-agent", "37-production-monitoring",
             "38-native-guardrails", "39-gateway-token-limits", "40-voice-agent",
             "41-content-understanding", "42-managed-harness-tool-search",
             "43-agent-routines", "44-evaluation-optimizer", "45-governed-iq",
             "46-fleet-governance"],
        )

    def test_storage_and_search_resource_outputs_override_stale_environment(self) -> None:
        names = {
            "searchResourceId": "AZURE_SEARCH_RESOURCE_ID",
            "storageEndpoint": "AZURE_STORAGE_BLOB_ENDPOINT",
            "storageResourceId": "AZURE_STORAGE_RESOURCE_ID",
        }
        outputs = {"endpoint": {"value": "https://foundry.openai.azure.com/"},
                   **{key: {"value": f"value-{key}"} for key in names}}
        result = deploy.demo_environment(outputs, {variable: "stale" for variable in names.values()})
        for key, variable in names.items():
            self.assertEqual(result[variable], outputs[key]["value"])
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, key):
                deploy.demo_environment({**outputs, key: {"value": ""}}, {})

    def test_selects_number_or_folder_in_discovery_order(self) -> None:
        demos = [Path("1-first"), Path("12-identity"), Path("14-fundamentals")]
        self.assertEqual(deploy.select_demos(demos, None), demos)
        self.assertEqual(deploy.select_demos(demos, ["14", "1-first", "14"]),
                         [demos[0], demos[2]])
        with self.assertRaisesRegex(ValueError, "exactly one demo"):
            deploy.select_demos(demos, ["missing"])

    def test_project_tenant_output_overrides_stale_environment(self) -> None:
        outputs = {
            "endpoint": {"value": "https://foundry.openai.azure.com/"},
            "tenantId": {"value": "project-tenant"},
        }
        result = deploy.demo_environment(outputs, {"AZURE_TENANT_ID": "stale-tenant"})
        self.assertEqual(result["AZURE_TENANT_ID"], "project-tenant")
        with self.assertRaisesRegex(ValueError, "tenantId"):
            deploy.demo_environment({**outputs, "tenantId": {"value": ""}}, {})

    def test_every_proposal_has_documentation_and_a_backlog_feature(self) -> None:
        root = Path(__file__).parent
        proposals = [
            int(number) for number in re.findall(r"^(\d+)\. ", (root / "demos.md").read_text(),
                                                re.MULTILINE)
        ]
        features = [
            int(number) for number in re.findall(r"^## F(\d{3}) - ",
                                                (root / "BACKLOG.md").read_text(), re.MULTILINE)
        ]
        self.assertEqual(proposals, list(range(1, 47)))
        self.assertEqual(features, proposals)
        for demo in deploy.discover(root):
            with self.subTest(demo=demo.name):
                self.assertTrue((demo / "README.md").is_file())

    def test_selected_demo_does_not_require_unselected_identity_setup(self) -> None:
        with (
            patch("deploy.discover", return_value=[Path("12-entra-agent-id"), Path("20-human-approval")]),
            patch("deploy.run", side_effect=RuntimeError("reached account lookup")) as run,
            patch.dict(os.environ, {}, clear=True),
        ):
            with self.assertRaisesRegex(RuntimeError, "reached account lookup"):
                deploy.main(["--demo", "20"])
            self.assertEqual(run.call_args.args[0][:3], ["az", "account", "show"])

    def test_unknown_demo_fails_before_azure_changes(self) -> None:
        with (
            patch("deploy.discover", return_value=[Path("1-first")]),
            patch("deploy.run") as run,
        ):
            with self.assertRaises(SystemExit):
                deploy.main(["--demo", "99"])
            run.assert_not_called()

    def test_search_location_is_passed_to_a_supporting_template(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            folder = root / "17-rag-fundamentals"
            folder.mkdir()
            commands = []

            def fake_run(command, **kwargs):
                commands.append(command)
                if command[:3] == ["az", "account", "show"]:
                    return json.dumps({"id": "sub", "name": "test", "user": {"type": "user"}})
                if command[:3] == ["az", "bicep", "build"]:
                    return '{"resources": [], "parameters": {"searchLocation": {"type": "string"}}}'
                if command[:4] == ["az", "deployment", "group", "create"]:
                    return '{"endpoint": {"value": "https://example/"}}'
                return ""

            with (
                patch("deploy.ROOT", root), patch("deploy.discover", return_value=[folder]),
                patch("deploy.run", side_effect=fake_run), patch("deploy.run_live"),
                redirect_stdout(io.StringIO()),
            ):
                deploy.main(["--demo", "17", "--principal-id", "presenter",
                             "--search-location", "westus2"])
            deployment = next(command for command in commands
                              if command[:4] == ["az", "deployment", "group", "create"])
            self.assertIn("searchLocation=westus2", deployment)
            group = next(command for command in commands
                         if command[:3] == ["az", "group", "create"])
            tags = dict(item.split("=", 1) for item in
                        group[group.index("--tags") + 1:group.index("--subscription")])
            self.assertEqual(tags, deploy.SECURITY_TAGS)

    def test_search_location_without_support_fails_before_resource_changes(self) -> None:
        commands = []

        def fake_run(command, **kwargs):
            commands.append(command)
            if command[:3] == ["az", "account", "show"]:
                return json.dumps({"id": "sub", "name": "test", "user": {"type": "user"}})
            if command[:3] == ["az", "bicep", "build"]:
                return '{"resources": []}'
            return ""

        with (
            patch("deploy.discover", return_value=[Path("20-human-approval")]),
            patch("deploy.run", side_effect=fake_run), redirect_stdout(io.StringIO()),
        ):
            with self.assertRaises(SystemExit):
                deploy.main(["--demo", "20", "--principal-id", "presenter",
                             "--search-location", "westus2"])
        self.assertFalse(any(command[:2] == ["az", "group"] for command in commands))
        self.assertFalse(any(command[:2] == ["az", "provider"] for command in commands))

    def test_retrieval_identity_preflight_fails_before_resource_changes(self) -> None:
        commands = []

        def fake_run(command, **kwargs):
            commands.append(command)
            if command[:3] == ["az", "account", "show"]:
                return json.dumps({"id": "sub", "name": "test", "user": {"type": "user"}})
            if command[:3] == ["az", "bicep", "build"]:
                return '{"resources": [{"type": "Microsoft.Search/searchServices"}]}'
            if command[-2:] == ["demo.py", "--preflight"]:
                raise RuntimeError("second identity is not configured")
            return ""

        for folder in (
            "21-permission-aware-retrieval", "31-native-agent-memory", "32-browser-runbook",
        ):
            commands.clear()
            with (
                self.subTest(folder=folder),
                patch("deploy.discover", return_value=[Path(folder)]),
                patch("deploy.run", side_effect=fake_run),
                redirect_stdout(io.StringIO()),
            ):
                with self.assertRaisesRegex(RuntimeError, "second identity"):
                    deploy.main(["--principal-id", "presenter", "--demo", folder.split("-")[0]])
            self.assertTrue(any(command[-2:] == ["demo.py", "--preflight"] for command in commands))
            self.assertFalse(any(command[:2] == ["az", "group"] for command in commands))
            self.assertFalse(any(command[:2] == ["az", "provider"] for command in commands))

    def test_unsupported_lab_region_fails_before_resource_changes(self) -> None:
        commands = []

        def fake_run(command, **kwargs):
            commands.append(command)
            if command[:3] == ["az", "account", "show"]:
                return json.dumps({"id": "sub", "name": "test", "user": {"type": "user"}})
            if command[:3] == ["az", "bicep", "build"]:
                return json.dumps({
                    "parameters": {"location": {"allowedValues": ["eastus"]}},
                    "resources": [{"type": "Microsoft.CognitiveServices/accounts"}],
                })
            return ""

        with (
            patch("deploy.discover", return_value=[Path("32-browser-runbook")]),
            patch("deploy.run", side_effect=fake_run),
            redirect_stdout(io.StringIO()),
            patch("sys.stderr", new_callable=io.StringIO) as errors,
        ):
            with self.assertRaises(SystemExit) as raised:
                deploy.main(["--principal-id", "presenter", "--demo", "32"])
        self.assertEqual(raised.exception.code, 2)
        self.assertIn("does not support --location 'eastus2'", errors.getvalue())
        self.assertFalse(any(command[:2] in (["az", "group"], ["az", "provider"]) for command in commands))

    def test_supported_browser_region_reaches_configuration_preflight(self) -> None:
        commands = []

        def fake_run(command, **kwargs):
            commands.append(command)
            if command[:3] == ["az", "account", "show"]:
                return json.dumps({"id": "sub", "name": "test", "user": {"type": "user"}})
            if command[:3] == ["az", "bicep", "build"]:
                return json.dumps({"parameters": {"location": {"allowedValues": ["eastus"]}}})
            if command[-2:] == ["demo.py", "--preflight"]:
                raise RuntimeError("reached configuration preflight")
            return ""

        with (
            patch("deploy.discover", return_value=[Path("32-browser-runbook")]),
            patch("deploy.run", side_effect=fake_run),
            redirect_stdout(io.StringIO()),
        ):
            with self.assertRaisesRegex(RuntimeError, "reached configuration preflight"):
                deploy.main(["--principal-id", "presenter", "--demo", "32", "--location", "eastus"])
        self.assertFalse(any(command[:2] in (["az", "group"], ["az", "provider"]) for command in commands))

    def test_managed_harness_region_fails_before_resource_changes(self) -> None:
        commands = []

        def fake_run(command, **kwargs):
            commands.append(command)
            if command[:3] == ["az", "account", "show"]:
                return json.dumps({"id": "sub", "name": "test", "user": {"type": "user"}})
            if command[:3] == ["az", "bicep", "build"]:
                return json.dumps({
                    "parameters": {"location": {"allowedValues": ["swedencentral"]}},
                    "resources": [{"type": "Microsoft.CognitiveServices/accounts"}],
                })
            return ""

        with (
            patch("deploy.discover", return_value=[Path("42-managed-harness-tool-search")]),
            patch("deploy.run", side_effect=fake_run),
            redirect_stdout(io.StringIO()),
            patch("sys.stderr", new_callable=io.StringIO) as errors,
        ):
            with self.assertRaises(SystemExit) as raised:
                deploy.main(["--principal-id", "presenter", "--demo", "42"])
        self.assertEqual(raised.exception.code, 2)
        self.assertIn("42-managed-harness-tool-search", errors.getvalue())
        self.assertFalse(any(command[:2] in (["az", "group"], ["az", "provider"]) for command in commands))

    def test_voice_and_content_preflight_receives_location_before_resource_changes(self) -> None:
        for name in ("40-voice-agent", "41-content-understanding"):
            with self.subTest(lab=name):
                commands = []

                def fake_run(command, **kwargs):
                    commands.append(command)
                    if command[:3] == ["az", "account", "show"]:
                        return json.dumps({"id": "sub", "name": "test", "user": {"type": "user"}})
                    if command[:3] == ["az", "bicep", "build"]:
                        return '{"resources": []}'
                    if command[-4:] == ["demo.py", "--preflight", "--location", "eastus2"]:
                        raise RuntimeError("reached native service preflight")
                    return ""

                with (
                    patch("deploy.discover", return_value=[Path(name)]),
                    patch("deploy.run", side_effect=fake_run),
                    redirect_stdout(io.StringIO()),
                ):
                    with self.assertRaisesRegex(RuntimeError, "reached native service preflight"):
                        deploy.main(["--principal-id", "presenter", "--demo", name])
                self.assertFalse(any(
                    command[:2] in (["az", "group"], ["az", "provider"]) for command in commands
                ))

    def test_automation_preflight_uses_deployed_outputs_before_live_calls(self) -> None:
        for name in ("42-managed-harness-tool-search", "43-agent-routines", "44-evaluation-optimizer"):
            with self.subTest(lab=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                folder = root / name
                folder.mkdir()
                commands = []
                project = "https://account.services.ai.azure.com/api/projects/workshop"

                def fake_run(command, **kwargs):
                    commands.append(command)
                    if command[:3] == ["az", "account", "show"]:
                        return json.dumps({"id": "sub", "name": "test", "user": {"type": "user"}})
                    if command[:3] == ["az", "bicep", "build"]:
                        return '{"resources": []}'
                    if command[:4] == ["az", "deployment", "group", "create"]:
                        return json.dumps({
                            "endpoint": {"value": "https://account.openai.azure.com/"},
                            "projectEndpoint": {"value": project},
                        })
                    if command[-2:] == ["demo.py", "--preflight"]:
                        self.assertEqual(kwargs["env"]["FOUNDRY_PROJECT_ENDPOINT"], project)
                        self.assertTrue((folder / ".azure" / "deployment.json").is_file())
                        raise RuntimeError("native access denied")
                    return ""

                with (
                    patch("deploy.ROOT", root),
                    patch("deploy.discover", return_value=[folder]),
                    patch("deploy.run", side_effect=fake_run),
                    patch("deploy.run_live") as live,
                    redirect_stdout(io.StringIO()),
                ):
                    with self.assertRaisesRegex(RuntimeError, "native access denied"):
                        deploy.main(["--principal-id", "presenter", "--demo", name])
                    live.assert_not_called()
                self.assertTrue(any(
                    command[:4] == ["az", "deployment", "group", "create"] for command in commands
                ))

    def test_content_safety_output_mapping(self) -> None:
        outputs = {"endpoint": {"value": "https://model.example/"},
                   "contentSafetyEndpoint": {"value": "https://safety.example/"}}
        result = deploy.demo_environment(outputs, {"CONTENT_SAFETY_ENDPOINT": "stale"})
        self.assertEqual(result["CONTENT_SAFETY_ENDPOINT"], "https://safety.example/")
        with self.assertRaisesRegex(ValueError, "contentSafetyEndpoint"):
            deploy.demo_environment({**outputs, "contentSafetyEndpoint": {"value": ""}}, {})

    def test_model_lab_output_mappings(self) -> None:
        for output, variable in (
            ("deniedEndpoint", "WORKSHOP_DENIED_ENDPOINT"),
            ("alternateDeployment", "AZURE_OPENAI_ALTERNATE_DEPLOYMENT"),
            ("routerDeployment", "AZURE_OPENAI_ROUTER_DEPLOYMENT"),
        ):
            with self.subTest(output=output):
                result = deploy.demo_environment(
                    {"endpoint": {"value": "https://model.example/"}, output: {"value": "new"}},
                    {variable: "stale"},
                )
                self.assertEqual(result[variable], "new")
                with self.assertRaisesRegex(ValueError, output):
                    deploy.demo_environment(
                        {"endpoint": {"value": "https://model.example/"}, output: {"value": ""}}, {},
                    )

    def test_lifecycle_lab_output_mappings(self) -> None:
        for output, variable in (
            ("applicationInsightsConnectionString", "APPLICATIONINSIGHTS_CONNECTION_STRING"),
            ("projectResourceId", "FOUNDRY_PROJECT_RESOURCE_ID"),
        ):
            with self.subTest(output=output):
                result = deploy.demo_environment(
                    {"endpoint": {"value": "https://model.example/"}, output: {"value": "configured"}},
                    {variable: "stale"},
                )
                self.assertEqual(result[variable], "configured")
                with self.assertRaisesRegex(ValueError, output):
                    deploy.demo_environment(
                        {"endpoint": {"value": "https://model.example/"}, output: {"value": ""}}, {},
                    )

    def test_search_and_agent_identity_output_mapping(self) -> None:
        outputs = {
            "endpoint": {"value": "https://example/"},
            "searchEndpoint": {"value": "https://search.search.windows.net"},
            "agentIdentityId": {"value": "agent-id"},
        }
        result = deploy.demo_environment(outputs, {})
        self.assertEqual(result["AZURE_SEARCH_ENDPOINT"], outputs["searchEndpoint"]["value"])
        self.assertEqual(result["ENTRA_AGENT_ID"], "agent-id")
        for name in ("searchEndpoint", "agentIdentityId"):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, name):
                deploy.demo_environment({**outputs, name: {"value": ""}}, {})

    def test_agent_identity_prerequisites_fail_before_azure_changes(self) -> None:
        with (
            patch("deploy.discover", return_value=[Path("12-entra-agent-id")]),
            patch.dict(os.environ, {}, clear=True),
            patch("deploy.run") as run,
        ):
            with self.assertRaises(SystemExit):
                deploy.main([])
            run.assert_not_called()

    def test_agent_identity_parameter_is_passed_but_secret_is_not(self) -> None:
        tenant = "11111111-1111-1111-1111-111111111111"
        blueprint = "22222222-2222-2222-2222-222222222222"
        agent = "33333333-3333-3333-3333-333333333333"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            folder = root / "12-entra-agent-id"
            folder.mkdir()
            outputs = {"endpoint": {"value": "https://example/"}, "agentIdentityId": {"value": agent}}
            commands = []

            def fake_run(command, **kwargs):
                commands.append(command)
                if command[:3] == ["az", "account", "show"]:
                    return json.dumps({"id": "sub", "name": "test", "user": {"type": "user"}})
                if command[:3] == ["az", "bicep", "build"]:
                    return '{"resources": []}'
                if command[:4] == ["az", "deployment", "group", "create"]:
                    return json.dumps(outputs)
                return ""

            with (
                patch("deploy.ROOT", root), patch("deploy.discover", return_value=[folder]),
                patch("deploy.run", side_effect=fake_run), patch("deploy.run_live"),
                patch.dict(os.environ, {
                    "ENTRA_TENANT_ID": tenant, "ENTRA_BLUEPRINT_CLIENT_ID": blueprint,
                    "ENTRA_AGENT_ID": agent, "ENTRA_BLUEPRINT_SECRET": "test-only-secret",
                }), redirect_stdout(io.StringIO()),
            ):
                deploy.main(["--principal-id", "presenter"])
            deployment = next(command for command in commands
                              if command[:4] == ["az", "deployment", "group", "create"])
            self.assertIn(f"agentIdentityId={agent}", deployment)
            self.assertNotIn("test-only-secret", str(commands))

    def test_content_safety_does_not_require_a_model_deployment(self) -> None:
        result = deploy.demo_environment(
            {"endpoint": {"value": "https://safety.cognitiveservices.azure.com/"}},
            {"AZURE_OPENAI_DEPLOYMENT": "stale"},
        )
        self.assertNotIn("AZURE_OPENAI_DEPLOYMENT", result)

    def test_embedding_deployment_mapping_and_validation(self) -> None:
        outputs = {"endpoint": {"value": "https://example/"},
                   "embeddingDeployment": {"value": "embeddings"}}
        self.assertEqual(
            deploy.demo_environment(outputs, {})["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"],
            "embeddings",
        )
        outputs["embeddingDeployment"]["value"] = ""
        with self.assertRaisesRegex(ValueError, "embeddingDeployment"):
            deploy.demo_environment(outputs, {})

    def test_runs_tests_then_deploys_and_persists_outputs(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            demo = root / "1-example"
            demo.mkdir()
            outputs = {
                "endpoint": {"value": "https://example.openai.azure.com/"},
                "deployment": {"value": "router"},
            }
            commands = []

            def fake_run(command, **kwargs):
                commands.append(command)
                if command[:3] == ["az", "account", "show"]:
                    return json.dumps({"id": "sub", "name": "test", "user": {"type": "user"}})
                if command[:4] == ["az", "deployment", "group", "create"]:
                    return json.dumps(outputs)
                if command[:3] == ["az", "bicep", "build"]:
                    return json.dumps({"resources": [
                        {"type": "Microsoft.CognitiveServices/accounts"},
                        {"type": "Microsoft.Storage/storageAccounts"},
                        {"type": "Microsoft.Web/sites"},
                        {"type": "Microsoft.Authorization/roleAssignments"},
                    ]})
                return ""

            with (
                patch("deploy.ROOT", root),
                patch("deploy.discover", return_value=[demo]),
                patch("deploy.run", side_effect=fake_run),
                patch("deploy.run_live") as live,
                redirect_stdout(io.StringIO()),
            ):
                deploy.main(["--principal-id", "presenter"])
            state = json.loads((demo / ".azure/deployment.json").read_text())
            self.assertEqual(state["resourceGroup"], "rg-foundry-example-demo")
            self.assertEqual(state["outputs"], outputs)
            live.assert_called_once()
            self.assertEqual(live.call_args.args[2]["AZURE_OPENAI_DEPLOYMENT"], "router")
            test_index = next(i for i, cmd in enumerate(commands) if "unittest" in cmd)
            deploy_index = next(i for i, cmd in enumerate(commands) if "group" in cmd and "deployment" in cmd)
            self.assertLess(test_index, deploy_index)
            self.assertIn("--subscription", commands[deploy_index])
            self.assertEqual(
                commands[deploy_index][commands[deploy_index].index("--name") + 1],
                "example-demo",
            )
            registered = {
                cmd[cmd.index("--namespace") + 1]
                for cmd in commands if cmd[:3] == ["az", "provider", "register"]
            }
            self.assertEqual(
                registered, {"Microsoft.CognitiveServices", "Microsoft.Storage", "Microsoft.Web"}
            )

    def test_local_failures_prevent_deployment(self) -> None:
        with (
            patch("deploy.discover", return_value=[Path("/demo")]),
            patch("deploy.run") as run,
            patch("deploy.run_live") as live,
            redirect_stdout(io.StringIO()),
        ):
            run.side_effect = [
                json.dumps({"id": "sub", "name": "test", "user": {"type": "user"}}),
                subprocess.CalledProcessError(1, ["az", "bicep", "build"]),
            ]
            with self.assertRaises(subprocess.CalledProcessError):
                deploy.main(["--principal-id", "presenter"])
            live.assert_not_called()
            self.assertFalse(any("create" in call.args[0] for call in run.call_args_list))

    def test_live_retry_is_bounded_and_logs_failures(self) -> None:
        failure = subprocess.CompletedProcess(
            [], 1, stdout="openai.PermissionDeniedError: Error code: 403"
        )
        success = subprocess.CompletedProcess([], 0, stdout="Serving model: example\nAnswer: Hello\n")
        with tempfile.TemporaryDirectory() as directory:
            demo = Path(directory)
            (demo / ".azure").mkdir()
            with (
                patch("deploy.subprocess.run", side_effect=[failure, success]) as run,
                patch("deploy.time.sleep") as sleep,
                redirect_stdout(io.StringIO()),
            ):
                deploy.run_live(Path("/python"), demo, {})
                self.assertEqual(run.call_count, 2)
                sleep.assert_called_once_with(30)
            self.assertIn("Answer: Hello", (demo / ".azure/demo.log").read_text())
            with (
                patch("deploy.subprocess.run", return_value=failure) as run,
                patch("deploy.time.sleep"),
                redirect_stdout(io.StringIO()),
                self.assertRaisesRegex(RuntimeError, "failed"),
            ):
                deploy.run_live(Path("/python"), demo, {})
            self.assertEqual(run.call_count, 11)

    def test_dns_failure_retries_then_succeeds(self) -> None:
        for message in (
            "[Errno 8] nodename nor servname provided, or not known",
            "[Errno -2] Name or service not known",
            "[Errno -3] Temporary failure in name resolution",
            "[Errno 11001] getaddrinfo failed",
        ):
            with self.subTest(message=message), tempfile.TemporaryDirectory() as directory:
                demo = Path(directory)
                (demo / ".azure").mkdir()
                failure = subprocess.CompletedProcess(
                    [], 1, stdout=f"httpx.ConnectError: {message}\n"
                    "openai.APIConnectionError: Connection error.\n",
                )
                with (
                    patch("deploy.subprocess.run", side_effect=[
                        failure, subprocess.CompletedProcess([], 0, stdout="Answer: Paris\n"),
                    ]) as run,
                    patch("deploy.time.sleep") as sleep,
                    redirect_stdout(io.StringIO()) as output,
                ):
                    deploy.run_live(Path("/python"), demo, {})
                self.assertEqual(run.call_count, 2)
                sleep.assert_called_once_with(30)
                self.assertIn("DNS lookup failed", output.getvalue())
                transcript = (demo / ".azure/demo.log").read_text()
                self.assertIn(message, transcript)
                self.assertIn("Answer: Paris", transcript)

    def test_persistent_dns_failure_stops_with_actionable_error(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            demo = Path(directory)
            (demo / ".azure").mkdir()
            failure = subprocess.CompletedProcess(
                [], 1, stdout="azure.core.exceptions.ServiceRequestError: "
                "[Errno -3] Temporary failure in name resolution",
            )
            with (
                patch("deploy.subprocess.run", return_value=failure) as run,
                patch("deploy.time.sleep") as sleep,
                redirect_stdout(io.StringIO()),
                self.assertRaisesRegex(RuntimeError, "DNS resolution failed.*after 11 attempts"),
            ):
                deploy.run_live(Path("/python"), demo, {})
            self.assertEqual(run.call_count, 11)
            self.assertEqual(sleep.call_count, 10)

    def test_other_connection_failures_are_not_retried(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            demo = Path(directory)
            (demo / ".azure").mkdir()
            failure = subprocess.CompletedProcess(
                [], 1, stdout="httpx.ConnectError: [SSL: CERTIFICATE_VERIFY_FAILED]\n"
                "openai.APIConnectionError: Connection error.\n",
            )
            with (
                patch("deploy.subprocess.run", return_value=failure) as run,
                patch("deploy.time.sleep") as sleep,
                redirect_stdout(io.StringIO()),
                self.assertRaises(RuntimeError),
            ):
                deploy.run_live(Path("/python"), demo, {})
            run.assert_called_once()
            sleep.assert_not_called()

    def test_non_propagation_errors_are_not_retried(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            demo = Path(directory)
            (demo / ".azure").mkdir()
            with (
                patch(
                    "deploy.subprocess.run",
                    return_value=subprocess.CompletedProcess([], 1, stdout="Invalid input"),
                ),
                patch("deploy.time.sleep") as sleep,
                redirect_stdout(io.StringIO()),
                self.assertRaises(RuntimeError),
            ):
                deploy.run_live(Path("/python"), demo, {})
            sleep.assert_not_called()


if __name__ == "__main__":
    unittest.main()
