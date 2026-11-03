"""Real two-project inventory, external registration, telemetry and scoped policy."""
import argparse
import json
import logging
import os
from pathlib import Path
import re
from time import monotonic, sleep
from uuid import uuid4

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import ExternalAgentDefinition, PromptAgentDefinition
from azure.identity import AzureCliCredential
from azure.monitor.opentelemetry.exporter import AzureMonitorTraceExporter
import httpx
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.trace import SpanKind, set_tracer_provider

HERE = Path(__file__).resolve().parent
STATE = HERE / ".azure"
DATA = json.loads((HERE / "data/fleet.json").read_text())
OUTPUT_ENV = {
    "endpoint": "AZURE_OPENAI_ENDPOINT", "deployment": "AZURE_OPENAI_DEPLOYMENT",
    "projectEndpoint": "FOUNDRY_PROJECT_ENDPOINT",
    "projectResourceId": "FOUNDRY_PROJECT_RESOURCE_ID",
    "secondProjectEndpoint": "FOUNDRY_SECOND_PROJECT_ENDPOINT",
    "secondProjectResourceId": "FOUNDRY_SECOND_PROJECT_RESOURCE_ID",
    "applicationInsightsResourceId": "APPLICATION_INSIGHTS_RESOURCE_ID",
    "workspaceId": "LOG_ANALYTICS_WORKSPACE_ID",
    "policyAssignmentId": "FLEET_POLICY_ASSIGNMENT_ID",
    "tenantId": "AZURE_TENANT_ID",
}
RG = "rg-foundry-fleet-governance-demo"
ARM = "https://management.azure.com"
POLICY_DEFINITION = "/providers/Microsoft.Authorization/policyDefinitions/71ef260a-8f18-47b7-abcb-62d0673d94dc"


class Blocked(RuntimeError):
    """A feature-specific live check cannot complete."""


def configuration() -> dict:
    path = STATE / "deployment.json"
    recorded = json.loads(path.read_text()) if path.exists() else {}
    saved = recorded.get("outputs", {})
    values = {key: os.environ.get(env, saved.get(key, {}).get("value", ""))
              for key, env in OUTPUT_ENV.items()}
    missing = [OUTPUT_ENV[key] for key, value in values.items() if not value]
    if missing:
        raise Blocked("Missing deployment outputs: " + ", ".join(missing))
    group = recorded.get("resourceGroup", RG)
    if not re.fullmatch(r"[a-zA-Z0-9_-]{1,90}", group) or not group.endswith("-fleet-governance-demo"):
        raise ValueError("Only a recorded synthetic fleet resource group may be mutated.")
    for key in ("projectResourceId", "secondProjectResourceId", "applicationInsightsResourceId",
                "policyAssignmentId"):
        if f"/resourceGroups/{group}/".lower() not in values[key].lower():
            raise ValueError("Only designated synthetic resource-group resources may be mutated.")
    for key in ("endpoint", "projectEndpoint", "secondProjectEndpoint"):
        if not values[key].startswith("https://"):
            raise ValueError(f"{key} must be HTTPS.")
        values[key] = values[key].rstrip("/")
    values["resourceGroup"] = group
    return values


def save(name: str, value: dict) -> None:
    STATE.mkdir(exist_ok=True)
    (STATE / name).write_text(json.dumps(value, indent=2) + "\n")


def safe_error(error: Exception) -> str:
    if isinstance(error, (Blocked, ValueError)):
        return str(error)
    if "Remote end closed connection without response" in str(error):
        return f"{type(error).__name__}; remote end closed connection without HTTP response"
    codes = sorted(set(re.findall(r"AADSTS\d+", str(error))))
    if codes:
        return f"{type(error).__name__}; Entra codes={','.join(codes)}; token not issued"
    status = getattr(error, "status_code", None)
    response = getattr(error, "response", None)
    request_id = response.headers.get("x-ms-request-id", "unavailable") if response is not None else "unavailable"
    return f"{type(error).__name__}; HTTP {status}; requestId={request_id}; body omitted"


class API:
    def __init__(self, credential, http: httpx.Client):
        self.credential, self.http = credential, http

    def request(self, method: str, url: str, scope: str = ARM + "/.default", **kwargs) -> dict:
        response = self.http.request(method, url, headers={
            "Authorization": "Bearer " + self.credential.get_token(scope).token,
        }, **kwargs)
        if not response.is_success:
            try:
                code = (response.json().get("error") or {}).get("code", "unknown")
            except (ValueError, AttributeError):
                code = "unknown"
            raise Blocked(f"{method} {url}: HTTP {response.status_code}, code={code}, "
                          f"requestId={response.headers.get('x-ms-request-id', 'unavailable')}")
        return response.json() if response.content else {}


def fleet_inventory(c: dict, credential) -> list[dict]:
    inventory = []
    for endpoint, resource_id in (
        (c["projectEndpoint"], c["projectResourceId"]),
        (c["secondProjectEndpoint"], c["secondProjectResourceId"]),
    ):
        with AIProjectClient(endpoint, credential, allow_preview=True) as project:
            agents = list(project.agents.list())
            connections = list(project.connections.list())
            inventory.append({
                "projectResourceId": resource_id, "projectEndpoint": endpoint,
                "agents": [{"name": agent.name, "id": agent.id,
                            "kind": str(agent.versions.latest.definition.kind),
                            "telemetryId": getattr(agent.versions.latest.definition,
                                                   "otel_agent_id", agent.id)}
                           for agent in agents],
                # No connection credentials, targets or metadata are logged.
                "connections": [{"name": connection.name, "id": connection.id,
                                 "type": str(connection.type)} for connection in connections],
            })
    return inventory


def setup_agents(c: dict, credential) -> list[dict]:
    saved = []
    for index, (endpoint, resource_id) in enumerate((
        (c["projectEndpoint"], c["projectResourceId"]),
        (c["secondProjectEndpoint"], c["secondProjectResourceId"]),
    )):
        with AIProjectClient(endpoint, credential, allow_preview=True) as project:
            name = DATA["agents"][index]
            existing = next((a for a in project.agents.list() if a.name == name), None)
            if existing:
                agent = existing.versions.latest
            else:
                agent = project.agents.create_version(
                    agent_name=name, definition=PromptAgentDefinition(
                        model=c["deployment"],
                        instructions="SYNTHETIC WORKSHOP DATA. The fictional Helios fleet "
                                     "maintenance interval is 36 months. Answer briefly.",
                    ), description="Synthetic fleet governance workshop prompt agent.",
                    metadata={"workshop": "46", "data": "synthetic"},
                )
            readback = project.agents.get_version(agent.name, agent.version)
            if readback.definition.model != c["deployment"]:
                raise RuntimeError("Native agent model readback mismatch.")
            logical = existing or next(a for a in project.agents.list() if a.name == name)
            saved.append({"name": agent.name, "version": agent.version, "id": logical.id,
                          "versionId": agent.id,
                          "projectEndpoint": endpoint, "projectResourceId": resource_id})
    save("agents.json", {"agents": saved})
    return saved


def register_external(c: dict, credential) -> dict:
    with AIProjectClient(c["projectEndpoint"], credential, allow_preview=True) as project:
        existing = next((a for a in project.agents.list() if a.name == DATA["externalAgent"]), None)
        if existing:
            registered = existing.versions.latest
        else:
            registered = project.agents.create_version(
                agent_name=DATA["externalAgent"],
                definition=ExternalAgentDefinition(otel_agent_id=DATA["externalTelemetryId"]),
                description="Synthetic external Python runtime; registration is not runtime hosting.",
            )
        readback = project.agents.get_version(registered.name, registered.version)
        if readback.definition.otel_agent_id != DATA["externalTelemetryId"]:
            raise RuntimeError("Native external telemetry ID readback mismatch.")
        result = {"status": "PASS", "name": registered.name, "version": registered.version,
                  "otelAgentId": readback.definition.otel_agent_id,
                  "projectResourceId": c["projectResourceId"]}
        save("external-agent.json", result)
        return result


def external_runtime(question: str) -> str:
    if "Helios" not in question:
        return "This synthetic runtime only answers fictional Helios fleet questions."
    return "36 months"


def correlate(inventory: list[dict], rows: list[dict], run_id: str) -> list[dict]:
    counts = {}
    expected_ids = {(p["projectResourceId"], a["name"]): a.get("telemetryId")
                    for p in inventory for a in p["agents"]}
    for row in rows:
        if row.get("RunId") != run_id:
            continue
        key = (row["ProjectId"], row["AgentName"])
        if expected_ids.get(key) and row.get("OtelAgentId") != expected_ids[key]:
            continue
        counts[key] = counts.get(key, 0) + int(row["Traces"])
    return [{"projectResourceId": project["projectResourceId"], "agent": agent["name"],
             "kind": agent["kind"], "telemetryId": agent.get("telemetryId"),
             "traceCount": counts.get(
                 (project["projectResourceId"], agent["name"]), 0)}
            for project in inventory for agent in project["agents"]]


def telemetry_query(run_id: str) -> str:
    # Caller controls only a generated UUID, never arbitrary KQL.
    from uuid import UUID
    run_id = str(UUID(run_id))
    return f"""union isfuzzy=true AppDependencies, AppRequests, AppTraces
| where TimeGenerated > ago(1h)
| extend RunId=tostring(Properties["workshop.run_id"]),
         ProjectId=tostring(Properties["workshop.project_id"]),
         AgentName=tostring(Properties["gen_ai.agent.name"]),
         OtelAgentId=tostring(Properties["gen_ai.agent.id"])
| where RunId == "{run_id}"
| summarize Traces=count() by RunId, ProjectId, AgentName, OtelAgentId"""


def telemetry_smoke(api: API, c: dict, agents: list[dict], external: dict | None,
                    timeout: int) -> dict:
    insights = api.request("GET", ARM + c["applicationInsightsResourceId"],
                           params={"api-version": "2020-02-02"})
    if not insights["properties"].get("DisableLocalAuth"):
        raise RuntimeError("Application Insights must enforce Entra ingestion.")
    connection_string = insights["properties"]["ConnectionString"]
    # Instrumentation key is routing configuration, not an authorization credential.
    # Disable disk retry storage: no stored tokens and no exporter scratch outside this lab.
    exporter = AzureMonitorTraceExporter(connection_string=connection_string,
                                         credential=api.credential, disable_offline_storage=True)
    provider = TracerProvider(resource=Resource.create({"service.name": "synthetic-fleet-46"}))
    set_tracer_provider(provider)
    provider.add_span_processor(SimpleSpanProcessor(exporter))
    tracer = provider.get_tracer("workshop.fleet")
    run_id = str(uuid4())
    invocations = []
    try:
        if external:
            with tracer.start_as_current_span("invoke_agent " + external["name"], kind=SpanKind.SERVER,
                attributes={
                    "gen_ai.agent.id": external["otelAgentId"], "gen_ai.agent.name": external["name"],
                    "workshop.project_id": external["projectResourceId"], "workshop.run_id": run_id,
                    "gen_ai.operation.name": "invoke_agent",
                }):
                answer = external_runtime(DATA["question"])
                if answer != DATA["expectedAnswer"]:
                    raise RuntimeError("External synthetic runtime returned an unexpected answer.")
                invocations.append({"agent": external["name"], "status": "completed",
                                    "runtime": "actual local Python function, not hosted by registration"})
        for agent in agents:
            try:
                with tracer.start_as_current_span("invoke_agent " + agent["name"], kind=SpanKind.CLIENT,
                    attributes={
                        "gen_ai.agent.id": agent["id"], "gen_ai.agent.name": agent["name"],
                        "workshop.project_id": agent["projectResourceId"], "workshop.run_id": run_id,
                        "gen_ai.operation.name": "invoke_agent",
                    }):
                    with AIProjectClient(agent["projectEndpoint"], api.credential, allow_preview=True) as project:
                        with project.get_openai_client(agent_name=agent["name"]) as openai:
                            response = openai.with_options(max_retries=0).responses.create(
                                input=DATA["question"], max_output_tokens=128, timeout=120,
                            )
                    if response.status != "completed" or DATA["expectedAnswer"] not in response.output_text:
                        raise Blocked(f"Real invocation didn't return expected answer: {agent['name']}")
                    invocations.append({"agent": agent["name"], "responseId": response.id,
                                        "status": response.status})
            except Exception as error:
                invocations.append({"agent": agent["name"], "status": "failed",
                                    "evidence": safe_error(error)})
        provider.force_flush()
    finally:
        provider.shutdown()
        save("invocations.json", {"runId": run_id, "invocations": invocations})
    deadline = monotonic() + timeout
    rows = []
    while True:
        try:
            payload = api.request("POST",
                f"https://api.loganalytics.azure.com/v1/workspaces/{c['workspaceId']}/query",
                "https://api.loganalytics.io/.default", json={"query": telemetry_query(run_id)})
        except Blocked as error:
            if "BadArgumentError" in str(error) and monotonic() < deadline:
                sleep(15)
                continue
            raise
        rows = [dict(zip([col["name"] for col in table["columns"]], row))
                for table in payload.get("tables", []) for row in table["rows"]]
        if len({(r["ProjectId"], r["AgentName"]) for r in rows}) >= len(invocations):
            break
        if monotonic() >= deadline:
            raise Blocked(f"Azure Monitor correlation timeout: observed {len(rows)} rows for "
                          f"{len(invocations)} real invocations; runId={run_id}")
        sleep(15)
    inventory = fleet_inventory(c, api.credential)
    correlated = correlate(inventory, rows, run_id)
    expected = {(a["projectResourceId"], a["name"]) for a in agents}
    if external:
        expected.add((external["projectResourceId"], external["name"]))
    observed = {(r["projectResourceId"], r["agent"]) for r in correlated if r["traceCount"] > 0}
    if not expected <= observed:
        raise Blocked("Native inventory couldn't correlate every invocation to ingested Azure telemetry.")
    native_success = all(item["status"] == "completed" for item in invocations
                         if item["agent"] in DATA["agents"])
    return {"status": "PASS", "runId": run_id, "invocations": invocations,
            "nativeInvocationStatus": "PASS" if native_success else "BLOCKED",
            "correlatedFleet": correlated, "rows": rows,
            "boundary": "Actual client/external runtime spans; not invented Foundry server telemetry."}


def policy_check(api: API, c: dict, timeout: int) -> dict:
    assignment = api.request("GET", ARM + c["policyAssignmentId"],
                             params={"api-version": "2024-04-01"})
    props = assignment["properties"]
    if (props["policyDefinitionId"] != POLICY_DEFINITION
            or props["parameters"]["effect"]["value"] != "Audit"):
        raise RuntimeError("Expected isolated built-in Audit policy; no broad enforcement permitted.")
    scope = c["policyAssignmentId"].split("/providers/Microsoft.Authorization/")[0]
    expected_scope = c["projectResourceId"].split("/providers/")[0]
    if scope.lower() != expected_scope.lower():
        raise RuntimeError("Policy assignment is not scoped to the isolated resource group.")
    api.request("POST", ARM + scope + "/providers/Microsoft.PolicyInsights/policyStates/latest/triggerEvaluation",
                 params={"api-version": "2019-10-01"})
    deadline = monotonic() + timeout
    while True:
        payload = api.request("POST",
            ARM + scope + "/providers/Microsoft.PolicyInsights/policyStates/latest/queryResults",
            params={"api-version": "2019-10-01",
                    "$filter": f"PolicyAssignmentId eq '{c['policyAssignmentId']}'"})
        states = payload.get("value", [])
        account_id = c["projectResourceId"].split("/projects/")[0]
        selected = [s for s in states if s.get("resourceId", "").lower() == account_id.lower()]
        if selected:
            if any(s.get("complianceState") != "Compliant" for s in selected):
                raise Blocked("Native Azure Policy evaluated isolated Foundry account as noncompliant.")
            return {"status": "PASS", "assignmentId": assignment["id"],
                    "scope": scope, "effect": "Audit", "complianceState": "Compliant"}
        if monotonic() >= deadline:
            raise Blocked("Audit assignment exists but Policy Insights has no evaluated account state yet.")
        sleep(15)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args()
    c = configuration()
    report = {"lab": 46, "resourceGroup": c["resourceGroup"],
              "controlPlanePortal": {"status": "NOT_VERIFIED",
                 "evidence": "API inventory is not complete Operate/Assets/Compliance portal verification."},
              "defenderPurview": {"status": "OPTIONAL_NOT_CONFIGURED",
                 "evidence": "No tenant security onboarding or fabricated security alerts."}}
    # Azure SDK HTTP logging can expose routing config; don't enable it in workshop logs.
    logging.getLogger("azure").setLevel(logging.ERROR)
    with AzureCliCredential(tenant_id=c["tenantId"]) as credential, httpx.Client(timeout=120) as http:
        api = API(credential, http)
        agents, external = [], None
        try:
            agents = setup_agents(c, credential)
            report["nativeAgents"] = {"status": "PASS", "agents": agents}
        except Exception as error:
            report["nativeAgents"] = {"status": "BLOCKED", "evidence": safe_error(error)}
        try:
            external = register_external(c, credential)
            report["externalRegistration"] = external
        except Exception as error:
            report["externalRegistration"] = {"status": "BLOCKED", "evidence": safe_error(error)}
        try:
            inventory = fleet_inventory(c, credential)
            if len(inventory) != 2 or any(not item["agents"] for item in inventory):
                raise Blocked("Expected native inventory with agents in both Foundry projects.")
            report["crossProjectInventory"] = {"status": "PASS", "projects": inventory}
        except Exception as error:
            report["crossProjectInventory"] = {"status": "BLOCKED", "evidence": safe_error(error)}
        try:
            if len(agents) != 2:
                raise Blocked("Two native prompt agents unavailable for real invocations.")
            report["telemetryCorrelation"] = telemetry_smoke(api, c, agents, external, args.timeout)
            report["nativeInvocations"] = {
                "status": report["telemetryCorrelation"]["nativeInvocationStatus"],
                "invocations": report["telemetryCorrelation"]["invocations"],
            }
        except Exception as error:
            report["telemetryCorrelation"] = {"status": "BLOCKED", "evidence": safe_error(error)}
            report["nativeInvocations"] = {"status": "BLOCKED", "evidence": safe_error(error)}
        try:
            report["isolatedAzurePolicy"] = policy_check(api, c, args.timeout)
        except Exception as error:
            report["isolatedAzurePolicy"] = {"status": "BLOCKED", "evidence": safe_error(error)}
    required = ("nativeAgents", "externalRegistration", "crossProjectInventory",
                "nativeInvocations", "telemetryCorrelation", "isolatedAzurePolicy")
    full_pass = all(report[name]["status"] == "PASS" for name in required)
    report["status"] = "PASS" if full_pass else "BLOCKED"
    save("live-report.json", report)
    print(json.dumps(report, indent=2))
    return 0 if full_pass else 2


if __name__ == "__main__":
    raise SystemExit(main())
