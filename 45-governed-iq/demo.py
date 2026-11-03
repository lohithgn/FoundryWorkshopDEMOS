"""Permission-native retrieval and a real Fabric data-agent smoke test."""
import argparse
from email.utils import formatdate
import json
import logging
import os
from pathlib import Path
import re
import subprocess
from time import monotonic, sleep
from uuid import UUID

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    FabricDataAgentToolParameters, MicrosoftFabricPreviewTool,
    PromptAgentDefinition, ToolProjectConnection,
)
from azure.identity import AzureCliCredential
import httpx

HERE = Path(__file__).resolve().parent
STATE = HERE / ".azure"
SEARCH_VERSION = "2026-08-01-preview"
SOURCE = "synthetic-governed-source"
BASE = "synthetic-governed-kb"
MARKER = "HELIOS-045-ORCHID"
OUTPUT_ENV = {
    "endpoint": "AZURE_OPENAI_ENDPOINT", "deployment": "AZURE_OPENAI_DEPLOYMENT",
    "projectEndpoint": "FOUNDRY_PROJECT_ENDPOINT",
    "projectResourceId": "FOUNDRY_PROJECT_RESOURCE_ID",
    "embeddingDeployment": "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
    "storageEndpoint": "AZURE_STORAGE_BLOB_ENDPOINT",
    "storageResourceId": "AZURE_STORAGE_RESOURCE_ID",
    "containerResourceId": "GOVERNED_IQ_CONTAINER_RESOURCE_ID",
    "searchEndpoint": "AZURE_SEARCH_ENDPOINT", "searchResourceId": "AZURE_SEARCH_RESOURCE_ID",
    "tenantId": "AZURE_TENANT_ID", "principalId": "AZURE_PRINCIPAL_ID",
}


class Blocked(RuntimeError):
    """A live prerequisite or capability is absent, never an offline success."""


def config() -> dict:
    path = STATE / "deployment.json"
    recorded = json.loads(path.read_text()) if path.exists() else {}
    saved = recorded.get("outputs", {})
    values = {key: os.environ.get(env, saved.get(key, {}).get("value", ""))
              for key, env in OUTPUT_ENV.items()}
    missing = [OUTPUT_ENV[key] for key, value in values.items() if not value]
    if missing:
        raise Blocked("Missing deployment outputs: " + ", ".join(missing))
    for key in ("endpoint", "projectEndpoint", "storageEndpoint", "searchEndpoint"):
        if not values[key].startswith("https://"):
            raise ValueError(f"{key} must be HTTPS.")
        values[key] = values[key].rstrip("/")
    group = recorded.get("resourceGroup", "rg-foundry-governed-iq-demo")
    if not re.fullmatch(r"[a-zA-Z0-9_-]{1,90}", group) or not group.endswith("-governed-iq-demo"):
        raise ValueError("Only a recorded synthetic governed-IQ resource group may be mutated.")
    for key in ("projectResourceId", "storageResourceId", "containerResourceId", "searchResourceId"):
        if f"/resourceGroups/{group}/".lower() not in values[key].lower():
            raise ValueError("Resource IDs must stay within the recorded synthetic resource group.")
    values["resourceGroup"] = group
    return values


def save(name: str, value: dict) -> None:
    STATE.mkdir(exist_ok=True)
    (STATE / name).write_text(json.dumps(value, indent=2) + "\n")


class API:
    def __init__(self, credential, client: httpx.Client):
        self.credential, self.client = credential, client

    def request(self, method: str, url: str, scope: str, *, headers=None, **kwargs) -> dict:
        response = self.client.request(
            method, url, headers={
                "Authorization": "Bearer " + self.credential.get_token(scope).token,
                **(headers or {}),
            }, **kwargs,
        )
        if not response.is_success:
            # Bodies can echo credentials. Retain only service code/status/request ID.
            try:
                payload = response.json()
                code = (payload.get("error") or {}).get("code", payload.get("errorCode", "unknown"))
            except (ValueError, AttributeError):
                code = "unknown"
            raise Blocked(f"{method} {url}: HTTP {response.status_code}, code={code}, "
                          f"requestId={response.headers.get('x-ms-request-id', 'unavailable')}")
        return response.json() if response.content else {}


def source_definition(c: dict) -> dict:
    return {
        "name": SOURCE, "kind": "azureBlob",
        "description": "Synthetic restricted Helios maintenance instructions.",
        "azureBlobParameters": {
            "connectionString": f"ResourceId={c['storageResourceId']}/;",
            "containerName": "synthetic-restricted", "isADLSGen2": False,
            "ingestionParameters": {
                "embeddingModel": {"kind": "azureOpenAI", "azureOpenAIParameters": {
                    "resourceUri": c["endpoint"], "deploymentId": c["embeddingDeployment"],
                    "modelName": "text-embedding-3-small",
                }},
                "disableImageVerbalization": True, "contentExtractionMode": "minimal",
                "ingestionPermissionOptions": ["rbacScope"],
            },
        },
    }


def retrieval_body() -> dict:
    return {
        "messages": [{"role": "user", "content": [{"type": "text",
                      "text": "What is the Helios restricted maintenance code?"}]}],
        "knowledgeSourceParams": [{"knowledgeSourceName": SOURCE, "kind": "azureBlob",
                                   "includeReferences": True, "alwaysQuerySource": True}],
    }


def contains_marker(value: dict) -> bool:
    return MARKER in json.dumps(value)


def setup_iq(api: API, c: dict, timeout: int) -> dict:
    def search(method, path, **kwargs):
        return api.request(method, f"{c['searchEndpoint']}/{path}",
                           "https://search.azure.com/.default",
                           params={"api-version": SEARCH_VERSION}, **kwargs)
    content = (HERE / "data/restricted.txt").read_bytes()
    if b"SYNTHETIC WORKSHOP DATA" not in content:
        raise ValueError("Only synthetic assets may be uploaded.")
    blob = f"{c['storageEndpoint']}/synthetic-restricted/restricted.txt"
    # Blob responses aren't JSON; use the same in-memory token, without logging it.
    response = api.client.put(blob, content=content, headers={
        "Authorization": "Bearer " + api.credential.get_token("https://storage.azure.com/.default").token,
        "x-ms-version": "2023-11-03", "x-ms-date": formatdate(usegmt=True),
        "x-ms-blob-type": "BlockBlob", "Content-Type": "text/plain",
    })
    if response.status_code not in (200, 201):
        raise Blocked(f"Synthetic blob upload HTTP {response.status_code}")
    search("PUT", f"knowledgesources/{SOURCE}", json=source_definition(c))
    source = search("GET", f"knowledgesources/{SOURCE}")
    params = source["azureBlobParameters"]
    if params["ingestionParameters"].get("ingestionPermissionOptions") != ["rbacScope"]:
        raise RuntimeError("Service didn't retain native permission ingestion.")
    resources = params["createdResources"]
    datasource = search("GET", f"datasources/{resources['datasource']}")
    if datasource.get("indexerPermissionOptions") != ["rbacScope"]:
        raise RuntimeError("Generated native data source isn't synchronizing RBAC scope.")
    deadline = monotonic() + timeout
    while True:
        status = search("GET", f"indexers/{resources['indexer']}/status")
        last = status.get("lastResult") or {}
        if last.get("status") == "success" and last.get("itemsProcessed", 0) > 0:
            if last.get("errors") or last.get("warnings") or last.get("itemsFailed", 0):
                raise Blocked("Native ingestion returned errors/warnings.")
            break
        if last.get("status") in ("persistentFailure", "transientFailure"):
            codes = [e.get("name", "indexer-error") for e in last.get("errors", [])]
            raise Blocked(f"Native indexer {last['status']}; errors={codes}")
        if monotonic() >= deadline:
            raise Blocked(f"Native ingestion timeout; last status={last.get('status')}")
        sleep(5)
    schema = search("GET", f"indexes/{resources['index']}")
    permission_fields = [f["name"] for f in schema["fields"] if f.get("permissionFilter")]
    if not permission_fields or schema.get("permissionFilterOption") != "enabled":
        raise RuntimeError("Generated index lacks enforced native permission fields.")
    search("PUT", f"knowledgebases/{BASE}", json={
        "name": BASE, "knowledgeSources": [{"name": SOURCE}],
        "models": [{"kind": "azureOpenAI", "azureOpenAIParameters": {
            "resourceUri": c["endpoint"], "deploymentId": c["deployment"],
            "modelName": "gpt-4.1-mini",
        }}],
        "outputMode": "extractiveData", "retrievalReasoningEffort": {"kind": "low"},
    })
    # Native project connection: explicit AAD, not project MI impersonating the caller.
    connection = api.request("PUT",
        f"https://management.azure.com{c['projectResourceId']}/connections/synthetic-iq-search",
        "https://management.azure.com/.default", params={"api-version": "2025-04-01-preview"},
        json={"properties": {"category": "CognitiveSearch", "authType": "AAD",
              "target": c["searchEndpoint"], "isSharedToAll": False,
              "metadata": {"ApiType": "Azure", "ResourceId": c["searchResourceId"]}}})
    return {"index": resources["index"], "permissionFields": permission_fields,
            "ingestion": "success", "nativeSourcePermissionSync": datasource["indexerPermissionOptions"],
            "connectionId": connection["id"]}


def retrieve(api: API, c: dict, query_token: str | None) -> dict:
    headers = {"x-ms-query-source-authorization": query_token} if query_token else {}
    return api.request("POST", f"{c['searchEndpoint']}/knowledgebases/{BASE}/retrieve",
                       "https://search.azure.com/.default", headers=headers,
                       params={"api-version": SEARCH_VERSION}, json=retrieval_body())


def test_iq_access(api: API, c: dict) -> dict:
    positive = retrieve(api, c, api.credential.get_token("https://search.azure.com/.default").token)
    no_identity = retrieve(api, c, None)
    if contains_marker(no_identity) or no_identity.get("references"):
        raise RuntimeError("SECURITY FAILURE: omitted source identity exposed restricted content.")
    result = {"authorizedCaller": "PASS" if contains_marker(positive) else "BLOCKED",
              "authorizedReferences": len(positive.get("references", [])),
              "missingSourceIdentityDenied": "PASS",
              "twoRealUsers": "BLOCKED"}
    if not contains_marker(positive):
        result["authorizedEvidence"] = "Native query accepted the real caller but didn't return the protected marker."
    directory = os.environ.get("SECOND_AZURE_CONFIG_DIR") or os.environ.get("SECONDARY_AZURE_CONFIG_DIR")
    if not directory:
        result["twoRealUsersEvidence"] = "SECOND_AZURE_CONFIG_DIR absent; no secondary CLI user created."
        return result
    env = {**os.environ, "AZURE_CONFIG_DIR": directory}

    def second(*args):
        completed = subprocess.run(["az", *args, "-o", "json", "--only-show-errors"],
                                   env=env, text=True, capture_output=True)
        if completed.returncode:
            raise Blocked("Secondary Azure CLI sign-in unavailable; credentials not logged.")
        return json.loads(completed.stdout)
    identity = second("ad", "signed-in-user", "show")
    if identity["id"] == c["principalId"]:
        raise Blocked("Secondary sign-in is the same principal; cannot prove two-user denial.")
    storage_token = second("account", "get-access-token", "--resource", "https://storage.azure.com/")["accessToken"]
    denied = api.client.get(f"{c['storageEndpoint']}/synthetic-restricted/restricted.txt", headers={
        "Authorization": "Bearer " + storage_token, "x-ms-version": "2023-11-03",
    })
    if denied.status_code != 403:
        raise Blocked(f"Secondary source read HTTP {denied.status_code}; expected genuine 403.")
    query_token = second("account", "get-access-token", "--resource", "https://search.azure.com/")["accessToken"]
    negative = retrieve(api, c, query_token)
    if contains_marker(negative) or negative.get("references"):
        raise RuntimeError("SECURITY FAILURE: real unauthorized caller retrieved restricted content.")
    result["twoRealUsers"] = "PASS"
    result["twoRealUsersEvidence"] = "Distinct signed-in user; source read 403; KB marker absent."
    return result


def fabric_connection_body(workspace: str, artifact: str) -> dict:
    return {"properties": {"category": "CustomKeys", "authType": "CustomKeys",
            "credentials": {"keys": {"workspace_id": str(UUID(workspace)),
                                     "artifact_id": str(UUID(artifact))}}}}


def fabric_definition(model: str, connection_id: str) -> PromptAgentDefinition:
    return PromptAgentDefinition(
        model=model, instructions="Use Fabric for synthetic sales. Never invent results.",
        tools=[MicrosoftFabricPreviewTool(
            fabric_dataagent_preview=FabricDataAgentToolParameters(
                project_connections=[ToolProjectConnection(project_connection_id=connection_id)]))],
    )


def fabric_smoke(api: API, c: dict) -> dict:
    probes = {}
    # Read metadata only, never enumerate or read existing business items.
    for name, url, scope in (
        ("fabricCapacities", "https://api.fabric.microsoft.com/v1/capacities",
         "https://api.fabric.microsoft.com/.default"),
        ("powerBiCapacities", "https://api.powerbi.com/v1.0/myorg/capacities",
         "https://analysis.windows.net/powerbi/api/.default"),
        ("armFabricCapacities",
         "https://management.azure.com/subscriptions/1217c203-6c76-47b4-b61c-9bba62dc8e02/providers/Microsoft.Fabric/capacities?api-version=2023-11-01",
         "https://management.azure.com/.default"),
    ):
        try:
            payload = api.request("GET", url, scope)
            probes[name] = {"status": "PASS", "count": len(payload.get("value", []))}
        except Exception as error:
            probes[name] = {"status": "BLOCKED", "evidence": safe_error(error)}
    save("fabric-preflight.json", probes)
    workspace, artifact = os.getenv("FABRIC_WORKSPACE_ID"), os.getenv("FABRIC_DATA_AGENT_ID")
    if not workspace or not artifact or os.getenv("FABRIC_SYNTHETIC_ONLY") != "1":
        raise Blocked("Fabric designated synthetic workspace/data agent IDs and FABRIC_SYNTHETIC_ONLY=1 "
                      "absent; capacity/license probes saved in .azure/fabric-preflight.json.")
    workspace, artifact = str(UUID(workspace)), str(UUID(artifact))
    item = api.request("GET", f"https://api.fabric.microsoft.com/v1/workspaces/{workspace}/items/{artifact}",
                       "https://api.fabric.microsoft.com/.default")
    if not item.get("displayName", "").startswith("synthetic-governed-iq"):
        raise Blocked("Designated Fabric item isn't labeled synthetic-governed-iq; no content queried.")
    connection = api.request("PUT",
        f"https://management.azure.com{c['projectResourceId']}/connections/synthetic-fabric-iq",
        "https://management.azure.com/.default", params={"api-version": "2025-04-01-preview"},
        json=fabric_connection_body(workspace, artifact))
    with AIProjectClient(c["projectEndpoint"], api.credential, allow_preview=True) as project:
        agent = project.agents.create_version(agent_name="synthetic-fabric-iq",
                                              definition=fabric_definition(c["deployment"], connection["id"]))
        with project.get_openai_client(agent_name=agent.name) as openai:
            response = openai.responses.create(
                tool_choice="required",
                input="Using the synthetic sales data, what is total revenue? Return the total.",
            )
        if response.status != "completed" or "400" not in response.output_text:
            raise Blocked("Actual Fabric tool query didn't return the expected synthetic revenue 400.")
        calls = [item for item in response.output if "fabric" in item.type]
        if not calls:
            raise Blocked("Expected revenue text but no native Fabric tool execution in response.")
        if any(getattr(item, "error", None) or getattr(item, "status", None) in (
            "failed", "incomplete", "cancelled",
        ) for item in calls):
            raise Blocked("Native Fabric tool execution reported failure; revenue text is not proof.")
        return {"status": "PASS", "agent": agent.name, "version": agent.version,
                "connectionId": connection["id"], "toolCalls": [item.type for item in calls],
                "expectedRevenue": 400}


def safe_error(error: Exception) -> str:
    if isinstance(error, (Blocked, ValueError)):
        return str(error)
    status = getattr(error, "status_code", None)
    identity_codes = sorted(set(re.findall(r"AADSTS\d+", str(error))))
    if identity_codes:
        disabled = "; tenant resource service principal is disabled" if "AADSTS500014" in identity_codes else ""
        return f"{type(error).__name__}; Entra codes={','.join(identity_codes)}{disabled}; token not issued"
    if "Remote end closed connection without response" in str(error):
        return f"{type(error).__name__}; remote end closed connection without HTTP response"
    response = getattr(error, "response", None)
    request_id = response.headers.get("x-ms-request-id", "unavailable") if response is not None else "unavailable"
    return f"{type(error).__name__}; HTTP {status}; requestId={request_id}; body omitted"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args()
    c = config()
    report = {"lab": 45, "resourceGroup": c["resourceGroup"]}
    logging.getLogger("azure").setLevel(logging.ERROR)
    with AzureCliCredential(tenant_id=c["tenantId"]) as credential, httpx.Client(timeout=120) as http:
        api = API(credential, http)
        try:
            report["nativeIQSetup"] = {"status": "PASS", **setup_iq(api, c, args.timeout)}
            source_read = http.get(
                f"{c['storageEndpoint']}/synthetic-restricted/restricted.txt",
                headers={"Authorization": "Bearer " + credential.get_token(
                    "https://storage.azure.com/.default").token, "x-ms-version": "2023-11-03"},
            )
            if source_read.status_code != 200 or MARKER.encode() not in source_read.content:
                raise Blocked(f"Authorized synthetic source read HTTP {source_read.status_code}; "
                              "expected 200 and marker.")
            report["authorizedSourceRead"] = {"status": "PASS", "httpStatus": 200}
            report["nativeIQAccess"] = test_iq_access(api, c)
        except Exception as error:
            report["nativeIQ"] = {"status": "BLOCKED", "evidence": safe_error(error)}
        try:
            report["fabricIQ"] = fabric_smoke(api, c)
        except Exception as error:
            report["fabricIQ"] = {"status": "BLOCKED", "evidence": safe_error(error)}
    full_pass = (report.get("nativeIQAccess", {}).get("authorizedCaller") == "PASS"
                 and report.get("nativeIQAccess", {}).get("twoRealUsers") == "PASS"
                 and report["fabricIQ"]["status"] == "PASS")
    report["status"] = "PASS" if full_pass else "BLOCKED"
    save("live-report.json", report)
    print(json.dumps(report, indent=2))
    return 0 if full_pass else 2


if __name__ == "__main__":
    raise SystemExit(main())
