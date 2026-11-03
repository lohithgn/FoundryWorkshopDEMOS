"""Ownership-safe standalone runner; the shared root runner is also supported."""

import argparse
import json
import os
from pathlib import Path
import re
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import deploy as runner

LAB = Path(__file__).resolve().parent
STATE = LAB / ".azure" / "lab-deployment.json"
OWNER = {"managedBy": "foundry-lab-script", "lab": LAB.name}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--subscription")
    parser.add_argument("--principal-id")
    parser.add_argument("--prefix", default="rg-foundry-student")
    parser.add_argument("--location", default="eastus2")
    parser.add_argument("--infra-only", action="store_true")
    parser.add_argument("--offline-only", action="store_true")
    args = parser.parse_args(argv)
    if not re.fullmatch(r"[a-zA-Z0-9_-]{1,50}", args.prefix):
        parser.error("Invalid resource-group prefix.")
    python = LAB / ".venv" / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
    if not args.infra_only:
        if not python.is_file():
            runner.run([sys.executable, "-m", "venv", str(LAB / ".venv")])
        try:
            runner.run([str(python), "-c", "import azure.identity, requests, azure.ai.projects"], cwd=LAB)
        except RuntimeError:
            runner.run([str(python), "-m", "pip", "install", "-r", "requirements.txt"], cwd=LAB)
        runner.run([str(python), "-m", "unittest", "discover", "-v"], cwd=LAB)
    template = LAB / "infra/main.bicep"
    compiled = json.loads(runner.run(["az", "bicep", "build", "--file", str(template), "--stdout"], capture=True))
    runner.run(["az", "bicep", "lint", "--file", str(template)])
    if args.offline_only:
        return
    command = ["az", "account", "show", "-o", "json"]
    if args.subscription:
        command.extend(["--subscription", args.subscription])
    account = json.loads(runner.run(command, capture=True))
    if account["user"]["type"] != "user":
        parser.error("Sign in as a user; principalId uses User RBAC.")
    subscription = account["id"]
    scope = ["--subscription", subscription]
    lab_name = re.sub(r"^\d+-", "", LAB.name)
    group = f"{args.prefix}-{lab_name}-demo"
    if STATE.exists():
        previous = json.loads(STATE.read_text())
        if (previous["subscription"], previous["resourceGroup"]) != (subscription, group):
            parser.error("Recorded deployment targets another subscription/group.")
    exists = runner.run(["az", "group", "exists", "--name", group, *scope], capture=True).strip()
    if exists == "true":
        tags = json.loads(runner.run(
            ["az", "group", "show", "--name", group, *scope, "--query", "tags", "-o", "json"], capture=True,
        ))
        if not STATE.exists() or not isinstance(tags, dict) or any(tags.get(k) != v for k, v in OWNER.items()):
            parser.error("Refusing to reuse a group not recorded and owned by this lab.")
    elif exists != "false":
        raise ValueError(f"Unexpected existence response: {exists!r}")
    principal = args.principal_id or runner.run(
        ["az", "ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"], capture=True,
    ).strip()
    for provider in sorted({r["type"].split("/")[0] for r in compiled.get("resources", [])
                            if not r["type"].startswith("Microsoft.Authorization/")}):
        runner.run(["az", "provider", "register", "--namespace", provider, "--wait", *scope])
    runner.run(["az", "group", "create", "--name", group, "--location", args.location,
                "--tags", *(f"{k}={v}" for k, v in {**OWNER, **runner.SECURITY_TAGS}.items()), *scope, "-o", "none"])
    STATE.parent.mkdir(exist_ok=True)
    result = {"subscription": subscription, "resourceGroup": group, "outputs": {}}
    STATE.write_text(json.dumps(result, indent=2))
    result["outputs"] = json.loads(runner.run([
        "az", "deployment", "group", "create", "--resource-group", group, "--name", "workshop", *scope,
        "--template-file", str(template), "--parameters", f"principalId={principal}", f"location={args.location}",
        "--query", "properties.outputs", "-o", "json",
    ], capture=True))
    STATE.write_text(json.dumps(result, indent=2))
    (STATE.parent / "deployment.json").write_text(json.dumps(result, indent=2))
    if not args.infra_only:
        runner.run_live(python, LAB, runner.demo_environment(result["outputs"], dict(os.environ)))


if __name__ == "__main__":
    main()
