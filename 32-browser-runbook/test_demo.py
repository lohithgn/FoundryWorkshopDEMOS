"""Offline boundary tests: no Azure deployment, browser sessions or model charges."""

from contextlib import asynccontextmanager
from contextlib import redirect_stderr
import copy
import importlib.util
from io import StringIO
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from types import SimpleNamespace as NS
from unittest.mock import AsyncMock, MagicMock, patch
from uuid import uuid4

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    BrowserAutomationToolboxTool, BrowserAutomationToolConnectionParameters,
    BrowserAutomationToolParameters,
)
from azure.identity import AzureCliCredential
import httpx
from mcp.types import CallToolResult, TextContent
from openai.types.responses import ResponseFunctionToolCall

spec = importlib.util.spec_from_file_location("browser_runbook", Path(__file__).with_name("demo.py"))
demo = importlib.util.module_from_spec(spec)
spec.loader.exec_module(demo)
SITE = "https://fixture.azurewebsites.net/api/runbook"
PROJECT = "https://foundry.services.ai.azure.com/api/projects/browser-runbook"
CONNECTION = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/foundry/projects/browser-runbook/connections/browser"
RUN, FORM = "1" * 32, "2" * 32
ACTION = demo.expected_action(SITE, RUN, FORM)
SNAPSHOT = "SYNTHETIC ONLY Foundry Browser Lab " + FORM + " Workshop Bot bot@example.invalid"


def state():
    return {
        "action": copy.deepcopy(ACTION), "draft": {**demo.FIELDS, "formId": FORM},
        "record": None, "permit": None, "expires": 9999999999999, "rejected": 0,
    }


def tools():
    return [NS(name="browser." + name, inputSchema={
        "properties": {key: {} for key in parameters}, "required": list(parameters),
    }) for name, parameters in demo.TOOLS.items()]


class Contracts(unittest.TestCase):
    def test_sdk_native_shape(self):
        tool = BrowserAutomationToolboxTool(browser_automation=BrowserAutomationToolParameters(
            connection=BrowserAutomationToolConnectionParameters(project_connection_id=CONNECTION)))
        self.assertEqual(tool.as_dict(), {
            "type": "browser_automation",
            "browser_automation": {"connection": {"project_connection_id": CONNECTION}},
        })
        with AzureCliCredential() as credential, AIProjectClient(endpoint=PROJECT, credential=credential) as project:
            with project.get_openai_client(max_retries=0) as client:
                self.assertEqual(str(client.base_url), PROJECT + "/openai/v1/")
                self.assertEqual(client.max_retries, 0)

    def test_json_digest_cross_language(self):
        result = subprocess.run(
            ["node", "-e", "const f=require('./fixture/index'); console.log(f.digest(JSON.parse(process.argv[1])))", demo.canonical(ACTION)],
            cwd=demo.ROOT, capture_output=True, text=True, check=True,
        )
        self.assertEqual(result.stdout.strip(), demo.digest(ACTION))

    def test_fixture_state_machine(self):
        subprocess.run(["node", "--test", "fixture/test.js"], cwd=demo.ROOT, check=True, capture_output=True, text=True)

    def test_evidence_not_model_text(self):
        demo.verify_draft(state(), ACTION, SNAPSHOT)
        for field, value in [
            ("record", {"id": "unexpected"}), ("permit", {"digest": "unexpected"}),
            ("draft", None), ("expires", 1), ("action", {**ACTION, "formId": "changed"}),
        ]:
            changed = state()
            changed[field] = value
            with self.subTest(field=field), self.assertRaises(RuntimeError):
                demo.verify_draft(changed, ACTION, SNAPSHOT)
        with self.assertRaises(RuntimeError):
            demo.verify_draft(state(), ACTION, "Model says done")

    def test_default_cannot_approve_and_scope_is_exact(self):
        for name, args in [
            ("browser_click", {"target": "#submit"}),
            ("browser_navigate", {"url": "https://real-bookings.example"}),
            ("browser_type", {"text": "x", "submit": True}),
            ("browser_snapshot", {"browserSessionId": "other"}),
            ("browser_fill_form", {"fields": [{"target": "#name", "value": "Real Name"}]}),
            ("browser_evaluate", {"function": "fetch('/approve')"}),
        ]:
            with self.subTest(name=name), self.assertRaises(RuntimeError):
                demo.authorize_draft_tool(name, args, SITE, RUN)
        for name, args in [
            ("browser_navigate", {"url": f"{SITE}/{RUN}/site"}),
            ("browser_snapshot", {}),
            ("browser_find", {"text": demo.SESSION}),
            ("browser_click", {"target": "#save-draft"}),
            ("browser_fill_form", {"fields": [{"target": "#" + k, "value": v} for k, v in demo.FIELDS.items()]}),
        ]:
            demo.authorize_draft_tool(name, args, SITE, RUN)

    def test_fail_closed_schema_and_results(self):
        with self.assertRaises(RuntimeError):
            demo.Browser(None, [])
        changed = tools()
        changed[0].inputSchema["required"].append("newParameter")
        with self.assertRaises(RuntimeError):
            demo.Browser(None, changed)
        for result in [
            CallToolResult(isError=True, content=[]),
            CallToolResult(content=[TextContent(type="text", text="ServerError")]),
            CallToolResult(content=[], structuredContent={"error": "failure"}),
            CallToolResult(content=[TextContent(type="text", text='{"status":"failed"}')]),
        ]:
            with self.assertRaises(RuntimeError):
                demo.result_value(result)
        self.assertEqual(demo.session_id('{"result":{"browserSessionId":"opaque"}}'), "opaque")
        self.assertIsNone(demo.session_id("not a session"))

    def test_cli_requires_explicit_exact_action(self):
        base = ["--project-endpoint", PROJECT, "--site-endpoint", SITE,
                "--connection", CONNECTION, "--deployment", "gpt-4.1-mini"]
        with patch.dict("os.environ", {}, clear=True), redirect_stderr(StringIO()):
            args = demo.configuration(base)
            self.assertIsNone(args.approve_action)
            for extras in [["--approve-action", "yes"], ["--run-id", RUN], ["--cleanup"],
                           ["--preflight", "--run-id", RUN, "--cleanup"]]:
                with self.subTest(extras=extras), self.assertRaises(SystemExit):
                    demo.configuration(base + extras)

    def test_explicit_configuration_does_not_read_invalid_deployment_state(self):
        base = ["--project-endpoint", PROJECT, "--site-endpoint", SITE,
                "--connection", CONNECTION, "--deployment", "gpt-4.1-mini"]
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "deployment.json").write_text("")
            with patch.object(demo, "STATE", directory), patch.dict("os.environ", {}, clear=True):
                args = demo.configuration(base)
                self.assertEqual((args.project_endpoint, args.site_endpoint, args.deployment),
                                 (PROJECT, SITE, "gpt-4.1-mini"))
                self.assertEqual(
                    demo.configuration(["--preflight", "--connection", CONNECTION]).connection,
                    CONNECTION,
                )
                stderr = StringIO()
                with redirect_stderr(stderr), self.assertRaises(SystemExit) as error:
                    demo.configuration(base[:-2])
                self.assertEqual(error.exception.code, 2)
                self.assertIn("Cannot read deployment outputs", stderr.getvalue())

    def test_missing_configuration_uses_valid_deployment_outputs(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "deployment.json").write_text(json.dumps({"outputs": {
                "projectEndpoint": {"value": PROJECT},
                "toolEndpoint": {"value": SITE},
                "deployment": {"value": "from-file"},
            }}))
            with patch.object(demo, "STATE", directory), patch.dict(
                "os.environ", {"AZURE_OPENAI_DEPLOYMENT": "from-environment"}, clear=True,
            ):
                args = demo.configuration(["--connection", CONNECTION])
                self.assertEqual((args.project_endpoint, args.site_endpoint, args.deployment),
                                 (PROJECT, SITE, "from-environment"))
                args = demo.configuration(["--connection", CONNECTION, "--deployment", "from-cli"])
                self.assertEqual(args.deployment, "from-cli")

    def test_missing_remote_run_cleanup_is_idempotent(self):
        with httpx.Client(transport=httpx.MockTransport(
            lambda request: httpx.Response(404)
        )) as client:
            self.assertIsNone(demo.Fixture(client, SITE, RUN, "private").request("delete", "DELETE"))
            with self.assertRaises(httpx.HTTPStatusError):
                demo.Fixture(client, SITE, RUN, "private").request("state")

    def test_configuration_only_preflight_no_existing_resources_or_clients(self):
        with patch.dict("os.environ", {"BROWSER_AUTOMATION_CONNECTION_NAME": "browser-workspace"}, clear=True):
            with patch.object(demo, "AzureCliCredential") as credential, patch.object(
                demo, "AIProjectClient"
            ) as project, patch.object(demo.httpx, "Client") as client:
                demo.main(["--preflight"])
                credential.assert_not_called()
                project.assert_not_called()
                client.assert_not_called()
        with patch.dict("os.environ", {}, clear=True), redirect_stderr(StringIO()):
            for flags in [["--preflight"], ["--preflight", "--connection", "not a valid name"]]:
                with self.subTest(flags=flags), self.assertRaises(SystemExit) as error:
                    demo.main(flags)
                self.assertEqual(error.exception.code, 2)
        self.assertEqual(demo.configuration(["--preflight", "--connection", CONNECTION]).connection, CONNECTION)


class RemoteFlow(unittest.IsolatedAsyncioTestCase):
    async def test_real_mcp_boundary_and_session_argument(self):
        session = NS(call_tool=AsyncMock(side_effect=[
            CallToolResult(content=[], structuredContent={"browserSessionId": "secret"}),
            CallToolResult(content=[TextContent(type="text", text=SNAPSHOT)]),
            CallToolResult(content=[]),
        ]))
        browser = demo.Browser(session, tools())
        await browser.open()
        self.assertEqual(await browser.call("browser_snapshot"), SNAPSHOT)
        await browser.close()
        self.assertIsNone(browser.id)
        self.assertEqual(session.call_tool.call_args_list[1].kwargs["arguments"], {"browserSessionId": "secret"})

    async def test_native_toolbox_failure_cleanup(self):
        project = NS(toolboxes=MagicMock(), connections=MagicMock())
        project.toolboxes.create_version.return_value = NS(name="unique", version="1")
        project.connections.get.return_value = NS(id=CONNECTION)
        credential = NS(get_token=MagicMock(return_value=NS(token="private")))
        session = NS(initialize=AsyncMock(), list_tools=AsyncMock(return_value=NS(tools=tools())),
                     call_tool=AsyncMock(side_effect=[
                         CallToolResult(content=[], structuredContent={"browserSessionId": "private"}),
                         CallToolResult(content=[]),
                     ]))

        @asynccontextmanager
        async def transport(*args, **kwargs):
            self.assertEqual(kwargs["headers"]["Authorization"], "Bearer private")
            yield None, None, None

        @asynccontextmanager
        async def client(*args):
            yield session

        with patch.object(demo, "streamablehttp_client", transport), patch.object(demo, "ClientSession", client):
            with self.assertRaisesRegex(ValueError, "injected"):
                async with demo.managed_browser(project, credential, "browser-workspace", PROJECT):
                    raise ValueError("injected")
        self.assertEqual(session.call_tool.call_args_list[-1].args[0], "browser.close_browser_session")
        project.toolboxes.delete_version.assert_called_once_with(name="unique", version="1")
        native = project.toolboxes.create_version.call_args.kwargs["tools"][0].as_dict()
        self.assertEqual(native["type"], "browser_automation")
        self.assertEqual(native["browser_automation"]["connection"]["project_connection_id"], CONNECTION)
        project.connections.get.assert_called_once_with(name="browser-workspace", include_credentials=False)

    async def test_preflight_no_browser_and_discovery_failure_cleanup(self):
        project = NS(toolboxes=MagicMock())
        project.toolboxes.create_version.return_value = NS(name="unique", version="1")
        credential = NS(get_token=MagicMock(return_value=NS(token="private")))
        session = NS(initialize=AsyncMock(), list_tools=AsyncMock(return_value=NS(tools=tools())),
                     call_tool=AsyncMock())

        @asynccontextmanager
        async def transport(*args, **kwargs):
            yield None, None, None

        @asynccontextmanager
        async def client(*args):
            yield session

        with patch.object(demo, "streamablehttp_client", transport), patch.object(demo, "ClientSession", client):
            async with demo.managed_browser(project, credential, CONNECTION, PROJECT, preflight=True):
                pass
            session.call_tool.assert_not_called()
            project.toolboxes.delete_version.assert_called_once()
            project.toolboxes.delete_version.reset_mock()
            session.list_tools.return_value = NS(tools=[])
            with self.assertRaises(RuntimeError):
                async with demo.managed_browser(project, credential, CONNECTION, PROJECT, preflight=True):
                    pass
            project.toolboxes.delete_version.assert_called_once()

    async def test_draft_reject_probe_has_no_approval(self):
        browser = NS(call=AsyncMock(side_effect=[SNAPSHOT, SNAPSHOT]))
        fixture = NS(site=SITE, run_id=RUN, request=MagicMock(side_effect=[state(), state()]))
        with patch.object(demo, "draft", AsyncMock()):
            await demo.prepare(browser, fixture, None, "model", ACTION)
        self.assertEqual([c.args[0] for c in fixture.request.call_args_list], ["state", "state"])
        self.assertEqual([c.args[0] for c in browser.call.call_args_list], ["browser_snapshot", "browser_snapshot"])

    async def test_approval_requires_page_and_record(self):
        record = {"id": "receipt-123", "action": ACTION}
        complete = {**state(), "record": record}
        browser = NS(call=AsyncMock(side_effect=["", SNAPSHOT, "", "Registration recorded receipt-123"]))
        fixture = NS(site=SITE, run_id=RUN, request=MagicMock(side_effect=[state(), {}, complete]))
        await demo.submit(browser, fixture, ACTION, demo.digest(ACTION))
        self.assertEqual(fixture.request.call_args_list[1].args, ("approve", "POST", {"digest": demo.digest(ACTION)}))
        self.assertEqual(sum(c.args[0] == "browser_click" for c in browser.call.call_args_list), 1)

    async def test_submission_missing_receipt_or_wrong_record_fails(self):
        for receipt, record in [
            ("Model says submitted", {"id": "receipt-123", "action": ACTION}),
            ("Registration recorded receipt-123", {"id": "receipt-123", "action": {**ACTION, "name": "changed"}}),
            ("Registration recorded receipt-123", None),
        ]:
            browser = NS(call=AsyncMock(side_effect=["", SNAPSHOT, "", receipt]))
            fixture = NS(site=SITE, run_id=RUN, request=MagicMock(side_effect=[
                state(), {}, {**state(), "record": record},
            ]))
            with self.subTest(record=record), self.assertRaises(RuntimeError):
                await demo.submit(browser, fixture, ACTION, demo.digest(ACTION))

    async def test_approval_mismatch_never_calls_browser_or_service(self):
        browser, fixture = NS(call=AsyncMock()), NS(request=MagicMock())
        with self.assertRaises(ValueError):
            await demo.submit(browser, fixture, ACTION, "yes")
        browser.call.assert_not_called()
        fixture.request.assert_not_called()

    async def test_ambiguous_click_never_retried(self):
        browser = NS(call=AsyncMock(side_effect=["", SNAPSHOT, TimeoutError()]))
        fixture = NS(site=SITE, run_id=RUN, request=MagicMock(side_effect=[state(), {}]))
        with self.assertRaises(TimeoutError):
            await demo.submit(browser, fixture, ACTION, demo.digest(ACTION))
        self.assertEqual(sum(c.args[0] == "browser_click" for c in browser.call.call_args_list), 1)

    async def test_model_final_text_alone_fails(self):
        client = NS(responses=NS(create=MagicMock(return_value=NS(status="completed", output=[]))))
        browser = NS(calls=[])
        with self.assertRaises(RuntimeError):
            await demo.draft(client, "model", browser, SITE, RUN)

    async def test_model_sdk_function_call_shapes(self):
        def response_call(name, args):
            return NS(status="completed", output=[ResponseFunctionToolCall(
                type="function_call", call_id=uuid4().hex, name=name, arguments=json.dumps(args),
            )])
        client = NS(responses=NS(create=MagicMock(side_effect=[
            response_call("browser_navigate", {"url": f"{SITE}/{RUN}/site"}),
            response_call("browser_fill_form", {"fields": [{"target": "#" + k, "value": v} for k, v in demo.FIELDS.items()]}),
            response_call("browser_click", {"target": "#save-draft"}),
            NS(status="completed", output=[]),
        ])))
        browser = NS(calls=[], call=AsyncMock())

        async def call(name, **kwargs):
            browser.calls.append(name)
            return "native evidence"

        browser.call.side_effect = call
        await demo.draft(client, "model", browser, SITE, RUN)
        kwargs = client.responses.create.call_args.kwargs
        self.assertFalse(kwargs["parallel_tool_calls"])
        self.assertFalse(kwargs["store"])
        self.assertIn("locally read and directly injected", kwargs["instructions"])

    async def test_pending_claim_replay_and_failure_cleanup(self):
        # Only project-local scratch files; every test removes its private directory.
        directory = demo.ROOT / f".test-state-{uuid4().hex}"
        directory.mkdir(mode=0o700)
        args = NS(site_endpoint=SITE, project_endpoint=PROJECT, connection=CONNECTION,
                  run_id=RUN, approve_action=demo.digest(ACTION), cleanup=False)
        pending = {"action": ACTION, "control": "private", "projectEndpoint": PROJECT, "connection": CONNECTION}

        @asynccontextmanager
        async def managed(*args, **kwargs):
            yield NS()

        try:
            with patch.object(demo, "STATE", directory):
                path = demo.save_pending(pending)
                self.assertEqual(path.stat().st_mode & 0o777, 0o600)
                with patch.object(demo, "managed_browser", managed), patch.object(
                    demo, "submit", AsyncMock(side_effect=TimeoutError())
                ), patch.object(demo.Fixture, "request", return_value=None) as request:
                    with self.assertRaises(TimeoutError):
                        await demo.execute(args, None, None, None)
                    request.assert_called_once_with("delete", "DELETE")
                self.assertFalse(path.exists())
                with self.assertRaises(FileNotFoundError):
                    await demo.execute(args, None, None, None)
        finally:
            for path in directory.iterdir():
                path.unlink()
            directory.rmdir()

    async def test_cleanup_failure_preserves_cleanup_only_capability(self):
        directory = demo.ROOT / f".test-state-{uuid4().hex}"
        directory.mkdir(mode=0o700)
        args = NS(site_endpoint=SITE, project_endpoint=PROJECT, connection=CONNECTION,
                  run_id=RUN, approve_action=None, cleanup=True)
        try:
            with patch.object(demo, "STATE", directory):
                path = demo.save_pending({"action": ACTION, "control": "private", "projectEndpoint": PROJECT, "connection": CONNECTION})
                with patch.object(demo.Fixture, "request", side_effect=TimeoutError()):
                    with self.assertRaises(TimeoutError):
                        await demo.execute(args, None, None, None)
                self.assertTrue(json.loads(path.read_text())["cleanupOnly"])
                args.cleanup, args.approve_action = False, demo.digest(ACTION)
                with self.assertRaises(ValueError):
                    await demo.execute(args, None, None, None)
        finally:
            for path in directory.iterdir():
                path.unlink()
            directory.rmdir()


if __name__ == "__main__":
    unittest.main()
