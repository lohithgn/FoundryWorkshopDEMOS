"""Deploy and verify only this recorded, owned synthetic lab resource group."""
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


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--subscription")
    parser.add_argument("--principal-id")
    parser.add_argument("--prefix")
    parser.add_argument("--location", default="eastus2")
    parser.add_argument("--infra-only", action="store_true")
    args = parser.parse_args(argv)
    if args.prefix and not re.fullmatch(r"[a-zA-Z0-9_-]{1,50}", args.prefix):
        parser.error("--prefix must contain 1-50 letters, numbers, underscores, or hyphens.")
    command = ["az", "account", "show", "-o", "json"]
    if args.subscription:
        command.extend(["--subscription", args.subscription])
    account = json.loads(runner.run(command, capture=True))
    if account["user"]["type"] != "user":
        parser.error("Use az login as a user; the template assigns User RBAC.")
    subscription = account["id"]
    scope = ["--subscription", subscription]
    previous = json.loads(STATE.read_text()) if STATE.exists() else None
    name = re.sub(r"^\d+-", "", LAB.name) + "-demo"
    group = (previous["resourceGroup"] if previous and args.prefix is None
             else f"{args.prefix or 'rg-foundry'}-{name}")
    if not re.fullmatch(r"[a-zA-Z0-9_-]{1,90}", group) or not group.endswith("-" + name):
        parser.error("Recorded group is not a valid group for this lab.")
    if previous and (previous["subscription"], previous["resourceGroup"]) != (subscription, group):
        parser.error("Recorded lab targets a different subscription/group; do not overwrite its state.")
    exists = runner.run(["az", "group", "exists", "--name", group, *scope], capture=True).strip()
    if exists == "true":
        tags = json.loads(runner.run(
            ["az", "group", "show", "--name", group, *scope, "--query", "tags", "-o", "json"],
            capture=True,
        ))
        if not previous or not isinstance(tags, dict) or any(tags.get(k) != v for k, v in OWNER.items()):
            parser.error("Refusing to reuse a group not recorded and owned by this lab.")
    elif exists != "false":
        raise ValueError(f"Unexpected group existence response: {exists!r}")
    principal = args.principal_id or runner.run(
        ["az", "ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"], capture=True,
    ).strip()
    if not principal:
        parser.error("Provide --principal-id or sign in as an identifiable user.")
    STATE.parent.mkdir(exist_ok=True)
    python = LAB / ".venv" / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
    if not args.infra_only:
        if not python.is_file():
            runner.run([sys.executable, "-m", "venv", str(LAB / ".venv")])
        runner.run([str(python), "-m", "pip", "install", "-r", "requirements.txt"], cwd=LAB)
        runner.run([str(python), "-m", "unittest", "discover", "-v"], cwd=LAB)
    template = LAB / "infra/main.bicep"
    compiled = json.loads(runner.run(
        ["az", "bicep", "build", "--file", str(template), "--stdout"], capture=True,
    ))
    for provider in sorted({r["type"].split("/")[0] for r in compiled.get("resources", [])
                            if not r["type"].startswith("Microsoft.Authorization/")}):
        runner.run(["az", "provider", "register", "--namespace", provider, "--wait", *scope])
    runner.run(["az", "group", "create", "--name", group, "--location", args.location,
                "--tags", *(f"{k}={v}" for k, v in {**OWNER, **runner.SECURITY_TAGS}.items()),
                *scope, "-o", "none"])
    result = {"subscription": subscription, "resourceGroup": group, "outputs": {}}
    STATE.write_text(json.dumps(result, indent=2) + "\n")
    result["outputs"] = json.loads(runner.run([
        "az", "deployment", "group", "create", "--resource-group", group,
        "--name", name, *scope, "--template-file", str(template),
        "--parameters", f"principalId={principal}", f"location={args.location}",
        "--query", "properties.outputs", "-o", "json",
    ], capture=True))
    STATE.write_text(json.dumps(result, indent=2) + "\n")
    (STATE.parent / "deployment.json").write_text(json.dumps(result, indent=2) + "\n")
    if not args.infra_only:
        runner.run_live(python, LAB, runner.demo_environment(result["outputs"], dict(os.environ)))


if __name__ == "__main__":
    main()
