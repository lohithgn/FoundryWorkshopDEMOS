"""Deploy every demo, run its local tests, and run its live Python example."""

import argparse
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import time
from uuid import UUID


ROOT = Path(__file__).resolve().parent
SECURITY_TAGS = {
    "SecurityControl": "Ignore",
    "SecurityExemption": "StorageAccountKeyAccess",
}


def run(
    command: list[str],
    *,
    cwd: Path = ROOT,
    env: dict[str, str] | None = None,
    capture: bool = False,
) -> str:
    print(f"\n> {shlex.join(command)}", flush=True)
    result = subprocess.run(
        command, cwd=cwd, env=env, check=True, text=True,
        stdout=subprocess.PIPE if capture else None,
    )
    return result.stdout if capture else ""


def discover(root: Path) -> list[Path]:
    demos = sorted({
        *[template.parent.parent for template in root.glob("*/infra/main.bicep")],
        *[requirements.parent for requirements in root.glob("*/requirements.txt")],
    }, key=lambda path: (
        int(match.group()) if (match := re.match(r"\d+", path.name)) else float("inf"),
        path.name,
    ))
    if not demos:
        raise ValueError(f"No demo templates found under {root}.")
    for demo in demos:
        for required in ("infra/main.bicep", "demo.py", "requirements.txt", "test_demo.py"):
            if not (demo / required).is_file():
                raise ValueError(f"{demo.name} is missing {required}.")
    return demos


def select_demos(demos: list[Path], selectors: list[str] | None) -> list[Path]:
    if not selectors:
        return demos
    selected = set()
    for selector in selectors:
        matches = [
            demo for demo in demos
            if demo.name == selector or demo.name.split("-", 1)[0] == selector
        ]
        if len(matches) != 1:
            raise ValueError(f"--demo {selector!r} must identify exactly one demo number or folder.")
        selected.add(matches[0])
    return [demo for demo in demos if demo in selected]


def demo_environment(outputs: dict, base: dict[str, str]) -> dict[str, str]:
    values = {}
    base = dict(base)
    base.pop("AZURE_OPENAI_DEPLOYMENT", None)
    for output, variable in (
        ("endpoint", "AZURE_OPENAI_ENDPOINT"),
    ):
        value = outputs.get(output, {}).get("value")
        if not isinstance(value, str) or not value.strip():
            raise ValueError(f"Bicep must return a nonempty '{output}' output.")
        values[variable] = value
    for output, variable in (
        ("deployment", "AZURE_OPENAI_DEPLOYMENT"),
        ("reasoningDeployment", "AZURE_OPENAI_REASONING_DEPLOYMENT"),
        ("batchDeployment", "AZURE_OPENAI_BATCH_DEPLOYMENT"),
        ("embeddingDeployment", "AZURE_OPENAI_EMBEDDING_DEPLOYMENT"),
        ("projectEndpoint", "FOUNDRY_PROJECT_ENDPOINT"),
        ("toolEndpoint", "WORKSHOP_TOOL_ENDPOINT"),
        ("searchEndpoint", "AZURE_SEARCH_ENDPOINT"),
        ("agentIdentityId", "ENTRA_AGENT_ID"),
        ("contentSafetyEndpoint", "CONTENT_SAFETY_ENDPOINT"),
        ("deniedEndpoint", "WORKSHOP_DENIED_ENDPOINT"),
        ("alternateDeployment", "AZURE_OPENAI_ALTERNATE_DEPLOYMENT"),
        ("routerDeployment", "AZURE_OPENAI_ROUTER_DEPLOYMENT"),
        ("applicationInsightsConnectionString", "APPLICATIONINSIGHTS_CONNECTION_STRING"),
        ("projectResourceId", "FOUNDRY_PROJECT_RESOURCE_ID"),
        ("tenantId", "AZURE_TENANT_ID"),
        ("searchResourceId", "AZURE_SEARCH_RESOURCE_ID"),
        ("storageEndpoint", "AZURE_STORAGE_BLOB_ENDPOINT"),
        ("storageResourceId", "AZURE_STORAGE_RESOURCE_ID"),
    ):
        if output in outputs:
            value = outputs[output].get("value")
            if not isinstance(value, str) or not value.strip():
                raise ValueError(f"Bicep output '{output}' must be a nonempty string.")
            values[variable] = value
    return {**base, **values, "PYTHONUNBUFFERED": "1"}


def run_live(python: Path, demo: Path, environment: dict[str, str]) -> None:
    log = demo / ".azure" / "demo.log"
    for attempt in range(11):
        print(f"\nRunning live demo: {demo.name} (attempt {attempt + 1}/11)", flush=True)
        result = subprocess.run(
            [str(python), "demo.py"], cwd=demo, env=environment,
            text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        )
        print(result.stdout, end="", flush=True)
        with log.open("a") as stream:
            stream.write(f"\n--- Attempt {attempt + 1} ---\n{result.stdout}")
        if result.returncode == 0:
            return
        dns_failure = (
            "openai.APIConnectionError:" in result.stdout
            or "azure.core.exceptions.ServiceRequestError:" in result.stdout
        ) and any(message in result.stdout for message in (
            "[Errno 8] nodename nor servname provided, or not known",
            "[Errno -2] Name or service not known",
            "[Errno -3] Temporary failure in name resolution",
            "[Errno 11001] getaddrinfo failed",
        ))
        propagating = (
            "openai.PermissionDeniedError: Error code: 403" in result.stdout
            or (
                "azure.core.exceptions.HttpResponseError" in result.stdout
                and ("(PermissionDenied)" in result.stdout or "(Forbidden)" in result.stdout)
            )
            or (
                "openai.NotFoundError: Error code: 404" in result.stdout
                and "DeploymentNotFound" in result.stdout
            )
        )
        if not (propagating or dns_failure) or attempt == 10:
            if dns_failure:
                raise RuntimeError(
                    f"DNS resolution failed for live demo {demo.name} after "
                    f"{attempt + 1} attempts. Check the endpoints in "
                    f"{demo / '.azure' / 'deployment.json'} and local DNS/VPN settings; "
                    f"see {log}."
                )
            raise RuntimeError(f"Live demo {demo.name} failed; see {log}.")
        reason = (
            "DNS lookup failed; the endpoint or local DNS may not be ready."
            if dns_failure else "Azure access/deployment is not ready."
        )
        print(f"{reason} Retrying in 30 seconds.", flush=True)
        time.sleep(30)


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--subscription", help="Subscription ID/name; defaults to the Azure CLI selection.")
    parser.add_argument("--location", default="eastus2")
    parser.add_argument("--search-location",
                        help="Override Search region for templates with searchLocation (for example demo 17).")
    parser.add_argument("--prefix", default="rg-foundry", help="Resource group prefix (default: rg-foundry).")
    parser.add_argument("--principal-id", help="Presenter user object ID; defaults to the signed-in user.")
    parser.add_argument("--demo", action="append", metavar="NUMBER_OR_FOLDER",
                        help="Deploy only this demo; repeat to select several. Default: all demos.")
    args = parser.parse_args(argv)
    if not re.fullmatch(r"[a-zA-Z0-9_-]{1,50}", args.prefix):
        parser.error("--prefix must contain 1-50 letters, numbers, underscores, or hyphens.")

    try:
        demos = select_demos(discover(ROOT), args.demo)
    except ValueError as error:
        parser.error(str(error))
    extra_parameters = {}
    if any(demo.name == "12-entra-agent-id" for demo in demos):
        identity_variables = (
            "ENTRA_TENANT_ID", "ENTRA_BLUEPRINT_CLIENT_ID", "ENTRA_AGENT_ID", "ENTRA_BLUEPRINT_SECRET",
        )
        if any(not os.getenv(name, "").strip() for name in identity_variables):
            parser.error("Demo 12 requires setup_identity.ps1 first: set " + ", ".join(identity_variables))
        try:
            for name in identity_variables[:3]:
                UUID(os.environ[name])
        except ValueError:
            parser.error("Demo 12 tenant, blueprint, and agent IDs must be UUIDs.")
        if os.environ["ENTRA_BLUEPRINT_CLIENT_ID"] == os.environ["ENTRA_AGENT_ID"]:
            parser.error("Demo 12 requires a child agent ID, not the blueprint client ID.")
        extra_parameters["12-entra-agent-id"] = [f"agentIdentityId={os.environ['ENTRA_AGENT_ID']}"]
    account_command = ["az", "account", "show", "-o", "json"]
    if args.subscription:
        account_command.extend(["--subscription", args.subscription])
    account = json.loads(run(account_command, capture=True))
    subscription = account["id"]
    if account["user"]["type"] != "user":
        parser.error("Sign in with az login as a user; the Bicep role assignment requires a User.")
    principal = args.principal_id or run(
        ["az", "ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"],
        capture=True,
    ).strip()
    if not principal:
        parser.error("Could not resolve the presenter object ID; provide --principal-id.")
    print(f"Subscription: {account['name']} ({subscription})", flush=True)
    print("This deploys billable Azure resources and sends live model requests.", flush=True)

    providers = set()
    search_location_applied = False
    for demo in demos:
        template = json.loads(run(
            ["az", "bicep", "build", "--file", str(demo / "infra/main.bicep"), "--stdout"],
            capture=True,
        ))
        allowed_locations = template.get("parameters", {}).get("location", {}).get("allowedValues")
        if allowed_locations and args.location not in allowed_locations:
            parser.error(
                f"{demo.name} does not support --location {args.location!r}; "
                f"choose one of: {', '.join(allowed_locations)}."
            )
        providers.update(
            resource["type"].split("/")[0] for resource in template.get("resources", [])
            if not resource["type"].startswith("Microsoft.Authorization/")
        )
        if args.search_location and "searchLocation" in template.get("parameters", {}):
            extra_parameters.setdefault(demo.name, []).append(f"searchLocation={args.search_location}")
            search_location_applied = True
        run(["az", "bicep", "lint", "--file", str(demo / "infra/main.bicep")])
        python = demo / ".venv" / "bin" / "python"
        if not python.is_file():
            run([sys.executable, "-m", "venv", str(demo / ".venv")])
        run([str(python), "-m", "pip", "install", "-r", "requirements.txt"], cwd=demo)
        run(
            [str(python), "-m", "unittest", "discover", "-s", ".", "-p", "test_*.py", "-v"],
            cwd=demo,
        )
        if demo.name in (
            "21-permission-aware-retrieval", "31-native-agent-memory", "32-browser-runbook",
        ):
            run([str(python), "demo.py", "--preflight"], cwd=demo)
        if demo.name in ("40-voice-agent", "41-content-understanding"):
            run([str(python), "demo.py", "--preflight", "--location", args.location], cwd=demo)

    if args.search_location and not search_location_applied:
        parser.error("--search-location requires a selected template exposing searchLocation.")

    for provider in sorted(providers):
        run([
            "az", "provider", "register", "--namespace", provider,
            "--wait", "--subscription", subscription,
        ])
    for demo in demos:
        demo_name = re.sub(r"^\d+-", "", demo.name)
        deployment_name = f"{demo_name}-demo"
        group = f"{args.prefix}-{deployment_name}"
        run([
            "az", "group", "create", "--name", group, "--location", args.location,
            "--tags", *(f"{key}={value}" for key, value in SECURITY_TAGS.items()),
            "--subscription", subscription, "-o", "none",
        ])
        state = demo / ".azure"
        state.mkdir(exist_ok=True)
        outputs = json.loads(run([
            "az", "deployment", "group", "create", "--resource-group", group,
            "--name", deployment_name, "--subscription", subscription,
            "--template-file", str(demo / "infra/main.bicep"),
            "--parameters", f"principalId={principal}", f"location={args.location}",
            *extra_parameters.get(demo.name, []),
            "--query", "properties.outputs", "-o", "json",
        ], capture=True))
        (state / "deployment.json").write_text(json.dumps({
            "subscription": subscription, "resourceGroup": group, "outputs": outputs,
        }, indent=2) + "\n")
        environment = demo_environment(outputs, dict(os.environ))
        if demo.name in (
            "42-managed-harness-tool-search", "43-agent-routines", "44-evaluation-optimizer",
        ):
            run([str(demo / ".venv" / "bin" / "python"), "demo.py", "--preflight"],
                cwd=demo, env=environment)
        run_live(demo / ".venv" / "bin" / "python", demo, environment)
        print(f"\nPASS: {demo.name}. Resource group: {group}.", flush=True)
    print("\nAll demo deployments, local tests, and live runs passed.", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (subprocess.CalledProcessError, OSError, ValueError, RuntimeError) as error:
        print(f"\nERROR: {error}", file=sys.stderr)
        sys.exit(1)
