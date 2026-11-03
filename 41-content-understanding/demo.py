"""GA native Content Understanding schema extraction with service-grounded evidence."""

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from urllib.parse import urlsplit

from azure.identity import AzureCliCredential
import httpx

ROOT = Path(__file__).resolve().parent
STATE = ROOT / ".azure"
API_VERSION = "2025-11-01"
EXPECTED = {"Workshop": "Cedar Workshop", "Room": "Blue Room", "Seats": 12}
SUPPORTED_REGIONS = {
    "australiaeast", "canadacentral", "eastus", "eastus2", "japaneast", "southcentralus",
    "southeastasia", "swedencentral", "uksouth", "westeurope", "westus", "westus3",
}


def saved_outputs() -> dict:
    path = STATE / "deployment.json"
    return json.loads(path.read_text()).get("outputs", {}) if path.exists() else {}


def setting(environment: str, output: str, default: str = "") -> str:
    return os.getenv(environment, "").strip() or saved_outputs().get(output, {}).get("value", default)


def schema(modality: str, completion_model: str, embedding_model: str) -> dict:
    if modality not in ("document", "audio"):
        raise ValueError("Only document and audio analyzers are supported.")
    config = {"returnDetails": True}
    if modality == "document":
        config["estimateFieldSourceAndConfidence"] = True
    else:
        config["locales"] = ["en-US"]
    method = "extract" if modality == "document" else "generate"
    qualifier = "" if modality == "document" else " Do not infer missing facts."
    return {
        "description": f"Synthetic workshop {modality} schema extraction; no personal data.",
        "baseAnalyzerId": f"prebuilt-{modality}",
        "models": {"completion": completion_model, "embedding": embedding_model},
        "config": config,
        "fieldSchema": {
            "name": "WorkshopNotice",
            "fields": {
                "Workshop": {"type": "string", "method": method, "description": "Workshop name explicitly stated in the content, without labels." + qualifier},
                "Room": {"type": "string", "method": method, "description": "Room name explicitly stated in the content, without labels." + qualifier},
                "Seats": {"type": "integer", "method": method, "description": "Number of available seats explicitly stated in the content." + qualifier},
            },
        },
    }


def normalized(value: str) -> str:
    return " ".join(re.findall(r"[a-z0-9]+", value.lower()))


def validate_analyzer(stored: dict, requested: dict) -> None:
    if stored.get("baseAnalyzerId") != requested["baseAnalyzerId"] or stored.get("models") != requested["models"]:
        raise ValueError("Persisted native analyzer base/model configuration differs from requested schema.")
    fields = stored.get("fieldSchema", {}).get("fields", {})
    requested_fields = requested["fieldSchema"]["fields"]
    if set(fields) != set(EXPECTED) or any(
        fields[name].get("method") != requested_fields[name]["method"] or fields[name].get("type") != requested_fields[name]["type"]
        for name in EXPECTED
    ):
        raise ValueError("Persisted analyzer must contain the exact custom extraction field schema.")
    if requested["baseAnalyzerId"] == "prebuilt-document" and not stored.get("config", {}).get("estimateFieldSourceAndConfidence"):
        raise ValueError("Persisted document analyzer has no confidence/source estimation enabled.")


def validate_result(body: dict, modality: str) -> dict:
    if body.get("status", "").lower() != "succeeded":
        raise ValueError(f"Analysis did not succeed: {body.get('status')}")
    if not body.get("id"):
        raise ValueError("Service operation ID is missing.")
    result = body.get("result", {})
    if result.get("apiVersion") != API_VERSION:
        raise ValueError("Result must be from the GA API version.")
    contents = result.get("contents", [])
    if len(contents) != 1:
        raise ValueError("Expected exactly one content segment.")
    content = contents[0]
    if not content.get("markdown"):
        raise ValueError("Service content evidence is missing.")
    fields = content.get("fields", {})
    facts = {}
    for name, expected in EXPECTED.items():
        field = fields.get(name, {})
        value = field.get("valueInteger") if name == "Seats" else field.get("valueString")
        matches_expected = (
            isinstance(value, int) and not isinstance(value, bool) and value == expected
            if name == "Seats"
            else isinstance(value, str) and normalized(value) == normalized(str(expected))
        )
        if not matches_expected:
            raise ValueError(f"{modality} {name}: expected {expected!r}, extracted {value!r}.")
        confidence = field.get("confidence")
        source = field.get("source")
        if modality == "document":
            if isinstance(confidence, bool) or not isinstance(confidence, (int, float)) or not math.isfinite(confidence) or not 0 <= confidence <= 1:
                raise ValueError(f"{name}: missing or invalid real service confidence.")
            if confidence < 0.8:
                raise ValueError(f"{name}: confidence {confidence} requires human review.")
            if not isinstance(source, str) or not re.fullmatch(r"D\([0-9., -]+\)(?:;\s*D\([0-9., -]+\))*", source):
                raise ValueError(f"{name}: document source grounding is missing or invalid.")
            page_numbers = {page.get("pageNumber") for page in content.get("pages", [])}
            for page in re.findall(r"D\((\d+),", source):
                if int(page) not in page_numbers:
                    raise ValueError(f"{name}: source references a missing service page.")
            for polygon in re.findall(r"D\(([^)]+)\)", source):
                coordinates = polygon.split(",")
                if len(coordinates) != 9 or not all(math.isfinite(float(number)) for number in coordinates):
                    raise ValueError(f"{name}: source must contain a real page and eight finite polygon coordinates.")
            if normalized(str(expected)) not in normalized(content["markdown"]):
                raise ValueError(f"{name}: value has no matching service document text.")
            facts[name] = {"value": value, "confidence": confidence, "source": source}
        else:
            # GA audio doesn't promise per-field confidence; keep absence explicit.
            evidence_texts = (normalized(str(expected)), "twelve" if name == "Seats" else normalized(str(expected)))
            matches = [
                phrase for phrase in content.get("transcriptPhrases", [])
                if any(text in normalized(phrase.get("text", "")) for text in evidence_texts)
                and isinstance(phrase.get("startTimeMs"), (int, float))
                and isinstance(phrase.get("endTimeMs"), (int, float))
                and 0 <= phrase["startTimeMs"] < phrase["endTimeMs"]
            ]
            if not matches:
                raise ValueError(f"{name}: no matching time-grounded service transcript phrase.")
            if confidence is not None and (isinstance(confidence, bool) or not isinstance(confidence, (int, float)) or not math.isfinite(confidence) or not 0 <= confidence <= 1):
                raise ValueError(f"{name}: invalid service audio confidence.")
            facts[name] = {
                "value": value, "confidence": confidence, "source": source,
                "confidenceAvailability": "service-emitted" if confidence is not None else "not-emitted-by-GA-audio",
                "transcriptPhrases": matches,
            }
    return facts


def preflight(location: str, completion: str, embedding: str, capacity: int = 10) -> dict:
    if location not in SUPPORTED_REGIONS:
        raise ValueError(f"BLOCKED: GA Content Understanding is not documented in {location}; choose {', '.join(sorted(SUPPORTED_REGIONS))}.")
    if not 1 <= capacity <= 10:
        raise ValueError("Model capacity must be between 1 and 10.")
    identity = json.loads(subprocess.check_output(
        ["az", "account", "show", "--query", "{subscription:id,tenant:tenantId}", "-o", "json"], text=True,
    ))
    catalog = json.loads(subprocess.check_output(
        ["az", "cognitiveservices", "model", "list", "--location", location, "-o", "json"], text=True,
    ))
    usage = json.loads(subprocess.check_output(
        ["az", "cognitiveservices", "usage", "list", "--location", location, "-o", "json"], text=True,
    ))
    models = []
    for name in (completion, embedding):
        versions = sorted({item["model"]["version"] for item in catalog if item.get("kind") == "AIServices" and item.get("model", {}).get("name") == name})
        quotas = [item for item in usage if item.get("name", {}).get("value") == f"OpenAI.GlobalStandard.{name}"]
        required_version = {"gpt-5.2": "2025-12-11", "gpt-4o-mini": "2024-07-18", "gpt-5.4-mini": "2026-03-17", "text-embedding-3-large": "1"}.get(name)
        if not versions or (required_version and required_version not in versions) or not quotas or quotas[0]["limit"] - quotas[0]["currentValue"] < capacity:
            raise RuntimeError(f"BLOCKED: {name} GlobalStandard capacity-{capacity} deployment/version is unavailable in {location}; no resources provisioned by preflight.")
        models.append({"name": name, "versions": versions, "remainingCapacity": quotas[0]["limit"] - quotas[0]["currentValue"]})
    return {"status": "PASS", "stage": "local-model-quota-preflight", "apiVersion": API_VERSION, "location": location, "models": models, **identity}


class ContentUnderstanding:
    def __init__(self, endpoint: str, credential: AzureCliCredential, client: httpx.Client):
        self.endpoint = endpoint.rstrip("/")
        self.credential = credential
        self.client = client

    def request(self, method: str, route: str, *, content_type: str | None = None, allow_status: tuple[int, ...] = (), **kwargs) -> httpx.Response:
        url = route if route.startswith("https://") else f"{self.endpoint}/contentunderstanding/{route}"
        if urlsplit(url).scheme != "https" or urlsplit(url).netloc != urlsplit(self.endpoint).netloc:
            raise ValueError("Refusing to send Entra token to an untrusted operation URL.")
        headers = {"Authorization": f"Bearer {self.credential.get_token('https://cognitiveservices.azure.com/.default').token}"}
        if method == "GET" and "/analyzerResults/" in url:
            headers["x-ms-diagnostics"] = "true"
        if content_type:
            headers["Content-Type"] = content_type
        response = None
        for attempt in range(3):
            try:
                response = self.client.request(
                    method, url, params={"api-version": API_VERSION},
                    headers=headers, **kwargs,
                )
                if response.status_code not in (429, 500, 502, 503, 504) or attempt == 2:
                    break
            except httpx.TransportError as exc:
                if attempt == 2:
                    raise RuntimeError(f"CU {method} {urlsplit(url).path}: transport failed after three real attempts: {exc}") from exc
            time.sleep(2 ** (attempt + 1))
        assert response is not None
        if response.is_error and response.status_code not in allow_status:
            raise RuntimeError(f"CU HTTP {response.status_code}: {response.text[:2500]}")
        return response

    def ensure_analyzer(self, analyzer_id: str, requested: dict) -> dict:
        existing = self.request("GET", f"analyzers/{analyzer_id}", allow_status=(404,))
        if existing.status_code == 404:
            self.poll(self.request("PUT", f"analyzers/{analyzer_id}", json=requested))
            existing = self.request("GET", f"analyzers/{analyzer_id}")
        stored = existing.json()
        validate_analyzer(stored, requested)
        return stored

    def poll(self, response: httpx.Response, timeout: int = 600, evidence_path: Path | None = None) -> dict:
        operation = response.headers.get("Operation-Location")
        if not operation:
            if response.status_code in (200, 201):
                return response.json()
            raise RuntimeError("Missing Content Understanding Operation-Location.")
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            result = self.request("GET", operation)
            body = result.json()
            if evidence_path:
                evidence_path.write_text(json.dumps(body, indent=2))
            status = body.get("status", "").lower()
            if status == "succeeded":
                return body
            if status in ("failed", "canceled", "cancelled"):
                raise RuntimeError(f"CU operation failed: {json.dumps(body)[:2500]}")
            time.sleep(min(5, max(1, int(result.headers.get("retry-after", "2")))))
        raise TimeoutError(f"Content Understanding operation exceeded bounded {timeout}-second deadline.")


def live() -> dict:
    endpoint = setting("CONTENT_UNDERSTANDING_ENDPOINT", "endpoint") or os.getenv("AZURE_OPENAI_ENDPOINT", "")
    if not endpoint:
        raise RuntimeError("Deploy infra/main.bicep first; .azure/deployment.json or CONTENT_UNDERSTANDING_ENDPOINT is required.")
    completion = setting("CU_COMPLETION_MODEL", "completionModelName", "gpt-5.2")
    embedding = setting("CU_EMBEDDING_MODEL", "embeddingModelName", "text-embedding-3-large")
    deployment = setting("AZURE_OPENAI_DEPLOYMENT", "deployment", "cu-ga-completion")
    embedding_deployment = setting("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "embeddingDeployment", "cu-embedding")
    report = {"status": "RUNNING", "stage": "GA-native-CU", "apiVersion": API_VERSION, "modalities": {}}
    (STATE / "live-report.json").write_text(json.dumps(report, indent=2))
    with AzureCliCredential() as credential, httpx.Client(timeout=60) as http:
        cu = ContentUnderstanding(endpoint, credential, http)
        print("Configuring native CU model deployment defaults.", flush=True)
        defaults = cu.request("PATCH", "defaults", json={"modelDeployments": {
            completion: deployment, embedding: embedding_deployment,
            "prebuilt-analyzer-completion": deployment, "prebuilt-analyzer-completion-mini": deployment,
            "prebuilt-analyzer-embedding": embedding_deployment,
        }}).json()
        (STATE / "defaults.json").write_text(json.dumps(defaults, indent=2))
        for modality, asset, content_type in (
            ("document", "notice.pdf", "application/pdf"), ("audio", "notice.wav", "audio/wav"),
        ):
            model_slug = completion.replace("-", "_").replace(".", "_")
            analyzer_id = f"workshop_{modality}_{model_slug}"
            analyzer = schema(modality, completion, embedding)
            print(f"Checking native {modality} analyzer {analyzer_id}.", flush=True)
            stored = cu.ensure_analyzer(analyzer_id, analyzer)
            (STATE / f"{modality}-analyzer.json").write_text(json.dumps(stored, indent=2))
            data = (ROOT / "assets" / asset).read_bytes()
            print(f"Submitting real {modality} bytes to GA analyzeBinary.", flush=True)
            response = cu.request("POST", f"analyzers/{analyzer_id}:analyzeBinary", content=data, content_type=content_type)
            (STATE / f"{modality}-operation.json").write_text(json.dumps({
                "statusCode": response.status_code, "operationLocation": response.headers.get("Operation-Location"),
                "analyzerId": analyzer_id, "assetSha256": hashlib.sha256(data).hexdigest(),
            }, indent=2))
            print(f"Submitted real {modality} CU operation for {analyzer_id}.", flush=True)
            result = cu.poll(response, evidence_path=STATE / f"{modality}-poll.json")
            (STATE / f"{modality}-response.json").write_text(json.dumps(result, indent=2))
            facts = validate_result(result, modality)
            report["modalities"][modality] = {
                "status": "PASS", "analyzerId": analyzer_id, "operationId": result.get("id"),
                "operationLocation": response.headers.get("Operation-Location"),
                "assetSha256": hashlib.sha256(data).hexdigest(), "fields": facts,
            }
            (STATE / "live-report.json").write_text(json.dumps(report, indent=2))
    report["status"] = "PASS"
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight", action="store_true")
    parser.add_argument("--location", default="eastus2")
    args = parser.parse_args()
    STATE.mkdir(exist_ok=True)
    try:
        report = preflight(args.location, os.getenv("CU_COMPLETION_MODEL", "gpt-5.2"), os.getenv("CU_EMBEDDING_MODEL", "text-embedding-3-large")) if args.preflight else live()
    except Exception as exc:
        path = STATE / ("preflight.json" if args.preflight else "live-report.json")
        report = json.loads(path.read_text()) if not args.preflight and path.exists() else {}
        report.update(status="BLOCKED", stage="preflight" if args.preflight else "GA-native-CU", errorType=type(exc).__name__, error=str(exc))
        path.write_text(json.dumps(report, indent=2))
        print(json.dumps(report, indent=2))
        return 1
    (STATE / ("preflight.json" if args.preflight else "live-report.json")).write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
