"""A locally orchestrated skill using native Foundry Browser Automation via Toolbox."""

import argparse
import asyncio
from contextlib import asynccontextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import sys
import time
from urllib.parse import urlsplit
from uuid import uuid4

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    BrowserAutomationToolboxTool,
    BrowserAutomationToolConnectionParameters,
    BrowserAutomationToolParameters,
)
from azure.identity import AzureCliCredential
import httpx
from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client


ROOT = Path(__file__).resolve().parent
STATE = ROOT / ".azure"
FIELDS = {"name": "Workshop Bot", "email": "bot@example.invalid"}
SESSION = "Foundry Browser Lab"
TOOLS = {
    "create_browser_session": set(),
    "close_browser_session": {"browserSessionId"},
    "browser_navigate": {"browserSessionId", "url"},
    "browser_snapshot": {"browserSessionId"},
    "browser_find": {"browserSessionId", "text"},
    "browser_click": {"browserSessionId", "target"},
    "browser_fill_form": {"browserSessionId", "fields"},
}
MODEL_PARAMETERS = {
    "browser_navigate": {"url": {"type": "string"}},
    "browser_snapshot": {},
    "browser_find": {"text": {"type": "string"}},
    "browser_click": {"target": {"type": "string"}},
    "browser_fill_form": {"fields": {
        "type": "array", "items": {
            "type": "object",
            "properties": {"target": {"type": "string"}, "value": {"type": "string"}},
            "required": ["target", "value"], "additionalProperties": False,
        },
    }},
}


class RunbookError(RuntimeError):
    """A host-authored diagnostic safe to display without transport secrets."""


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def digest(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def expected_action(site, run_id, form_id):
    return {
        "site": site, "method": "POST", "path": f"/api/runbook/{run_id}/submit",
        "runId": run_id, "formId": form_id, "session": SESSION, **FIELDS,
    }


def endpoint(value, suffix):
    parsed = urlsplit(value or "")
    if (
        parsed.scheme != "https" or not parsed.hostname
        or parsed.username or parsed.password or parsed.query or parsed.fragment
        or parsed.port not in (None, 443) or not parsed.path.rstrip("/").endswith(suffix)
    ):
        raise ValueError(f"Expected a plain HTTPS endpoint ending in {suffix}.")
    return value.rstrip("/")


class Fixture:
    def __init__(self, client, site, run_id=None, control=None):
        self.client, self.site = client, site
        self.run_id, self.control = run_id, control

    def request(self, operation, method="GET", body=None):
        path = f"/{self.run_id}/{operation}" if self.run_id else f"/{operation}"
        response = self.client.request(
            method, self.site + path, json=body,
            headers={"x-run-control": self.control} if self.control else {},
        )
        if operation == "delete" and response.status_code == 404:
            return None
        response.raise_for_status()
        return response.json() if response.status_code != 204 else None


def result_value(result):
    if result.isError:
        raise RunbookError("Managed browser tool returned an MCP error.")
    structured = result.structuredContent
    texts = [block.text for block in result.content if block.type == "text"]
    if any("ServerError" in text for text in texts):
        raise RunbookError("Managed browser reported ServerError.")
    value = structured if structured is not None else "\n".join(texts)
    if structured is None and len(texts) == 1:
        try:
            value = json.loads(texts[0])
        except json.JSONDecodeError:
            pass
    if isinstance(value, dict) and (
        value.get("error") or value.get("isError") or value.get("status") in ("error", "failed")
    ):
        raise RunbookError("Managed browser returned a structured execution error.")
    return value


def session_id(value):
    if isinstance(value, str):
        try:
            return session_id(json.loads(value))
        except json.JSONDecodeError:
            return None
    if isinstance(value, dict):
        if isinstance(value.get("browserSessionId"), str) and value["browserSessionId"]:
            return value["browserSessionId"]
        for child in value.values():
            found = session_id(child)
            if found:
                return found
    if isinstance(value, list):
        for child in value:
            found = session_id(child)
            if found:
                return found
    return None


class Browser:
    """Only this adapter can call the real remote tools; the model cannot bypass it."""

    def __init__(self, session, tool_list):
        self.session, self.id = session, None
        self.names = {}
        self.calls = []
        for short, parameters in TOOLS.items():
            candidates = [t for t in tool_list if t.name.split(".")[-1] == short]
            if len(candidates) != 1:
                raise RunbookError(f"Toolbox must expose exactly one {short}; preview schema unavailable.")
            tool = candidates[0]
            schema = tool.inputSchema
            if not parameters <= set(schema.get("properties", {})):
                raise RunbookError(f"Unsupported managed tool parameter shape: {short}.")
            if not set(schema.get("required", [])) <= parameters:
                raise RunbookError(f"New required managed tool parameters: {short}.")
            self.names[short] = tool.name

    async def call(self, name, **arguments):
        if name not in self.names:
            raise RunbookError("Tool is not in the host allowlist.")
        if name != "create_browser_session":
            if not self.id:
                raise RunbookError("No managed browser session.")
            arguments["browserSessionId"] = self.id
        result = await self.session.call_tool(self.names[name], arguments=arguments)
        value = result_value(result)
        self.calls.append(name)
        print(f"Managed tool completed: {name}", flush=True)
        return value

    async def open(self):
        self.id = session_id(await self.call("create_browser_session"))
        if not self.id:
            raise RunbookError("No browserSessionId in managed create result; cannot continue.")

    async def close(self):
        if self.id:
            try:
                await self.call("close_browser_session")
            finally:
                self.id = None


@asynccontextmanager
async def managed_browser(project, credential, connection, project_endpoint, *, preflight=False):
    if not connection.startswith("/"):
        connection = project.connections.get(name=connection, include_credentials=False).id
    toolbox = project.toolboxes.create_version(
        name=f"browser-runbook-{uuid4().hex}",
        description="Native managed Browser Automation; synthetic workshop only.",
        tools=[BrowserAutomationToolboxTool(
            browser_automation=BrowserAutomationToolParameters(
                connection=BrowserAutomationToolConnectionParameters(project_connection_id=connection)
            )
        )],
    )
    try:
        url = (
            f"{project_endpoint}/toolboxes/{toolbox.name}"
            f"/versions/{toolbox.version}/mcp?api-version=v1"
        )
        token = credential.get_token("https://ai.azure.com/.default").token
        async with streamablehttp_client(
            url, headers={"Authorization": f"Bearer {token}"}, timeout=60, sse_read_timeout=300,
        ) as (read, write, _):
            async with ClientSession(read, write) as session:
                await session.initialize()
                tools = (await session.list_tools()).tools
                browser = Browser(session, tools)
                try:
                    if not preflight:
                        await browser.open()
                    yield browser
                finally:
                    await browser.close()
    finally:
        project.toolboxes.delete_version(name=toolbox.name, version=toolbox.version)


def model_tools():
    return [
        {
            "type": "function", "name": name, "description": f"Managed remote {name}.",
            "parameters": {
                "type": "object", "properties": properties,
                "required": list(properties), "additionalProperties": False,
            },
            "strict": True,
        }
        for name, properties in MODEL_PARAMETERS.items()
    ]


def authorize_draft_tool(name, arguments, site, run_id):
    root = f"{site}/{run_id}"
    if name not in MODEL_PARAMETERS or set(arguments) != set(MODEL_PARAMETERS[name]):
        raise RunbookError("Model requested an unauthorized tool or parameter.")
    if name == "browser_navigate" and arguments["url"] not in (root + "/site", root + "/form"):
        raise RunbookError("Navigation outside this run is forbidden.")
    if name == "browser_click" and arguments["target"] not in ("#session", "#save-draft"):
        raise RunbookError("Submission and arbitrary clicks are disabled during drafting.")
    if name == "browser_find" and arguments["text"] != SESSION:
        raise RunbookError("Only the requested session may be searched.")
    if name == "browser_fill_form":
        fields = arguments["fields"]
        expected = [{"target": "#" + key, "value": value} for key, value in FIELDS.items()]
        if not isinstance(fields, list) or len(fields) != 2 or any(f not in fields for f in expected):
            raise RunbookError("Only the exact synthetic form values are permitted.")


async def draft_call(browser, name, arguments, site, run_id):
    authorize_draft_tool(name, arguments, site, run_id)
    return await browser.call(name, **arguments)


async def draft(client, deployment, browser, site, run_id):
    history = [{
        "role": "user", "content": (
            f"Draft only. Start at {site}/{run_id}/site. Locate {SESSION}. "
            f"Fill Name={FIELDS['name']}, Email={FIELDS['email']}. "
            "Use selectors #session, #name, #email and #save-draft after observing them. "
            "Save draft and inspect a fresh snapshot; stop before submitting."
        ),
    }]
    instructions = (ROOT / "SKILL.md").read_text(encoding="utf-8")
    for _ in range(16):
        response = client.responses.create(
            model=deployment, instructions=instructions, input=history, tools=model_tools(),
            parallel_tool_calls=False, store=False, max_output_tokens=1800, timeout=180,
        )
        if response.status != "completed":
            raise RunbookError(f"Drafting response incomplete: {response.status}.")
        calls = [item for item in response.output if item.type == "function_call"]
        history.extend(item.model_dump(exclude_none=True) for item in response.output)
        if not calls:
            if not {"browser_navigate", "browser_fill_form", "browser_click"} <= set(browser.calls):
                raise RunbookError("No managed navigation/fill/save evidence; model text is not proof.")
            return
        if len(calls) != 1:
            raise RunbookError("Sequential browser calls required.")
        call = calls[0]
        arguments = json.loads(call.arguments)
        result = await draft_call(browser, call.name, arguments, site, run_id)
        history.append({
            "type": "function_call_output", "call_id": call.call_id, "output": canonical(result),
        })
    raise RunbookError("Drafting exceeded the bounded tool-call budget.")


def verify_draft(state, action, snapshot):
    expected = {**FIELDS, "formId": action["formId"]}
    text = canonical(snapshot)
    if (
        state.get("action") != action or state.get("draft") != expected
        or state.get("record") is not None or state.get("permit") is not None
        or time.time() * 1000 >= state.get("expires", 0)
        or not all(str(value) in text for value in (
            "SYNTHETIC ONLY", SESSION, action["formId"], *FIELDS.values(),
        ))
    ):
        raise RunbookError("Actual page/form and server draft do not match the exact action.")


async def prepare(browser, fixture, model_client, deployment, action):
    await draft(model_client, deployment, browser, fixture.site, fixture.run_id)
    snapshot = await browser.call("browser_snapshot")
    state = fixture.request("state")
    verify_draft(state, action, snapshot)
    # Exercise the actual host gate, without sending an unapproved browser action.
    try:
        await draft_call(browser, "browser_click", {"target": "#submit"}, fixture.site, fixture.run_id)
    except RunbookError:
        print("Host gate rejected unapproved Submit BEFORE remote tool execution.")
    else:
        raise RunbookError("Host approval gate unexpectedly allowed an unapproved Submit.")
    rejected_page = await browser.call("browser_snapshot")
    rejected = fixture.request("state")
    verify_draft(rejected, action, rejected_page)
    if (
        rejected.get("rejected") != state.get("rejected", 0)
    ):
        raise RunbookError("Unexpected server submission attempt during unapproved drafting.")
    print("Verified: actual filled draft; unapproved Submit not executed; no registration record.")


async def submit(browser, fixture, action, approval):
    if not secrets.compare_digest(approval, digest(action)):
        raise ValueError("Approval does not match this run's exact action.")
    await browser.call("browser_navigate", url=f"{fixture.site}/{fixture.run_id}/form")
    snapshot = await browser.call("browser_snapshot")
    verify_draft(fixture.request("state"), action, snapshot)
    fixture.request("approve", "POST", {"digest": approval})
    # Never retry this call, including after an ambiguous timeout.
    await browser.call("browser_click", target="#submit")
    receipt = await browser.call("browser_snapshot")
    state = fixture.request("state")
    record = state.get("record")
    if (
        not record or record.get("action") != action or not record.get("id")
        or state.get("permit") is not None
        or record["id"] not in canonical(receipt) or "Registration recorded" not in canonical(receipt)
    ):
        raise RunbookError("Submission not proven by both the actual receipt page and stored record.")
    print(f"Verified synthetic registration record: {record['id']} (single-use permit consumed).")


def save_pending(value):
    STATE.mkdir(mode=0o700, parents=True, exist_ok=True)
    path = STATE / f"pending-{value['action']['runId']}.json"
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(value, stream)
    return path


def read_pending(run_id):
    if not re.fullmatch(r"[a-f0-9]{32}", run_id or ""):
        raise ValueError("--run-id must be the displayed 32-character run ID.")
    path = STATE / f"pending-{run_id}.json"
    value = json.loads(path.read_text(encoding="utf-8"))
    return path, value


async def execute(args, project, credential, http):
    site = args.site_endpoint
    if args.run_id:
        path, pending = read_pending(args.run_id)
        action = pending["action"]
        if action != expected_action(site, args.run_id, action["formId"]):
            raise ValueError("Saved exact action differs from the configured site/run.")
        if pending.get("cleanupOnly") and not args.cleanup:
            raise ValueError("A prior attempt consumed this run; only --cleanup is permitted.")
        if args.approve_action and not secrets.compare_digest(args.approve_action, digest(action)):
            raise ValueError("Exact-action approval mismatch; no permit granted.")
        if pending["projectEndpoint"] != args.project_endpoint or pending["connection"] != args.connection:
            raise ValueError("Saved run belongs to a different project/connection.")
        fixture = Fixture(http, site, args.run_id, pending["control"])
        # Atomic claim: a concurrent process or later CLI replay cannot use this pending run.
        claimed = path.with_name(f"claimed-{args.run_id}-{uuid4().hex}.json")
        path.rename(claimed)
        try:
            if not args.cleanup:
                async with managed_browser(project, credential, args.connection, args.project_endpoint) as browser:
                    await submit(browser, fixture, action, args.approve_action)
        finally:
            try:
                fixture.request("delete", "DELETE")
            except Exception:
                pending["cleanupOnly"] = True
                claimed.write_text(json.dumps(pending), encoding="utf-8")
                claimed.rename(path)
                print(f"Cleanup failed; capability retained. Run: python demo.py --run-id {args.run_id} --cleanup")
                raise
            else:
                claimed.unlink(missing_ok=True)
        print("Run state deleted.")
        return
    health = Fixture(http, site).request("health")
    if health != {"fixture": "browser-runbook-v1", "syntheticOnly": True, "site": site}:
        raise RunbookError("Controlled fixture identity/site preflight failed.")
    connection = project.connections.get(name=args.connection.rsplit("/", 1)[-1], include_credentials=False)
    if args.connection.startswith("/") and connection.id.lower() != args.connection.lower():
        raise ValueError("Browser connection does not belong to the configured project.")
    async with managed_browser(
        project, credential, connection.id, args.project_endpoint, preflight=args.verify_services,
    ) as browser:
        if args.verify_services:
            print("Service verification passed: fixture, connection, native Toolbox tools/parameter schemas.")
            return
        run_id, form_id, control = uuid4().hex, uuid4().hex, secrets.token_hex(32)
        fixture = Fixture(http, site, run_id, control)
        action = expected_action(site, run_id, form_id)
        retain = False
        try:
            created = fixture.request("create", "PUT", {
                "formId": form_id, "controlHash": hashlib.sha256(control.encode()).hexdigest(),
            })
            if created["action"] != action:
                raise RunbookError("Server exact action differs from the requested action.")
            with project.get_openai_client(max_retries=0) as model_client:
                await prepare(browser, fixture, model_client, args.deployment, action)
            save_pending({
                "action": action, "control": control, "expires": created["expires"],
                "projectEndpoint": args.project_endpoint, "connection": args.connection,
            })
            retain = True
            print("Review this exact action:\n" + json.dumps(action, indent=2))
            print(
                "No approval granted. Pending run expires in 30 minutes.\n"
                f"Approve once: python demo.py --run-id {run_id} --approve-action {digest(action)}\n"
                f"Or delete: python demo.py --run-id {run_id} --cleanup"
            )
        finally:
            if not retain:
                try:
                    fixture.request("delete", "DELETE")
                except Exception:
                    save_pending({
                        "action": action, "control": control, "cleanupOnly": True,
                        "projectEndpoint": args.project_endpoint, "connection": args.connection,
                    })
                    print(f"Cleanup failed. Run: python demo.py --run-id {run_id} --cleanup")
                    raise


def configuration(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    outputs_file = STATE / "deployment.json"
    parser.add_argument("--project-endpoint", default=os.getenv("FOUNDRY_PROJECT_ENDPOINT"))
    parser.add_argument("--deployment", default=os.getenv("AZURE_OPENAI_DEPLOYMENT"))
    parser.add_argument("--site-endpoint", default=os.getenv("WORKSHOP_TOOL_ENDPOINT"))
    parser.add_argument("--connection", default=(
        os.getenv("BROWSER_AUTOMATION_PROJECT_CONNECTION_ID")
        or os.getenv("BROWSER_AUTOMATION_CONNECTION_NAME")
    ), help="Existing project connection resource ID, or the name to create manually after deployment.")
    checks = parser.add_mutually_exclusive_group()
    checks.add_argument("--preflight", action="store_true", help="Configuration-only; no Azure requests or existing resources required.")
    checks.add_argument("--verify-services", action="store_true", help="Post-deployment fixture/connection/Toolbox check; no browser or model call.")
    parser.add_argument("--run-id")
    decision = parser.add_mutually_exclusive_group()
    decision.add_argument("--approve-action", help="Exact displayed SHA-256; never inferred from natural language.")
    decision.add_argument("--cleanup", action="store_true")
    args = parser.parse_args(argv)
    if not args.preflight and not all((args.project_endpoint, args.deployment, args.site_endpoint)):
        if outputs_file.exists():
            try:
                outputs = json.loads(outputs_file.read_text(encoding="utf-8"))["outputs"]
                if not isinstance(outputs, dict):
                    raise ValueError("outputs must be an object")
                for attribute, key in (
                    ("project_endpoint", "projectEndpoint"),
                    ("deployment", "deployment"),
                    ("site_endpoint", "toolEndpoint"),
                ):
                    if not getattr(args, attribute):
                        output = outputs.get(key, {})
                        if not isinstance(output, dict):
                            raise ValueError(f"{key} must be an output object")
                        value = output.get("value")
                        if value is not None and not isinstance(value, str):
                            raise ValueError(f"{key} output must be a string")
                        setattr(args, attribute, value)
            except (OSError, UnicodeError, ValueError, TypeError, KeyError) as error:
                parser.error(f"Cannot read deployment outputs from {outputs_file}: {error}")
    try:
        if not args.connection or not (
            re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}", args.connection)
            or re.fullmatch(
                r"/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft.CognitiveServices/"
                r"accounts/[^/]+/projects/[^/]+/connections/[^/]+", args.connection,
            )
        ):
            raise ValueError(
                "Set BROWSER_AUTOMATION_CONNECTION_NAME (create it manually after deployment), "
                "or BROWSER_AUTOMATION_PROJECT_CONNECTION_ID, or --connection."
            )
        if args.preflight:
            if args.run_id or args.approve_action or args.cleanup:
                raise ValueError("--preflight is configuration-only; it cannot operate on a run.")
            return args
        args.project_endpoint = endpoint(args.project_endpoint, urlsplit(args.project_endpoint or "").path)
        if not re.fullmatch(r"/api/projects/[^/]+", urlsplit(args.project_endpoint).path):
            raise ValueError("Project endpoint must have path /api/projects/{project-name}.")
        args.site_endpoint = endpoint(args.site_endpoint, "/api/runbook")
        if not args.deployment:
            raise ValueError("Set deployment from the deployment output.")
        if bool(args.run_id) != bool(args.approve_action or args.cleanup) or (args.verify_services and args.run_id):
            raise ValueError("Use --run-id with exactly one of --approve-action or --cleanup.")
    except ValueError as error:
        parser.error(str(error))
    return args


def main(argv=None):
    args = configuration(argv)
    if args.preflight:
        print(
            "Configuration preflight passed; no Azure requests made. "
            "After deployment, manually create the configured Browser Automation connection "
            "using project managed identity, confirm remote MCP preview access, "
            "then run --verify-services. Provisioning is not fully automatic."
        )
        return
    with (
        AzureCliCredential() as credential,
        AIProjectClient(endpoint=args.project_endpoint, credential=credential) as project,
        httpx.Client(timeout=30, follow_redirects=False) as http,
    ):
        asyncio.run(execute(args, project, credential, http))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Transport exception strings may contain private browser/session URLs.
        if isinstance(error, RunbookError):
            print(str(error), file=sys.stderr)
        print(f"Run failed ({type(error).__name__}); no success claimed. Inspect service state; do not retry submission.", file=sys.stderr)
        raise SystemExit(1) from None
