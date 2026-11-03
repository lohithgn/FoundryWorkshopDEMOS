"""Verify a managed Copilot harness, immutable native skill, and native Toolbox search."""

import argparse
import asyncio
from contextlib import ExitStack
import io
import json
import os
from pathlib import Path
import subprocess
from uuid import uuid4
import zipfile

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    CreateSkillVersionFromFilesBody, MCPToolboxTool, MicrosoftCopilotHarness,
    MicrosoftCopilotToolsetConfig, MicrosoftCopilotToolsetDefaultConfig,
    MicrosoftCopilotToolsetPreview, PromptAgentDefinition, SkillReference,
    ToolSearchToolboxTool,
)
from azure.identity import AzureCliCredential
from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

ROOT = Path(__file__).resolve().parent
STATE = ROOT / ".azure"
SOURCE = "https://learn.microsoft.com/api/mcp"


def settings() -> dict:
    path = STATE / "deployment.json"
    saved = json.loads(path.read_text()).get("outputs", {}) if path.exists() else {}
    config = {key: value["value"] for key, value in saved.items()}
    for output, variable in (
        ("endpoint", "AZURE_OPENAI_ENDPOINT"), ("projectEndpoint", "FOUNDRY_PROJECT_ENDPOINT"),
        ("deployment", "AZURE_OPENAI_DEPLOYMENT"), ("projectResourceId", "FOUNDRY_PROJECT_RESOURCE_ID"),
    ):
        if os.getenv(variable):
            config[output] = os.environ[variable]
    for key in ("endpoint", "projectEndpoint", "deployment", "projectResourceId"):
        if not config.get(key):
            raise RuntimeError(f"BLOCKED: missing {key}; deploy with python3 deploy.py --demo 42 --location swedencentral")
    return config


def save(report: dict) -> None:
    STATE.mkdir(exist_ok=True)
    pending = STATE / "live-report.pending"
    pending.write_text(json.dumps(report, indent=2, default=str) + "\n")
    pending.replace(STATE / "live-report.json")


def skill_package(name: str = "workshop-brief") -> bytes:
    data = io.BytesIO()
    with zipfile.ZipFile(data, "w", zipfile.ZIP_DEFLATED) as package:
        text = (ROOT / "skills/workshop-brief/SKILL.md").read_text()
        package.writestr("SKILL.md", text.replace("name: workshop-brief", "name: " + name))
    return data.getvalue()


def verify_definition(definition: dict, skill_name: str, version: str) -> None:
    if definition.get("harness", {}).get("type") != "microsoft_copilot_preview":
        raise RuntimeError("Persisted agent does not use the native Microsoft Copilot harness.")
    if not any(s.get("name") == skill_name and str(s.get("version")) == version
               for s in definition.get("skills", [])):
        raise RuntimeError("Persisted agent is missing its immutable native skill reference.")


def verify_meta_tools(names: set[str]) -> None:
    if names != {"tool_search", "call_tool"}:
        raise RuntimeError(f"Native meta-tools must hide unpinned tools; received {sorted(names)}")


def object_content(result) -> list[dict]:
    if result.isError:
        raise RuntimeError(f"Native MCP operation failed: {result.model_dump()}")
    if result.structuredContent:
        return [result.structuredContent]
    objects = []
    for item in result.content:
        if getattr(item, "type", "") == "text":
            try:
                parsed = json.loads(item.text)
                objects.extend(parsed if isinstance(parsed, list) else [parsed])
            except json.JSONDecodeError:
                continue
    return objects


def find_tool(objects: list[dict]) -> dict:
    def walk(value):
        if isinstance(value, dict):
            name = value.get("name", value.get("tool_name", ""))
            if isinstance(name, str) and "microsoft_docs_search" in name:
                return value
            for child in value.values():
                result = walk(child)
                if result:
                    return result
        if isinstance(value, list):
            for child in value:
                result = walk(child)
                if result:
                    return result
        return None
    result = walk(objects)
    if not result:
        raise RuntimeError("Native search did not discover the read-only Learn docs search tool.")
    return result


async def toolbox_search(project, credential, endpoint: str, report: dict) -> None:
    name = "native-search-" + uuid4().hex[:10]
    toolbox = project.toolboxes.create_version(
        name=name, description="Read-only official Learn connector with native deferred discovery.",
        tools=[
            MCPToolboxTool(server_label="learn", server_url=SOURCE, require_approval="never"),
            ToolSearchToolboxTool(),
        ],
    )
    report["toolbox"] = toolbox.as_dict()
    save(report)
    try:
        readback = project.toolboxes.get_version(name=name, version=toolbox.version).as_dict()
        if not any(t.get("type") == "toolbox_search" for t in readback["tools"]):
            raise RuntimeError("Service read-back lacks the native toolbox_search definition.")
        url = f"{endpoint.rstrip('/')}/toolboxes/{name}/versions/{toolbox.version}/mcp?api-version=v1"
        headers = {"Authorization": "Bearer " + credential.get_token("https://ai.azure.com/.default").token}
        async with streamablehttp_client(url, headers=headers) as (read, write, _):
            async with ClientSession(read, write) as session:
                await session.initialize()
                listing = await session.list_tools()
                verify_meta_tools({t.name for t in listing.tools})
                report["initial_tools"] = listing.model_dump()
                search_schema = next(t.inputSchema for t in listing.tools if t.name == "tool_search")
                report["search_schema"] = search_schema
                properties = search_schema.get("properties", {})
                query_key = "query" if "query" in properties else "search_query"
                if query_key not in properties:
                    raise RuntimeError(f"Unsupported native search schema: {search_schema}")
                result = await session.call_tool(
                    "tool_search", {query_key: "microsoft_docs_search official Foundry Copilot harness documentation"},
                )
                report["tool_search"] = result.model_dump()
                tool = find_tool(object_content(result))
                call_schema = next(t.inputSchema for t in listing.tools if t.name == "call_tool")
                props = call_schema.get("properties", {})
                name_key = "tool_name" if "tool_name" in props else "name"
                args_key = "arguments" if "arguments" in props else "tool_arguments"
                if name_key not in props or args_key not in props:
                    raise RuntimeError(f"Unsupported native call_tool schema: {call_schema}")
                discovered_name = tool.get("name", tool.get("tool_name"))
                called = await session.call_tool(
                    "call_tool", {name_key: discovered_name, args_key: {"query": "Microsoft Foundry Copilot harness"}},
                )
                if called.isError or not called.content:
                    raise RuntimeError("Native call_tool did not return official documentation.")
                report["call_tool"] = called.model_dump()
                report["tool_search_status"] = "PASS"
    finally:
        project.toolboxes.delete(name=name)
        report["toolbox_deleted"] = True
        save(report)


def managed_harness(project, config: dict, report: dict) -> None:
    suffix = uuid4().hex[:10]
    name, skill_name = "native-harness-" + suffix, "workshop-brief-" + suffix
    with ExitStack() as cleanup:
        skill = project.beta.skills.create_from_files(
            name=skill_name,
            content=CreateSkillVersionFromFilesBody(files=[("skill.zip", io.BytesIO(skill_package(skill_name)))]),
        )
        cleanup.callback(project.beta.skills.delete, name=skill_name)
        agent = project.agents.create_version(
            agent_name=name,
            definition=PromptAgentDefinition(
                model=config["deployment"],
                instructions="Use the workshop-brief skill for a synthetic workshop brief about native tool discovery.",
                harness=MicrosoftCopilotHarness(),
                skills=[SkillReference(name=skill_name, version=skill.version)],
                tools=[MicrosoftCopilotToolsetPreview(
                    default_config=MicrosoftCopilotToolsetDefaultConfig(enabled=False),
                    configs=[MicrosoftCopilotToolsetConfig(name="filesystem_read", enabled=True)],
                )],
            ),
        )
        cleanup.callback(project.agents.delete, agent_name=name)
        persisted = project.agents.get_version(agent_name=name, agent_version=agent.version).as_dict()
        verify_definition(persisted["definition"], skill_name, str(skill.version))
        report["agent"] = persisted
        principal = persisted.get("instance_identity", {}).get("principal_id")
        if not principal:
            principal = project.agents.get(name).as_dict().get("instance_identity", {}).get("principal_id")
        if not principal:
            raise RuntimeError("Native harness agent has no instance identity.")
        role_id = "53ca6127-db72-4b80-b1b0-d745d6d5456d"
        assigned = subprocess.run([
            "az", "role", "assignment", "create", "--assignee-object-id", principal,
            "--assignee-principal-type", "ServicePrincipal", "--role", role_id,
            "--scope", config["projectResourceId"], "-o", "json",
        ], check=True, capture_output=True, text=True)
        role = json.loads(assigned.stdout)
        cleanup.callback(subprocess.run, [
            "az", "role", "assignment", "delete", "--ids", role["id"], "-o", "none",
        ], check=True)
        report["agent_identity"] = principal
        with project.get_openai_client(agent_name=name, timeout=180, max_retries=0) as ai:
            conversation = ai.conversations.create()
            try:
                response = ai.responses.create(
                    conversation=conversation.id,
                    input="Create a synthetic workshop brief about native tool discovery using the workshop-brief skill.",
                )
                report["response"] = response.model_dump()
                if response.status != "completed" or "NATIVE-SKILL-42" not in response.output_text:
                    raise RuntimeError("Native harness response did not demonstrate the pinned skill.")
            finally:
                ai.conversations.delete(conversation_id=conversation.id)
        report["harness_status"] = "PASS"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight", action="store_true")
    args = parser.parse_args(argv)
    report = {"status": "BLOCKED", "harness_status": "NOT_RUN", "tool_search_status": "NOT_RUN", "blockers": []}
    try:
        config = settings()
        with AzureCliCredential() as credential, AIProjectClient(
            config["projectEndpoint"], credential, allow_preview=True,
            connection_timeout=30, read_timeout=180, retry_total=0,
        ) as project:
            if args.preflight:
                credential.get_token("https://ai.azure.com/.default")
                if config["deployment"] not in {d.name for d in project.deployments.list()}:
                    raise RuntimeError("The configured native harness model deployment is missing.")
                print("PASS preflight: Entra identity, project access, current native SDK.")
                return 0
            for feature, run in (
                ("tool_search", lambda: asyncio.run(asyncio.wait_for(
                    toolbox_search(project, credential, config["projectEndpoint"], report), timeout=180,
                ))),
                ("harness", lambda: managed_harness(project, config, report)),
            ):
                try:
                    run()
                except Exception as error:
                    report[feature + "_status"] = "BLOCKED"
                    report["blockers"].append({"feature": feature, "type": type(error).__name__, "message": str(error)})
                    save(report)
        report["status"] = "PASS" if not report["blockers"] else "BLOCKED"
    except Exception as error:
        report["blockers"].append({"feature": "preflight", "type": type(error).__name__, "message": str(error)})
    save(report)
    print(json.dumps({"status": report["status"], "harness": report["harness_status"],
                      "tool_search": report["tool_search_status"], "blockers": report["blockers"]}, default=str))
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
