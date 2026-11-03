"""Prove central APIM token accounting, intervention and project isolation."""

import json
import os
import re
from pathlib import Path
import sys
import time
import xml.etree.ElementTree as ET
from uuid import uuid4

from azure.identity import AzureCliCredential
import requests

ROOT = Path(__file__).resolve().parent


def load_outputs():
    path = ROOT / ".azure" / "deployment.json"
    return json.loads(path.read_text()).get("outputs", {}) if path.exists() else {}


def gateway_endpoint():
    outputs = load_outputs()
    endpoint = outputs.get("gatewayEndpoint", {}).get("value") or os.getenv("AI_GATEWAY_ENDPOINT")
    if not endpoint or not endpoint.startswith("https://"):
        raise RuntimeError("Missing HTTPS AI_GATEWAY_ENDPOINT; deploy this lab first.")
    return endpoint.rstrip("/")


def intervention(kind, status, headers):
    return (
        status == {"rate": 429, "quota": 403}[kind]
        and headers.get("X-Workshop-Policy") == "workshop-token-limit"
        and headers.get("X-Workshop-Reason") == {
            "rate": "OpenAITokenLimitExceeded", "quota": "OpenAITokenQuotaExceeded",
        }[kind]
    )


def model_success(status, body):
    choices = body.get("choices", [])
    return (
        status == 200 and body.get("usage", {}).get("total_tokens", 0) > 0
        and bool(choices) and bool(choices[0].get("message", {}).get("content"))
    )


def parse_policy(text):
    text = text.lstrip("\ufeff \r\n\t")
    if not text.startswith("<"):
        text = json.loads(text)["properties"]["value"]
    return ET.fromstring(text)


def verify_policy_configuration(credential, run_id=None):
    outputs = load_outputs()
    gateway = outputs.get("gatewayResourceId", {}).get("value")
    projects = {
        "project-a": outputs.get("projectResourceId", {}).get("value"),
        "project-b": outputs.get("secondProjectResourceId", {}).get("value"),
    }
    if not gateway or not all(projects.values()):
        raise RuntimeError("Missing gateway/project ARM outputs; deploy this lab first.")
    headers = {"Authorization": "Bearer " + credential.get_token("https://management.azure.com/.default").token}
    policies = []
    for project, project_id in projects.items():
        for kind in ("rate", "quota"):
            route = f"{project}-{kind}"
            response = requests.get(
                f"https://management.azure.com{gateway}/apis/{route}/policies/policy",
                params={"api-version": "2024-05-01"}, headers=headers, timeout=45,
            )
            response.raise_for_status()
            policy = parse_policy(response.text)
            limit = policy.find("inbound/llm-token-limit")
            expected = {"tokens-per-minute": "100"} if kind == "rate" else {
                "token-quota": "100", "token-quota-period": "Hourly",
            }
            if limit is None or any(limit.get(key) != value for key, value in expected.items()):
                raise RuntimeError(f"ARM policy does not contain the expected real token limit: {route}")
            base = f"{project_id}-{kind}"
            key = limit.get("counter-key", "")
            expected_key = base + "-run-" + run_id if run_id else None
            valid_key = key == expected_key if run_id else bool(re.fullmatch(re.escape(base) + r"(?:-run-[0-9a-f]{32})?", key))
            if not valid_key or limit.get("id") != "workshop-token-limit":
                raise RuntimeError(f"ARM project-scoped counter or evidence policy ID differs: {route}")
            policies.append({"route": route, "project_resource_id": project_id, "limits": expected, "counter_key": key})
    return policies


def apply_counter_run(policy, base, run_id):
    policy.find("inbound/llm-token-limit").set("counter-key", base + "-run-" + run_id)
    for section in ("outbound", "on-error"):
        parent = policy.find(section)
        for old in list(parent.findall("set-header")):
            if old.get("name") == "X-Workshop-Counter-Run":
                parent.remove(old)
        node = ET.SubElement(parent, "set-header", {"name": "X-Workshop-Counter-Run", "exists-action": "override"})
        ET.SubElement(node, "value").text = run_id
    return policy


def rotate_policy_counters(credential):
    """Privileged lab reset: retain exact limits, assign new central counter keys."""
    policies = verify_policy_configuration(credential)
    run_id = uuid4().hex
    gateway = load_outputs()["gatewayResourceId"]["value"]
    headers = {"Authorization": "Bearer " + credential.get_token("https://management.azure.com/.default").token}
    for item in policies:
        url = f"https://management.azure.com{gateway}/apis/{item['route']}/policies/policy"
        response = requests.get(url, params={"api-version": "2024-05-01"}, headers=headers, timeout=45)
        response.raise_for_status()
        base = f"{item['project_resource_id']}-{item['route'].rsplit('-', 1)[1]}"
        policy = apply_counter_run(parse_policy(response.text), base, run_id)
        response = requests.put(
            url, params={"api-version": "2024-05-01"}, headers=headers,
            json={"properties": {"format": "rawxml", "value": ET.tostring(policy, encoding="unicode")}}, timeout=60,
        )
        response.raise_for_status()
    (ROOT / ".azure" / "counter-run.json").write_text(json.dumps({"run_id": run_id, "limits": {"TPM": 100, "Hourly": 100}}, indent=2))
    time.sleep(20)
    return run_id


def run():
    endpoint = gateway_endpoint()
    credential = AzureCliCredential()
    session = requests.Session()
    headers = {"Authorization": "Bearer " + credential.get_token("https://cognitiveservices.azure.com/.default").token}
    run_id = rotate_policy_counters(credential)
    evidence = {
        "gateway": endpoint, "policy": "llm-token-limit", "run_id": run_id,
        "arm_verified_policies": verify_policy_configuration(credential, run_id), "requests": [],
    }

    def request(project, kind, prompt):
        if evidence["requests"]:
            # Stay below the deployment's 10 RPM short-window backend limit.
            # This is request pacing, not the token-limit policy being demonstrated.
            time.sleep(7)
        for attempt in range(3):
            response = session.post(
                f"{endpoint}/{project}-{kind}/chat/completions",
                headers=headers, timeout=90,
                json={"messages": [{"role": "user", "content": prompt}], "max_tokens": 16, "temperature": 0},
            )
            fresh = response.headers.get("X-Workshop-Counter-Run") == run_id
            cold_start = not evidence["requests"] and response.status_code == 404
            if (fresh and not cold_start) or attempt == 2:
                break
            evidence.setdefault("startup_retries", []).append({
                "status": response.status_code, "reason": "APIM route/new counter-policy propagation",
            })
            time.sleep(15)
        try:
            body = response.json()
        except ValueError:
            body = {"non_json_body": response.text[:4000]}
        safe_headers = {
            key: response.headers[key] for key in (
                "X-Workshop-Policy", "X-Workshop-Reason", "Retry-After", "X-Workshop-Counter-Run",
                "X-Tokens-Consumed", "X-Rate-Remaining", "X-Quota-Remaining",
            ) if key in response.headers
        }
        evidence["requests"].append({
            "project": project, "kind": kind, "status": response.status_code,
            "headers": safe_headers, "body": body,
        })
        if response.headers.get("X-Workshop-Counter-Run") != run_id:
            raise RuntimeError(f"Gateway has not adopted this run's real counter policy: {json.dumps(evidence)}")
        return response, body

    for kind in ("rate", "quota"):
        response, body = request("project-a", kind, "Say hello workshop.")
        if not model_success(response.status_code, body):
            raise RuntimeError(f"Initial allowed request failed; counters may need reset: {json.dumps(evidence)}")
        # Actual backend-reported usage crosses the small service-side budget.
        prompt = "Synthetic inventory: " + "orchid shelf alpha " * 100 + ". Reply hello."
        response, body = request("project-a", kind, prompt)
        if not model_success(response.status_code, body):
            raise RuntimeError(f"Budget-consuming model request failed: {json.dumps(evidence)}")
        response, body = request("project-a", kind, "Say hello.")
        if not intervention(kind, response.status_code, response.headers):
            raise RuntimeError(f"No APIM {kind} intervention: {json.dumps(evidence)}")
        response, body = request("project-b", kind, "Say hello other project.")
        if not model_success(response.status_code, body):
            raise RuntimeError(f"Other project did not remain available: {json.dumps(evidence)}")
    evidence["status"] = "PASS"
    print(json.dumps(evidence, indent=2))
    return evidence


if __name__ == "__main__":
    result = None
    error = None
    try:
        result = run()
        (ROOT / ".azure" / "gateway-evidence.json").write_text(json.dumps(result, indent=2))
    except Exception as exc:
        error = str(exc)
        print(json.dumps({"status": "BLOCKED", "stage": "gateway-token-limit-live", "error": str(exc)}))
        sys.exit(1)
    finally:
        (ROOT / ".azure").mkdir(exist_ok=True)
        (ROOT / ".azure" / "live-report.json").write_text(json.dumps({
            "implemented": True, "live": "PASS" if result else "BLOCKED",
            "run_id": result.get("run_id") if result else None,
            "counter_reset": "privileged per-run APIM counters; unchanged 100 TPM/100 hourly tokens",
            "error": error,
        }, indent=2))
