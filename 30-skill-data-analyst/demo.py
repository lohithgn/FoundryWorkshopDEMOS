"""Run a pinned instructional skill with Foundry's native Code Interpreter."""

import argparse
from collections.abc import Callable, Iterator, Sequence
from contextlib import contextmanager
from functools import partial
from hashlib import sha256
from io import BytesIO
import json
import os
from pathlib import Path, PurePosixPath
import struct
import sys
from time import monotonic, sleep
from uuid import uuid4
from zipfile import ZipFile
import zlib

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    CodeInterpreterTool, CreateSkillVersionFromFilesBody, PromptAgentDefinition,
)
from azure.core.exceptions import ResourceNotFoundError
from azure.identity import AzureCliCredential
from openai import OpenAI
from openai.types.responses import Response


HERE = Path(__file__).resolve().parent
SKILL_PATH = HERE / "skills" / "csv-analyst" / "SKILL.md"
CSV_DATA = b"""workshop,session,attendees,cost_cents
Foundry,F1,12,18000
Agents,A1,9,18000
Foundry,F2,8,12000
Agents,A2,11,22000
Foundry,F3,10,15000
Agents,A3,5,10000
"""
# An independent test oracle, never sent to the model and never used to create artifacts.
EXPECTED = {
    "skill_code": "CSV-ANALYST-V1", "row_count": 6,
    "total_attendees": 55, "total_cost_cents": 95000,
    "by_workshop": [
        {"workshop": "Agents", "attendees": 25, "cost_cents": 50000},
        {"workshop": "Foundry", "attendees": 30, "cost_cents": 45000},
    ],
}
ARTIFACT_NAMES = {"report.json", "attendance-cost.png"}
TIMEOUT = 180.0


def skill_package(name: str) -> tuple[bytes, bytes]:
    content = SKILL_PATH.read_text(encoding="utf-8").replace(
        "name: csv-analyst\n", f"name: {name}\n", 1,
    ).encode("utf-8")
    stream = BytesIO()
    with ZipFile(stream, "w") as archive:
        archive.writestr("SKILL.md", content)
    return stream.getvalue(), content


def downloaded_instructions(package: bytes, expected: bytes) -> str:
    with ZipFile(BytesIO(package)) as archive:
        files = [entry for entry in archive.infolist() if not entry.is_dir()]
        if len(files) != 1 or PurePosixPath(files[0].filename).name != "SKILL.md":
            raise RuntimeError("Downloaded skill must contain exactly one SKILL.md.")
        if files[0].file_size != len(expected):
            raise RuntimeError("Downloaded skill content differs from pinned upload.")
        content = archive.read(files[0])
    if content != expected:
        raise RuntimeError("Downloaded skill content differs from pinned upload.")
    return content.decode("utf-8")


@contextmanager
def cleanup_scope() -> Iterator[list[tuple[str, Callable[[], object]]]]:
    tasks: list[tuple[str, Callable[[], object]]] = []
    try:
        yield tasks
    finally:
        already_failing = sys.exc_info()[0] is not None
        failures = []
        for label, delete in reversed(tasks):
            try:
                delete()
                print(f"Deleted {label}", flush=True)
            except Exception as exc:
                failures.append(label)
                print(f"Cleanup failed for {label}: {exc}", file=sys.stderr, flush=True)
        if failures and not already_failing:
            raise RuntimeError(f"Cleanup incomplete; remove manually: {', '.join(failures)}")


def execution_citations(response: Response, container_id: str) -> dict[str, str]:
    print(f"Response {response.id}: {response.status}", flush=True)
    calls = [item for item in response.output if item.type == "code_interpreter_call"]
    for call in calls:
        print(f"Execution {call.id}: {call.status}; container={call.container_id}", flush=True)
        print(f"Executed code:\n{call.code}", flush=True)
        for output in call.outputs or []:
            if output.type == "logs":
                print(f"Sandbox logs:\n{output.logs}", flush=True)
    if response.status != "completed":
        raise RuntimeError(f"Response did not complete: {response.status}; {response.error}")
    if not calls or any(
        call.status != "completed" or call.container_id != container_id or not call.code
        for call in calls
    ):
        raise RuntimeError("Missing successful native Code Interpreter execution in our container.")
    if not any(
        output.type == "logs" and "CSV_ANALYSIS_EXECUTED" in output.logs
        for call in calls for output in call.outputs or []
    ):
        raise RuntimeError("Missing sandbox execution log marker.")
    citations: dict[str, str] = {}
    for item in response.output:
        if item.type != "message":
            continue
        for content in item.content:
            if content.type != "output_text":
                continue
            for citation in content.annotations:
                if citation.type != "container_file_citation":
                    continue
                print(f"File citation: {citation.model_dump_json()}", flush=True)
                name = PurePosixPath(citation.filename).name
                if name not in ARTIFACT_NAMES:
                    continue
                if citation.container_id != container_id or not citation.file_id:
                    raise RuntimeError("Artifact citation references the wrong container or empty file ID.")
                if name in citations and citations[name] != citation.file_id:
                    raise RuntimeError("Conflicting artifact citations.")
                citations[name] = citation.file_id
    if set(citations) != ARTIFACT_NAMES or len(set(citations.values())) != 2:
        raise RuntimeError("Missing distinct container-file citations for report.json and attendance-cost.png.")
    print(response.output_text, flush=True)
    if response.usage is not None:
        print(f"Actual usage: {response.usage.model_dump_json()}", flush=True)
    return citations


def unique_fields(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def verify_report(content: bytes) -> None:
    if len(content) > 1_000_000:
        raise RuntimeError("Downloaded report is too large.")
    try:
        report = json.loads(content.decode("utf-8"), object_pairs_hook=unique_fields)
    except (ValueError, UnicodeError) as exc:
        raise RuntimeError("Downloaded report is not valid unique-key UTF-8 JSON.") from exc
    if json.dumps(report, sort_keys=True) != json.dumps(EXPECTED, sort_keys=True):
        raise RuntimeError("Downloaded report failed exact synthetic attendance/cost verification.")


def verify_png(content: bytes) -> None:
    if len(content) > 10_000_000 or not content.startswith(b"\x89PNG\r\n\x1a\n"):
        raise RuntimeError("Downloaded chart is not a PNG or is too large.")
    offset, kinds, image_data = 8, [], bytearray()
    row_bytes, height = 0, 0
    while offset + 12 <= len(content):
        size = struct.unpack(">I", content[offset:offset + 4])[0]
        kind = content[offset + 4:offset + 8]
        end = offset + 12 + size
        if end > len(content):
            raise RuntimeError("Truncated PNG chart.")
        data = content[offset + 8:end - 4]
        crc = struct.unpack(">I", content[end - 4:end])[0]
        if zlib.crc32(kind + data) & 0xFFFFFFFF != crc:
            raise RuntimeError("PNG chart checksum failed.")
        if not kinds:
            if kind != b"IHDR" or size != 13:
                raise RuntimeError("Invalid PNG chart header.")
            width, height = struct.unpack(">II", data[:8])
            if not (64 <= width <= 4096 and 64 <= height <= 4096):
                raise RuntimeError("PNG chart dimensions out of bounds.")
            depth, color, compression, filtering, interlace = data[8:]
            if depth != 8 or color not in (2, 6) or (compression, filtering, interlace) != (0, 0, 0):
                raise RuntimeError("Expected a noninterlaced 8-bit RGB/RGBA PNG chart.")
            row_bytes = 1 + width * (3 if color == 2 else 4)
        if kind == b"IDAT":
            image_data.extend(data)
        kinds.append(kind)
        offset = end
        if kind == b"IEND":
            break
    if offset != len(content) or b"IDAT" not in kinds or kinds[-1:] != [b"IEND"]:
        raise RuntimeError("Incomplete PNG chart.")
    decoder = zlib.decompressobj()
    try:
        pixels = decoder.decompress(bytes(image_data), row_bytes * height + 1)
    except zlib.error as exc:
        raise RuntimeError("Invalid PNG compressed image data.") from exc
    if (
        len(pixels) != row_bytes * height or not decoder.eof or decoder.unused_data
        or any(pixels[row] > 4 for row in range(0, len(pixels), row_bytes))
    ):
        raise RuntimeError("Invalid PNG image scanlines.")


def upload_skill(project: AIProjectClient, name: str, package: bytes, timeout: float = 180):
    deadline = monotonic() + timeout
    while True:
        try:
            return project.beta.skills.create_from_files(
                name=name,
                content=CreateSkillVersionFromFilesBody(files=[("csv-analyst.zip", BytesIO(package))]),
            )
        except ResourceNotFoundError as error:
            remaining = deadline - monotonic()
            if (error.status_code != 404 or error.error is None or
                    error.error.code != "NotFound" or error.error.message != "Project not found" or
                    remaining <= 0):
                raise
            print("Project is not ready; retrying the initial skill upload.", flush=True)
            sleep(min(15, remaining))


def demonstrate(
    project: AIProjectClient, client: OpenAI, deployment: str, *, save_artifacts: bool = False,
) -> None:
    run_id = uuid4().hex
    name = f"csv-analyst-{run_id}"
    package, expected = skill_package(name)
    with cleanup_scope() as cleanup:
        skill = upload_skill(project, name, package)
        cleanup.append((f"skill {name}", partial(project.beta.skills.delete, name=name)))
        print(f"Uploaded skill {name} version {skill.version}", flush=True)
        instructions = downloaded_instructions(
            b"".join(project.beta.skills.download_version(name, skill.version)), expected,
        )
        print(f"Byte-verified pinned skill {skill.version}; application-managed instruction injection.", flush=True)
        uploaded = client.files.create(
            purpose="assistants", file=(f"attendance-{run_id}.csv", CSV_DATA, "text/csv"),
        )
        cleanup.append((f"input file {uploaded.id}", partial(client.files.delete, uploaded.id)))
        print(f"Uploaded synthetic CSV: {uploaded.id}", flush=True)
        container = client.containers.create(
            name=f"analyst-{run_id}", file_ids=[uploaded.id],
            expires_after={"anchor": "last_active_at", "minutes": 20},
        )
        cleanup.append((f"container {container.id}", partial(client.containers.delete, container.id)))
        print(f"Native sandbox: {container.id}", flush=True)
        agent = project.agents.create_version(
            agent_name=f"data-analyst-{run_id}",
            definition=PromptAgentDefinition(
                model=deployment, instructions=instructions,
                tools=[CodeInterpreterTool(container=container.id)],
            ),
        )
        cleanup.append((f"agent {agent.name}/{agent.version}", partial(
            project.agents.delete_version, agent_name=agent.name, agent_version=agent.version,
        )))
        print(f"Agent {agent.name} version {agent.version}", flush=True)
        response = client.responses.create(
            input="Analyze the attached synthetic attendance CSV. Follow the CSV analyst skill "
                  "to execute Python. After saving both files, print CSV_ANALYSIS_EXECUTED in "
                  "the Code Interpreter execution log using Python, and only then provide "
                  "the downloadable report and chart with citations.",
            extra_body={"agent_reference": {
                "name": agent.name, "version": agent.version, "type": "agent_reference",
            }},
            tool_choice="required", include=["code_interpreter_call.outputs"], timeout=TIMEOUT,
        )
        cleanup.append((f"response {response.id}", partial(client.responses.delete, response.id)))
        citations = execution_citations(response, container.id)
        artifacts = {}
        for filename, file_id in citations.items():
            content = client.containers.files.content.retrieve(
                file_id=file_id, container_id=container.id,
            ).read()
            artifacts[filename] = content
        verify_report(artifacts["report.json"])
        verify_png(artifacts["attendance-cost.png"])
        print("Downloaded report verified: 6 rows; 55 attendance instances; 95000 cents.", flush=True)
        print(artifacts["report.json"].decode("utf-8"), flush=True)
        for filename, content in artifacts.items():
            print(f"Downloaded {filename}: {len(content)} bytes; SHA256={sha256(content).hexdigest()}", flush=True)
        if save_artifacts:
            destination = HERE / ".azure" / "artifacts" / run_id
            destination.mkdir(parents=True, exist_ok=False)
            for filename, content in artifacts.items():
                (destination / filename).write_bytes(content)
            print(f"Saved verified user artifacts: {destination}", flush=True)


def main(argv: Sequence[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-endpoint", default=os.getenv("FOUNDRY_PROJECT_ENDPOINT"))
    parser.add_argument("--deployment", default=os.getenv("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini"))
    parser.add_argument("--save-artifacts", action="store_true", help="Keep verified downloads in ignored .azure/artifacts.")
    args = parser.parse_args(argv)
    if not args.project_endpoint or not args.project_endpoint.startswith("https://"):
        parser.error("Set FOUNDRY_PROJECT_ENDPOINT to the Bicep projectEndpoint HTTPS URL.")
    if not args.deployment.strip():
        parser.error("--deployment must not be empty.")
    try:
        with (
            AzureCliCredential(process_timeout=30) as credential,
            AIProjectClient(
                endpoint=args.project_endpoint, credential=credential, allow_preview=True,
                connection_timeout=15, read_timeout=TIMEOUT, retry_total=0,
            ) as project,
            project.get_openai_client(timeout=TIMEOUT, max_retries=0) as client,
        ):
            demonstrate(project, client, args.deployment, save_artifacts=args.save_artifacts)
    except Exception as exc:
        raise RuntimeError(
            "Foundry CSV analyst failed; no local-computation fallback. Check preview Skills, "
            "native Code Interpreter/containers support, model/region, quota and Entra RBAC. "
            f"Service/validation error: {exc}"
        ) from exc


if __name__ == "__main__":
    main()
