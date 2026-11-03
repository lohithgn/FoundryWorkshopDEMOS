"""Dispatch a native one-time routine, verify identity and response linkage, and delete it."""

import argparse
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import subprocess
from time import monotonic, sleep
from uuid import uuid4

from azure.ai.projects import AIProjectClient
from azure.core.exceptions import HttpResponseError, ResourceNotFoundError
from azure.ai.projects.models import (
    AgentEndpointConfig, EntraAuthorizationScheme, FixedRatioVersionSelectionRule,
    PromptAgentDefinition, ProtocolConfiguration, VersionSelector,
)
from azure.identity import AzureCliCredential

ROOT = Path(__file__).resolve().parent
STATE = ROOT / ".azure"
WAIT_SECONDS = 360


def settings() -> dict:
    path = STATE / "deployment.json"
    saved = json.loads(path.read_text()).get("outputs", {}) if path.exists() else {}
    config = {key: value["value"] for key, value in saved.items()}
    for output, variable in (
        ("endpoint", "AZURE_OPENAI_ENDPOINT"), ("projectEndpoint", "FOUNDRY_PROJECT_ENDPOINT"),
        ("deployment", "AZURE_OPENAI_DEPLOYMENT"),
        ("projectResourceId", "FOUNDRY_PROJECT_RESOURCE_ID"),
    ):
        if os.getenv(variable):
            config[output] = os.environ[variable]
    for key in ("endpoint", "projectEndpoint", "deployment", "projectResourceId"):
        if not config.get(key):
            raise RuntimeError(f"BLOCKED: missing {key}; deploy with python3 deploy.py --demo 43")
    return config


def save(report: dict) -> None:
    STATE.mkdir(exist_ok=True)
    pending = STATE / "live-report.pending"
    pending.write_text(json.dumps(report, indent=2, default=str) + "\n")
    pending.replace(STATE / "live-report.json")


def routine_body(agent_name: str, conversation_id: str, timer: datetime,
                 dispatch_identity: str = "creator") -> dict:
    if dispatch_identity not in ("creator", "agent"):
        raise ValueError("Dispatch identity must explicitly be creator or agent.")
    return {
        "description": "Bounded synthetic one-time workshop timer; deleted on exit.",
        "enabled": False,
        "authorization": {"identity": dispatch_identity},
        "triggers": {"once": {"type": "timer", "at": timer.isoformat().replace("+00:00", "Z")}},
        "action": {"type": "invoke_agent_responses_api", "agent_name": agent_name,
                   "input": "Return the unique synthetic timer marker.", "conversation": conversation_id},
    }


def verify_routine(routine: dict, name: str, enabled: bool,
                   dispatch_identity: str = "creator") -> None:
    if routine.get("name") != name or routine.get("enabled") is not enabled:
        raise RuntimeError("Native routine name/enabled read-back mismatch.")
    if routine.get("authorization", {}).get("identity") != dispatch_identity:
        raise RuntimeError(f"Native routine did not persist the explicitly selected {dispatch_identity} dispatch identity.")
    triggers = list(routine.get("triggers", {}).values())
    if len(triggers) != 1 or triggers[0].get("type") != "timer":
        raise RuntimeError("Expected exactly one native one-shot timer trigger.")


def verify_run(run: dict) -> None:
    if run.get("phase") != "completed":
        raise RuntimeError(f"Native timer failed: {run}")
    if run.get("trigger_type") != "timer" or run.get("attempt_source") != "timer_delivery":
        raise RuntimeError("Run history does not prove an actual service timer delivery.")
    if not run.get("response_id"):
        raise RuntimeError("Completed routine run lacks its linked native response identifier.")


def verify_response(run: dict, response: dict, text: str, marker: str) -> None:
    if response.get("id") != run["response_id"] or response.get("status") != "completed":
        raise RuntimeError("Retrieved response is not the completed response linked by routine history.")
    conversation = response.get("conversation")
    if run.get("conversation_id") and isinstance(conversation, dict) and conversation.get("id") != run["conversation_id"]:
        raise RuntimeError("Routine history and response conversation differ.")
    if text.strip() != marker:
        raise RuntimeError("Native routine response does not contain the exact unique synthetic marker.")


def verify_completed_response(project, report: dict) -> None:
    final_run = next((r for r in report.get("run_history", []) if r.get("phase") == "completed"), None)
    if final_run is None:
        raise RuntimeError("No previously completed native timer run to verify.")
    verify_run(final_run)
    with project.get_openai_client(
        agent_name=report["agent"]["name"], timeout=90, max_retries=1,
    ) as ai:
        response = ai.responses.retrieve(final_run["response_id"])
        report["response"] = response.model_dump()
        verify_response(final_run, report["response"], response.output_text, report["marker"])
        report["native_response_trace"] = [item.model_dump() for item in response.output]
    report["timer_status"] = "PASS"
    save(report)


def identity_failure_evidence(error: HttpResponseError, report: dict) -> None:
    response = error.response
    report["service_failure"] = {
        "status_code": getattr(response, "status_code", None),
        "request_ids": {key: value for key, value in getattr(response, "headers", {}).items()
                        if key.lower() in ("x-ms-request-id", "apim-request-id", "x-request-id",
                                           "x-ms-correlation-request-id", "x-ms-error-code")},
    }
    report["identity_directory_readback"] = {}
    for kind in ("instance_identity", "blueprint"):
        identity = report.get("agent", {}).get(kind, {})
        client_id = identity.get("client_id")
        if not client_id:
            continue
        result = subprocess.run([
            "az", "ad", "sp", "show", "--id", client_id,
            "--query", "{id:id,appId:appId,accountEnabled:accountEnabled,servicePrincipalType:servicePrincipalType}",
            "-o", "json",
        ], capture_output=True, text=True, timeout=30)
        report["identity_directory_readback"][kind] = {
            "client_id": client_id, "returncode": result.returncode,
            "readback": json.loads(result.stdout) if result.returncode == 0 else None,
            "error": result.stderr.strip() if result.returncode else None,
        }
    save(report)


def demonstrate(project, config: dict, report: dict, wait_seconds: int = WAIT_SECONDS,
                dispatch_identity: str = "creator") -> None:
    suffix = uuid4().hex[:12]
    agent_name, routine_name = "routine-agent-" + suffix, "one-time-" + suffix
    marker = "NATIVE-TIMER-43-" + suffix
    report.update({"routine_name": routine_name, "marker": marker, "timer_status": "NOT_RUN",
                   "dispatch_identity": dispatch_identity,
                   "runtime_configuration": {k: config[k] for k in (
                       "endpoint", "projectEndpoint", "deployment", "projectResourceId") if k in config}})
    agent_created = False
    agent_attempted = False
    routine_attempted = False
    conversation_id = None
    try:
        agent_attempted = True
        agent = project.agents.create_version(
            agent_name=agent_name,
            definition=PromptAgentDefinition(
                model=config["deployment"],
                instructions=f"Reply to every input with exactly this literal text and nothing else: {marker}",
            ),
        )
        agent_created = True
        report["agent"] = agent.as_dict()
        project.agents.update_details(
            agent_name,
            agent_endpoint=AgentEndpointConfig(
                authorization_schemes=[EntraAuthorizationScheme()],
                protocol_configuration=ProtocolConfiguration(responses={}),
                version_selector=VersionSelector(version_selection_rules=[
                    FixedRatioVersionSelectionRule(agent_version=agent.version, traffic_percentage=100),
                ]),
            ),
        )
        details = project.agents.get(agent_name).as_dict()
        report["agent_endpoint"] = details
        schemes = details.get("agent_endpoint", {}).get("authorization_schemes", [])
        if not any(s.get("type") in ("Entra", "AzureRbac") for s in schemes):
            raise RuntimeError("Routine endpoint lacks the documented Entra/AzureRbac authorization scheme.")
        # Agent identity must be able to invoke the project even with creator dispatch selected.
        principal = agent.as_dict().get("instance_identity", {}).get("principal_id")
        if principal and config.get("projectResourceId"):
            assigned = subprocess.run([
                "az", "role", "assignment", "create", "--assignee-object-id", principal,
                "--assignee-principal-type", "ServicePrincipal",
                "--role", "53ca6127-db72-4b80-b1b0-d745d6d5456d",
                "--scope", config["projectResourceId"], "-o", "json",
            ], capture_output=True, text=True, check=True)
            report["agent_role"] = json.loads(assigned.stdout)
        save(report)
        with project.get_openai_client(agent_name=agent_name, timeout=90, max_retries=1) as ai:
            conversation_id = ai.conversations.create().id
        report["configured_conversation_id"] = conversation_id
        timer = datetime.now(timezone.utc) + timedelta(seconds=90)
        body = routine_body(agent_name, conversation_id, timer, dispatch_identity)
        report["submitted_routine_body"] = body
        save(report)
        routine_attempted = True
        routine = project.beta.routines.create_or_update(
            routine_name=routine_name, body=body,
        )
        report["created_routine"] = routine.as_dict()
        verify_routine(report["created_routine"], routine_name, False, dispatch_identity)
        save(report)
        report["lifecycle"] = []
        for enabled in (False, True, False, True):
            if enabled:
                project.beta.routines.enable(routine_name)
            else:
                project.beta.routines.disable(routine_name)
            persisted = project.beta.routines.get(routine_name).as_dict()
            verify_routine(persisted, routine_name, enabled, dispatch_identity)
            report["lifecycle"].append(persisted)
            save(report)
        deadline = monotonic() + wait_seconds
        final_run = None
        while monotonic() < deadline:
            history = [r.as_dict() for r in project.beta.routines.list_runs(routine_name, limit=5)]
            report["run_history"] = history
            save(report)
            terminal = [r for r in history if r.get("phase") in ("completed", "failed")]
            if terminal:
                final_run = terminal[0]
                break
            sleep(min(10, max(0, deadline - monotonic())))
        if final_run is None:
            raise TimeoutError(f"Native one-time timer produced no terminal run within {wait_seconds}s.")
        verify_run(final_run)
        verify_completed_response(project, report)
    except HttpResponseError as error:
        try:
            identity_failure_evidence(error, report)
        except Exception as diagnostic_error:
            report["identity_diagnostic_error"] = str(diagnostic_error)
        raise
    finally:
        cleanup_errors = []
        if routine_attempted:
            try:
                project.beta.routines.disable(routine_name)
            except ResourceNotFoundError:
                pass
            except Exception as error:
                cleanup_errors.append("disable: " + str(error))
            try:
                project.beta.routines.delete(routine_name)
                remaining = [r.name for r in project.beta.routines.list()]
                if routine_name in remaining:
                    raise RuntimeError("Routine still listed after deletion.")
                report["routine_deleted"] = True
            except ResourceNotFoundError:
                report["routine_deleted"] = True
            except Exception as error:
                cleanup_errors.append("delete: " + str(error))
        if conversation_id and agent_created:
            try:
                with project.get_openai_client(agent_name=agent_name, timeout=60, max_retries=1) as ai:
                    ai.conversations.delete(conversation_id)
                report["conversation_deleted"] = True
            except Exception as error:
                cleanup_errors.append("conversation delete: " + str(error))
        if agent_attempted:
            try:
                project.agents.delete(agent_name=agent_name)
                report["agent_deleted"] = True
            except ResourceNotFoundError:
                report["agent_deleted"] = True
            except Exception as error:
                cleanup_errors.append("agent delete: " + str(error))
        if report.get("agent_role"):
            try:
                subprocess.run(["az", "role", "assignment", "delete", "--ids", report["agent_role"]["id"],
                                "-o", "none"], check=True, capture_output=True, text=True)
                report["agent_role_deleted"] = True
            except Exception as error:
                cleanup_errors.append("agent role delete: " + str(error))
        report["cleanup_errors"] = cleanup_errors
        save(report)
        if cleanup_errors:
            raise RuntimeError(f"BLOCKED cleanup requires attention: {cleanup_errors}")


def argument_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight", action="store_true")
    parser.add_argument("--dispatch-identity", choices=("creator", "agent"), default="creator",
                        help="Native dispatch identity; creator is the root/standalone default. No fallback.")
    return parser


def main(argv=None) -> int:
    args = argument_parser().parse_args(argv)
    report = {"status": "BLOCKED", "timer_status": "NOT_RUN", "blockers": [],
              "dispatch_identity": args.dispatch_identity}
    try:
        config = settings()
        if not args.preflight and config.get("projectResourceId"):
            readiness = subprocess.run([
                "az", "resource", "show", "--ids", config["projectResourceId"],
                "--query", "{provisioningState:properties.provisioningState,identity:identity}",
                "-o", "json",
            ], capture_output=True, text=True, check=True)
            report["arm_project_readback"] = json.loads(readiness.stdout)
            save(report)
            if report["arm_project_readback"].get("provisioningState") != "Succeeded":
                raise RuntimeError("BLOCKED: ARM project has not completed provisioning; no routine submitted.")
        with AzureCliCredential() as credential, AIProjectClient(
            config["projectEndpoint"], credential, allow_preview=True,
            connection_timeout=30, read_timeout=90, retry_total=0,
        ) as project:
            if args.preflight:
                credential.get_token("https://ai.azure.com/.default")
                if config["deployment"] not in {d.name for d in project.deployments.list()}:
                    raise RuntimeError("The configured native routine model deployment is missing.")
                list(project.beta.routines.list())
                print("PASS preflight: native routines endpoint and Entra authorization.")
                return 0
            demonstrate(project, config, report, dispatch_identity=args.dispatch_identity)
        report["status"] = "PASS"
    except Exception as error:
        report["blockers"].append({"type": type(error).__name__, "message": str(error)})
        report["timer_status"] = "BLOCKED"
    save(report)
    print(json.dumps({"status": report["status"], "timer": report["timer_status"],
                      "routine_deleted": report.get("routine_deleted"), "blockers": report["blockers"]}))
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
