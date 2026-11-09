"""Evaluate, promote, pin and roll back a centrally versioned Foundry skill."""

import argparse
from contextlib import ExitStack
from io import BytesIO
import json
import os
from pathlib import Path
from uuid import uuid4
from zipfile import ZipFile

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import PromptAgentDefinition, SkillInlineContent
from azure.identity import AzureCliCredential


SKILLS = Path(__file__).parent / "skills"
PRICES = {"intro": 25, "advanced": 50}
AGENT_INSTRUCTIONS = "Answer the user's synthetic workshop pricing question using this procedure:\n"


def skill_body(version: str, regression: bool = False) -> str:
    text = (SKILLS / f"pricing-{version}" / "SKILL.md").read_text(encoding="utf-8")
    body = text.split("---\n", 2)[2].strip()
    if regression:
        body = body.replace("intro workshop: USD 25", "intro workshop: USD 40", 1)
    return body


def download_body(project: AIProjectClient, name: str, version: str, expected: str) -> str:
    package = b"".join(project.beta.skills.download_version(name, version))
    with ZipFile(BytesIO(package)) as archive:
        files = [entry for entry in archive.infolist() if not entry.is_dir()]
        if len(files) != 1 or Path(files[0].filename).name != "SKILL.md":
            raise RuntimeError("Expected an instruction-only skill archive containing one SKILL.md.")
        text = archive.read(files[0]).decode("utf-8")
    if expected not in text:
        raise RuntimeError("Downloaded skill differs from the tested immutable instructions.")
    return text


def set_default(project: AIProjectClient, name: str, version: str) -> None:
    project.beta.skills.update(name=name, default_version=version)
    actual = project.beta.skills.get(name=name).default_version
    if actual != version:
        raise RuntimeError(f"Skill default readback mismatch: expected {version}, got {actual}.")
    print(f"Skill default read back: {name} / {actual}", flush=True)


def consume(
    project: AIProjectClient, deployment: str, name: str, consumer: str, workshop: str,
    bodies: dict[str, str], codes: dict[str, str], *, pinned: str | None = None,
) -> str:
    resolved = pinned if pinned is not None else project.beta.skills.get(name=name).default_version
    if not resolved or resolved not in bodies:
        raise RuntimeError(f"Consumer resolved an unrecognized skill version: {resolved}")
    text = download_body(project, name, resolved, bodies[resolved])
    print(
        f"{consumer}: {'pinned' if pinned else 'default resolved at session start'} "
        f"skill {name} / {resolved}; workshop={workshop}", flush=True,
    )
    agent = project.agents.create_version(
        agent_name=f"pricing-{uuid4().hex[:12]}",
        definition=PromptAgentDefinition(
            model=deployment, instructions=AGENT_INSTRUCTIONS + text, tools=[],
        ),
    )
    try:
        with project.get_openai_client(agent_name=agent.name, max_retries=0) as client:
            response = client.responses.create(
                input=(f'The workshop ID is "{workshop}". What is its unit price? '
                       "Echo the workshop ID exactly in the workshop field."),
                tool_choice="none", timeout=90.0,
            )
        if response.status != "completed" or not response.output_text.strip():
            raise RuntimeError(f"Pricing agent failed: {response.status}; {response.error}")
        value = json.loads(response.output_text)
        expected = {
            "workshop": workshop, "unit_price": PRICES[workshop],
            "currency": "USD", "skill_code": codes[resolved],
        }
        if (
            not isinstance(value, dict) or value != expected
            or type(value.get("unit_price")) is not int
        ):
            raise RuntimeError(f"Release gate failed: expected {expected}, received {value}.")
        print(f"PASS response {response.id}: {response.output_text}", flush=True)
        if response.usage is not None:
            print(f"Usage: {response.usage.model_dump_json()}", flush=True)
        return resolved
    finally:
        project.agents.delete_version(agent_name=agent.name, agent_version=agent.version)


def demonstrate(project: AIProjectClient, deployment: str, *, regression: bool = False) -> None:
    name = f"pricing-release-{uuid4().hex[:12]}"
    first_body = skill_body("v1")
    second_body = skill_body("v2", regression)
    with ExitStack() as cleanup:
        first = project.beta.skills.create(
            name=name, default=True,
            inline_content=SkillInlineContent(
                description="Synthetic workshop pricing procedure.", instructions=first_body,
            ),
        )
        cleanup.callback(project.beta.skills.delete, name=name)
        print(f"Created owned skill: {name} / {first.version}", flush=True)
        set_default(project, name, first.version)
        bodies = {first.version: first_body}
        codes = {first.version: "PRICING-V1"}
        try:
            for consumer, pinned in (("floating", None), ("pinned", first.version)):
                consume(project, deployment, name, consumer, "intro", bodies, codes, pinned=pinned)
            second = project.beta.skills.create(
                name=name, default=False,
                inline_content=SkillInlineContent(
                    description="Candidate synthetic workshop pricing procedure.",
                    instructions=second_body,
                ),
            )
            if second.version == first.version:
                raise RuntimeError("Skill versions must be distinct immutable snapshots.")
            bodies[second.version] = second_body
            codes[second.version] = "PRICING-V2"
            if project.beta.skills.get(name=name).default_version != first.version:
                raise RuntimeError("Creating the candidate unexpectedly changed the active default.")
            print(f"Evaluating candidate {second.version} before promotion.", flush=True)
            for workshop in PRICES:
                consume(
                    project, deployment, name, "candidate gate", workshop, bodies, codes,
                    pinned=second.version,
                )
            set_default(project, name, second.version)
            floating = consume(project, deployment, name, "floating", "intro", bodies, codes)
            pinned = consume(
                project, deployment, name, "pinned", "intro", bodies, codes, pinned=first.version,
            )
            if floating != second.version or pinned != first.version:
                raise RuntimeError("Default and pinned consumers did not resolve the expected versions.")
        finally:
            set_default(project, name, first.version)
        for consumer, pinned in (("floating after rollback", None), ("pinned after rollback", first.version)):
            resolved = consume(
                project, deployment, name, consumer, "intro", bodies, codes, pinned=pinned,
            )
            if resolved != first.version:
                raise RuntimeError("Rollback did not restore the original skill version.")
        print("Verified candidate gate, default promotion, pinned isolation and rollback.", flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-endpoint", default=os.getenv("FOUNDRY_PROJECT_ENDPOINT"))
    parser.add_argument("--deployment", default=os.getenv("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini"))
    parser.add_argument("--regression", action="store_true",
                        help="Use a wrong candidate price; expected nonzero gate failure without promotion.")
    args = parser.parse_args()
    if not args.project_endpoint or not args.project_endpoint.startswith("https://"):
        parser.error("Set FOUNDRY_PROJECT_ENDPOINT to the deployment's projectEndpoint HTTPS URL.")
    if not args.deployment.strip():
        parser.error("--deployment must not be empty.")
    with AzureCliCredential() as credential, AIProjectClient(
        endpoint=args.project_endpoint, credential=credential, allow_preview=True,
    ) as project:
        demonstrate(project, args.deployment, regression=args.regression)


if __name__ == "__main__":
    main()
