"""Explain sampling, compare chat and reasoning, and run a resumable native batch."""

import argparse
import json
import math
import os
from pathlib import Path
import sys
import time
from typing import TYPE_CHECKING, Literal

if TYPE_CHECKING:
    from openai import OpenAI
    from openai.types.chat import ChatCompletion, ChatCompletionCreateParamsNonStreaming


SAMPLING_PROMPT = "Invent one short, playful name for a beginner AI workshop. Return only the name."
COMPARISON_PROMPT = (
    "Tasks A, B, C, D take 4, 3, 5, 2 minutes respectively. B and C must wait "
    "for A; D must wait for both B and C. Two workers are available, each task "
    "needs one worker, and tasks cannot be interrupted. What is the minimum "
    "total duration? Return only the integer number of minutes."
)
BATCH_PROMPTS = (
    "Explain temperature in one sentence for a beginner.",
    "Explain top_p in one sentence for a beginner.",
    "Explain when asynchronous batch processing is useful in one sentence.",
)


def temperature_distribution(probabilities: list[float], temperature: float) -> list[float]:
    if not probabilities or any(not math.isfinite(p) or p <= 0 for p in probabilities):
        raise ValueError("Toy probabilities must be finite and positive.")
    if not math.isclose(sum(probabilities), 1.0):
        raise ValueError("Toy probabilities must sum to one.")
    if not math.isfinite(temperature) or temperature < 0:
        raise ValueError("Temperature must be finite and nonnegative.")
    if temperature == 0:
        winner = max(range(len(probabilities)), key=probabilities.__getitem__)
        return [float(index == winner) for index in range(len(probabilities))]
    logits = [math.log(p) / temperature for p in probabilities]
    weights = [math.exp(value - max(logits)) for value in logits]
    return [weight / sum(weights) for weight in weights]


def nucleus(probabilities: list[float], top_p: float) -> list[int]:
    temperature_distribution(probabilities, 1)
    if not math.isfinite(top_p) or not 0 < top_p <= 1:
        raise ValueError("Toy top_p must be in (0, 1].")
    selected = []
    cumulative = 0.0
    for index in sorted(range(len(probabilities)), key=probabilities.__getitem__, reverse=True):
        selected.append(index)
        cumulative += probabilities[index]
        if cumulative >= top_p:
            break
    return selected


def concepts() -> None:
    probabilities = [0.5, 0.3, 0.15, 0.05]
    print("LOCAL TOY EXAMPLE, not probabilities obtained from a model.")
    print("Next-token candidates A/B/C/D:", probabilities)
    for temperature in (0, 0.2, 1, 2):
        print(f"temperature={temperature}:",
              [round(p, 4) for p in temperature_distribution(probabilities, temperature)])
    for top_p in (0.5, 0.8, 1):
        print(f"top_p={top_p}: keep",
              [chr(65 + index) for index in nucleus(probabilities, top_p)])
    print("Temperature reshapes probabilities; top_p keeps a cumulative-probability nucleus,")
    print("then sampling renormalizes the retained probabilities. Change one knob at a time.")
    print("Low temperature is not a correctness or reproducibility guarantee.")
    print("Chat/reasoning are model choices; batch is asynchronous request scheduling.")
    print("o4-mini uses reasoning_effort, not temperature/top_p; reasoning tokens are billed.")


def chat_request(
    deployment: str, prompt: str, *, temperature: float = 1, top_p: float = 1,
) -> "ChatCompletionCreateParamsNonStreaming":
    return {
        "model": deployment, "messages": [{"role": "user", "content": prompt}],
        "max_tokens": 256, "temperature": temperature, "top_p": top_p,
    }


def reasoning_request(
    deployment: str, effort: Literal["low", "medium", "high"],
) -> "ChatCompletionCreateParamsNonStreaming":
    return {
        "model": deployment, "messages": [{"role": "user", "content": COMPARISON_PROMPT}],
        "max_completion_tokens": 4096, "reasoning_effort": effort,
    }


def show_completion(response: "ChatCompletion", elapsed: float) -> str:
    if not response.choices:
        raise RuntimeError("Service returned no choices.")
    choice = response.choices[0]
    content = choice.message.content
    if choice.finish_reason != "stop" or not content or not content.strip():
        raise RuntimeError(
            f"Incomplete/empty response: finish_reason={choice.finish_reason}, "
            f"refusal={choice.message.refusal}, usage={response.usage}"
        )
    print(f"Model: {response.model} | elapsed: {elapsed:.3f}s | finish: {choice.finish_reason}")
    print("Usage:", response.usage.model_dump() if response.usage else "Not returned by service")
    print("Answer:", content)
    return content.strip()


def sampling(client: "OpenAI", deployment: str, samples: int) -> None:
    for temperature, top_p in ((0, 1), (0.7, 1), (1.4, 1), (1, 0.2), (1, 0.8), (1, 1)):
        print(f"\nSame prompt: temperature={temperature}, top_p={top_p}")
        answers = []
        for _ in range(samples):
            started = time.monotonic()
            response = client.chat.completions.create(
                **chat_request(deployment, SAMPLING_PROMPT, temperature=temperature, top_p=top_p),
            )
            answers.append(show_completion(response, time.monotonic() - started))
        print(f"Distinct answers: {len(set(answers))}/{samples} (illustration, not a benchmark)")


def compare(client: "OpenAI", chat: str, reasoning: str,
            effort: Literal["low", "medium", "high"]) -> None:
    print("\nSame scheduling problem:", COMPARISON_PROMPT)
    for label, request in (
        ("Typical chat (GPT-4.1-mini)", chat_request(chat, COMPARISON_PROMPT, temperature=0)),
        (f"Reasoning (o4-mini, effort={effort})", reasoning_request(reasoning, effort)),
    ):
        print("\n" + label)
        started = time.monotonic()
        response = client.chat.completions.create(**request)
        answer = show_completion(response, time.monotonic() - started)
        print("Matches reference answer 11:", answer == "11")
    print("Reference: A 0-4; B 4-7 and C 4-9; D 9-11. Both models may be correct.")
    print("One problem does not establish quality, latency or cost superiority.")


def batch_rows(deployment: str) -> list[dict]:
    return [
        {"custom_id": f"task-{index}", "method": "POST", "url": "/v1/chat/completions",
         "body": chat_request(deployment, prompt, temperature=0)}
        for index, prompt in enumerate(BATCH_PROMPTS)
    ]


def validate_batch_results(text: str) -> list[dict]:
    rows = [json.loads(line) for line in text.splitlines() if line.strip()]
    expected = {f"task-{index}" for index in range(len(BATCH_PROMPTS))}
    seen = set()
    for row in rows:
        custom_id = row.get("custom_id")
        if custom_id not in expected or custom_id in seen:
            raise RuntimeError(f"Unexpected or duplicate batch custom_id: {custom_id}")
        seen.add(custom_id)
        response = row.get("response") or {}
        if row.get("error") or response.get("status_code") != 200:
            raise RuntimeError(f"Batch request failed: {row}")
        body = response.get("body") or {}
        choices = body.get("choices") or []
        if not choices:
            raise RuntimeError(f"Missing choices for {custom_id}")
        choice = choices[0]
        content = (choice.get("message") or {}).get("content")
        if choice.get("finish_reason") != "stop" or not isinstance(content, str) or not content.strip():
            raise RuntimeError(f"Incomplete/empty batch answer for {custom_id}: {choice}")
    if seen != expected:
        raise RuntimeError(f"Missing batch results: {sorted(expected - seen)}")
    return sorted(rows, key=lambda row: row["custom_id"])


def run_batch(client: "OpenAI", endpoint: str, deployment: str, state_dir: Path,
              batch_id: str | None, wait_seconds: int) -> None:
    state_dir.mkdir(parents=True, exist_ok=True)
    state_path = state_dir / "batch-state.json"
    if not batch_id and state_path.exists():
        state = json.loads(state_path.read_text())
        if state["endpoint"] != endpoint or state["deployment"] != deployment:
            raise ValueError("Saved batch belongs to a different endpoint/deployment; use a different --state-dir.")
        batch_id = state["batch_id"]
    if not batch_id:
        payload = "".join(json.dumps(row) + "\n" for row in batch_rows(deployment)).encode()
        uploaded = client.files.create(file=("basics.jsonl", payload, "application/jsonl"), purpose="batch")
        print("Uploaded input file:", uploaded.id, flush=True)
        job = client.batches.create(
            input_file_id=uploaded.id, endpoint="/chat/completions", completion_window="24h",
        )
        batch_id = job.id
        state_path.write_text(json.dumps({
            "endpoint": endpoint, "deployment": deployment,
            "batch_id": batch_id, "input_file_id": uploaded.id,
        }, indent=2) + "\n")
    print("Batch ID:", batch_id, "| resume with --section batch; no new submission.", flush=True)
    deadline = time.monotonic() + wait_seconds
    while True:
        job = client.batches.retrieve(batch_id)
        print("Batch status:", job.status, "| request counts:", job.request_counts, flush=True)
        if job.status == "completed":
            if job.error_file_id:
                errors = client.files.content(job.error_file_id).text
                (state_dir / "batch-errors.jsonl").write_text(errors)
                raise RuntimeError(f"Batch contains failed requests; see {state_dir / 'batch-errors.jsonl'}")
            if not job.output_file_id:
                raise RuntimeError("Completed batch has no output file.")
            text = client.files.content(job.output_file_id).text
            (state_dir / "batch-output.jsonl").write_text(text)
            for row in validate_batch_results(text):
                body = row["response"]["body"]
                print(row["custom_id"], "| model:", body.get("model"),
                      "| usage:", body.get("usage", "Not returned by service"))
                print("Answer:", body["choices"][0]["message"]["content"])
            print("PASS: all three native batch requests completed and validated.")
            return
        if job.status in ("failed", "expired", "cancelled"):
            raise RuntimeError(f"Batch {batch_id} ended as {job.status}: {job.errors}")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise RuntimeError(
                f"Batch {batch_id} is still {job.status}; NOT a completed live test. "
                "Rerun --section batch later to resume. The service target window is 24 hours."
            )
        time.sleep(min(10, remaining))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--section", choices=("concepts", "sampling", "compare", "batch", "all"), default="all")
    parser.add_argument("--endpoint", default=os.getenv("AZURE_OPENAI_ENDPOINT"))
    parser.add_argument("--deployment", default=os.getenv("AZURE_OPENAI_DEPLOYMENT", "basics-chat"))
    parser.add_argument("--reasoning-deployment", default=os.getenv("AZURE_OPENAI_REASONING_DEPLOYMENT", "basics-reasoning"))
    parser.add_argument("--batch-deployment", default=os.getenv("AZURE_OPENAI_BATCH_DEPLOYMENT", "basics-batch"))
    parser.add_argument("--samples", type=int, choices=range(1, 6), default=3)
    parser.add_argument("--reasoning-effort", choices=("low", "medium", "high"), default="medium")
    parser.add_argument("--batch-id", help="Inspect an existing lab batch without submitting another.")
    parser.add_argument("--wait-seconds", type=int, default=120, help="Bound batch polling; 0 checks once.")
    parser.add_argument("--state-dir", type=Path, default=Path(__file__).parent / ".azure")
    args = parser.parse_args(argv)
    if args.wait_seconds < 0:
        parser.error("--wait-seconds must be nonnegative.")
    if args.batch_id is not None and not args.batch_id.strip():
        parser.error("--batch-id must be nonempty.")
    concepts()
    if args.section == "concepts":
        return 0
    if not args.endpoint or not args.endpoint.strip():
        parser.error("Set AZURE_OPENAI_ENDPOINT or pass --endpoint.")
    required = [args.deployment] if args.section in ("sampling", "compare", "all") else []
    if args.section in ("compare", "all"):
        required.append(args.reasoning_deployment)
    if args.section in ("batch", "all"):
        required.append(args.batch_deployment)
    if any(not name or not name.strip() for name in required):
        parser.error("Selected model deployment names must be nonempty.")

    from azure.identity import DefaultAzureCredential, get_bearer_token_provider
    from openai import OpenAI

    with DefaultAzureCredential(exclude_interactive_browser_credential=True) as credential:
        token = get_bearer_token_provider(credential, "https://cognitiveservices.azure.com/.default")
        endpoint = args.endpoint.rstrip("/")
        with OpenAI(base_url=endpoint + "/openai/v1/", api_key=token,
                    max_retries=0, timeout=120) as client:
            if args.section in ("sampling", "all"):
                sampling(client, args.deployment, args.samples)
            if args.section in ("compare", "all"):
                compare(client, args.deployment, args.reasoning_deployment, args.reasoning_effort)
            if args.section in ("batch", "all"):
                run_batch(client, endpoint, args.batch_deployment, args.state_dir,
                          args.batch_id, args.wait_seconds)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
