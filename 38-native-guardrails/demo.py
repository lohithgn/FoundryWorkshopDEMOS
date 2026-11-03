"""Verify a model-attached native guardrail using only benign synthetic text."""

import json
import os
from pathlib import Path
import sys
import time
from uuid import uuid4

from azure.identity import AzureCliCredential
import requests

ROOT = Path(__file__).resolve().parent
MARKER = "WORKSHOP_RESTRICTED_ORCHID"
POLICY = "workshop-native-guardrail"
BLOCKLIST = "workshop-synthetic-marker"


def settings():
    path = ROOT / ".azure" / "deployment.json"
    outputs = json.loads(path.read_text()).get("outputs", {}) if path.exists() else {}
    return {
        "endpoint": outputs.get("endpoint", {}).get("value") or os.getenv("AZURE_OPENAI_ENDPOINT"),
        "deployment": outputs.get("deployment", {}).get("value") or os.getenv("AZURE_OPENAI_DEPLOYMENT"),
        "account": outputs.get("accountResourceId", {}).get("value"),
    }


def native_intervention(status, body):
    """Only the native custom-blocklist result qualifies, not generic HTTP 400."""
    error = body.get("error", {})
    inner = error.get("innererror", {})
    result = inner.get("content_filter_result", {}).get("custom_blocklists", {})
    if isinstance(result, list):
        filtered = any(item.get("filtered") is True and item.get("id") == BLOCKLIST for item in result)
    elif isinstance(result, dict):
        filtered = result.get("filtered") is True and any(
            item.get("filtered") is True and item.get("id") == BLOCKLIST for item in result.get("details", [])
        )
    else:
        filtered = False
    return (
        status == 400
        and error.get("code") == "content_filter"
        and inner.get("code") == "ResponsibleAIPolicyViolation"
        and filtered
    )


def agent_intervention(status, body):
    return (
        status == 400 and isinstance(body, dict)
        and body.get("code") == "content_filter"
        and body.get("innererror", {}).get("code") == "ContentFiltered"
        and any(
            item.get("blocked") is True and item.get("source_type") == "prompt"
            and any(
                entry.get("filtered") is True and entry.get("id") == BLOCKLIST
                for entry in item.get("content_filter_results", {}).get("custom_blocklists", [])
            )
            for item in body.get("content_filters", [])
        )
    )


def run():
    config = settings()
    if not all(config.values()):
        raise RuntimeError("Deploy first: endpoint, deployment and accountResourceId are required.")
    credential = AzureCliCredential()
    session = requests.Session()
    arm_header = {"Authorization": "Bearer " + credential.get_token("https://management.azure.com/.default").token}
    resource = requests.get(
        f"https://management.azure.com{config['account']}/deployments/{config['deployment']}",
        params={"api-version": "2025-06-01"}, headers=arm_header, timeout=45,
    )
    resource.raise_for_status()
    attached = resource.json()["properties"].get("raiPolicyName")
    if attached != POLICY:
        raise RuntimeError(f"Native guardrail is not attached: {attached!r}")
    headers = {"Authorization": "Bearer " + credential.get_token("https://cognitiveservices.azure.com/.default").token}
    url = f"{config['endpoint'].rstrip('/')}/openai/deployments/{config['deployment']}/chat/completions"
    evidence = {"native_policy": attached, "synthetic_only": True, "requests": []}
    for name, prompt in (
        ("allowed", "Reply with exactly: hello workshop"),
        ("restricted", f"The synthetic workshop inventory label is {MARKER}. Say hello."),
    ):
        if name == "restricted":
            # Azure RPM limits use short evaluation windows, not only minute totals.
            time.sleep(25)
        response = session.post(
            url, params={"api-version": "2024-10-21"}, headers=headers,
            json={"messages": [{"role": "user", "content": prompt}], "max_tokens": 24, "temperature": 0},
            timeout=60,
        )
        try:
            body = response.json()
        except ValueError:
            body = {"non_json_body": response.text[:4000]}
        evidence["requests"].append({"case": name, "status": response.status_code, "body": body})
        if name == "allowed":
            if response.status_code != 200 or not body.get("choices", [{}])[0].get("message", {}).get("content"):
                raise RuntimeError(f"Allowed request failed: {json.dumps(evidence)}")
        elif not native_intervention(response.status_code, body):
            raise RuntimeError(f"No native custom-blocklist intervention: {json.dumps(evidence)}")
    evidence["status"] = "PASS"
    print(json.dumps(evidence, indent=2))
    return evidence


def run_agent():
    from azure.ai.projects import AIProjectClient
    from azure.ai.projects.models import PromptAgentDefinition, RaiConfig

    outputs = json.loads((ROOT / ".azure" / "deployment.json").read_text())["outputs"]
    endpoint = outputs.get("projectEndpoint", {}).get("value") or os.getenv("FOUNDRY_PROJECT_ENDPOINT")
    deployment = outputs.get("agentDeployment", {}).get("value")
    if not endpoint or not deployment:
        raise RuntimeError("Native agent projectEndpoint/agentDeployment outputs are required.")
    credential = AzureCliCredential()
    evidence = {"stage": "native-agent", "status": "BLOCKED", "probes": []}
    # Prove the agent model itself does not supply the custom guardrail.
    config = settings()
    r = requests.post(
        f"{config['endpoint'].rstrip('/')}/openai/deployments/{deployment}/chat/completions",
        params={"api-version": "2024-10-21"},
        headers={"Authorization": "Bearer " + credential.get_token("https://cognitiveservices.azure.com/.default").token},
        json={"messages": [{"role": "user", "content": f"Inventory marker {MARKER}. Say hello."}], "max_tokens": 16},
        timeout=60,
    )
    evidence["unguarded_model"] = {"status": r.status_code, "body": r.json()}
    if r.status_code != 200:
        raise RuntimeError(f"Unguarded model control failed: {json.dumps(evidence)}")
    with AIProjectClient(endpoint=endpoint, credential=credential, allow_preview=True) as project:
        for policy_reference in (f"{config['account']}/raiPolicies/{POLICY}",):
            name = "native-guardrails-" + uuid4().hex[:12]
            created = False
            probe = {"agent_name": name, "policy_reference": policy_reference}
            evidence["probes"].append(probe)
            try:
                agent = project.agents.create_version(
                    agent_name=name,
                    definition=PromptAgentDefinition(
                        model=deployment, instructions="Answer benign synthetic workshop questions briefly.",
                        rai_config=RaiConfig(rai_policy_name=policy_reference),
                    ),
                )
                created = True
                probe["agent_version"] = agent.version
                persisted = project.agents.get_version(agent_name=name, agent_version=agent.version).as_dict()
                probe["persisted_definition"] = persisted["definition"]
                if persisted["definition"].get("rai_config", {}).get("rai_policy_name") != policy_reference:
                    raise RuntimeError("Agent did not persist its native RAI attachment.")
                with project.get_openai_client(agent_name=name, max_retries=0, timeout=90) as ai:
                    time.sleep(15)
                    allowed = ai.responses.create(input="Say hello workshop.", max_output_tokens=64)
                    probe["allowed"] = allowed.model_dump()
                    if allowed.status != "completed" or not allowed.output_text:
                        raise RuntimeError("Native agent allowed request failed.")
                    time.sleep(15)
                    try:
                        restricted = ai.responses.create(
                            input=f"Inventory marker {MARKER}. Say hello.", max_output_tokens=64,
                        )
                        probe["restricted"] = restricted.model_dump()
                        raise RuntimeError("Agent returned a response without native custom-blocklist intervention.")
                    except Exception as exc:
                        body = getattr(exc, "body", None)
                        status = getattr(exc, "status_code", None)
                        probe["intervention"] = {"status": status, "body": body, "error": str(exc)}
                        if not agent_intervention(status, body):
                            raise
                evidence["status"] = "PASS"
                return evidence
            except Exception as exc:
                probe["error"] = str(exc)
            finally:
                if created:
                    project.agents.delete(agent_name=name)
    return evidence


if __name__ == "__main__":
    stages = {"model": "BLOCKED", "agent": "NOT_ATTEMPTED"}
    try:
        baseline = run()
        stages["model"] = "PASS"
        (ROOT / ".azure" / "model-evidence.json").write_text(json.dumps(baseline, indent=2))
        stages["agent"] = "BLOCKED"
        agent = run_agent()
        stages["agent"] = agent["status"]
        (ROOT / ".azure" / "agent-evidence.json").write_text(json.dumps(agent, indent=2))
        print(json.dumps({"status": agent["status"], "stages": stages, "agent": agent}, indent=2))
        if agent["status"] != "PASS":
            sys.exit(1)
    except Exception as exc:
        print(json.dumps({"status": "BLOCKED", "stages": stages, "stage": "native-agent-live", "error": str(exc)}))
        sys.exit(1)
    finally:
        (ROOT / ".azure").mkdir(exist_ok=True)
        (ROOT / ".azure" / "live-report.json").write_text(json.dumps({
            "implemented": True, "live": "PASS" if all(value == "PASS" for value in stages.values()) else "BLOCKED",
            "stages": stages, "agent_boundary": "user-input", "tool_boundary": "NOT_CONFIGURED",
        }, indent=2))
