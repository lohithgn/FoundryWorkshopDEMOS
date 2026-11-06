"""Local Phi QLoRA: baseline, train, and compare held-out Polish domain answers."""

import argparse
from contextlib import redirect_stdout
from datetime import datetime, timezone
import hashlib
from importlib.metadata import version
import json
from pathlib import Path
import platform
import re
from time import monotonic
from types import SimpleNamespace
import unicodedata


ROOT = Path(__file__).resolve().parent
DATA = ROOT / "polish_culture.json"
CONFIG = ROOT / "polish_lora.json"
REPLAY = ROOT / "polish_replay.json"
CONFIRMATION = ROOT / "polish_confirmation.json"
MODEL = "mlx-community/Phi-3.5-mini-instruct-4bit"
REVISION = "7b2052fd882fe017300d4d42a4eb06a27b816af4"
MAX_TOKENS = 100
STOP_TOKEN = "<|end|>"


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8") as output:
        json.dump(value, output, ensure_ascii=False, indent=2)
        output.write("\n")


def normalize(text: str) -> str:
    text = unicodedata.normalize("NFKD", text.casefold().replace("\u0142", "l"))
    text = "".join(character for character in text if not unicodedata.combining(character))
    return " ".join(re.sub(r"[^a-z0-9]+", " ", text).split())


def score(answer: str, required: list[list[str]], forbidden: list[str]) -> dict:
    text = normalize(answer)
    # Prefix matching allows inflections (pancake/pancakes, smoke/smoked).
    def contains(term: str) -> bool:
        term = normalize(term)
        end = r"\b" if term[-1].isdigit() else ""
        return re.search(r"\b" + re.escape(term) + end, text) is not None
    hits = [any(contains(term) for term in group) for group in required]
    contradictions = [term for term in forbidden if contains(term)]
    return {
        "facts_matched": sum(hits),
        "facts_total": len(hits),
        "missing": [group for group, hit in zip(required, hits) if not hit],
        "forbidden_matches": contradictions,
        "passed": all(hits) and not contradictions,
    }


def load_data() -> dict:
    data = read_json(DATA)
    ids: set[str] = set()
    prompts: set[str] = set()
    for topic in data["topics"]:
        if topic["id"] in ids:
            raise ValueError(f"Duplicate topic: {topic['id']}")
        ids.add(topic["id"])
        if not topic["source"].startswith("https://") or not topic["required"]:
            raise ValueError(f"Missing source or rubric: {topic['id']}")
        if not score(topic["answer"], topic["required"], topic["forbidden"])["passed"]:
            raise ValueError(f"Reference answer fails rubric: {topic['id']}")
        for prompt in [*topic["train"], topic["valid"], topic["test"]]:
            key = normalize(prompt)
            if not key or key in prompts:
                raise ValueError(f"Empty or overlapping split prompt: {prompt}")
            prompts.add(key)
    return data


def evaluation_items() -> list[dict]:
    data = load_data()
    confirmation = read_json(CONFIRMATION)
    questions = confirmation["questions"]
    if [row["id"] for row in questions] != [topic["id"] for topic in data["topics"]]:
        raise ValueError("Confirmation questions must cover every domain topic exactly once.")
    seen = {normalize(question) for topic in data["topics"]
            for question in [*topic["train"], topic["valid"], topic["test"]]}
    seen.update(normalize(item["prompt"]) for item in data["controls"])
    replay = read_json(REPLAY)
    for split in ("train", "valid"):
        for item in replay[split]:
            key = normalize(item["question"])
            if not key or key in seen or not item["answer"].strip():
                raise ValueError("Replay question overlaps an existing split or is empty.")
            seen.add(key)
    items = [topic | {"test": row["question"]} for topic, row in zip(data["topics"], questions)]
    items.extend(confirmation["controls"])
    ids: set[str] = set()
    for item in items:
        question = normalize(item.get("test", item.get("prompt")))
        if not question or question in seen or item["id"] in ids:
            raise ValueError("Confirmation question or ID is empty, duplicated, or leaks into another split.")
        if not item["required"] or any(not group or any(not normalize(term) for term in group)
                                       for group in item["required"]):
            raise ValueError("Confirmation rubric must contain nonempty concept groups.")
        seen.add(question)
        ids.add(item["id"])
    return items


def prepare(output: Path) -> None:
    data = load_data()
    evaluation_items()
    directory = output / "data"
    if directory.exists():
        raise FileExistsError(f"{directory} exists; use a new output directory.")
    directory.mkdir(parents=True)
    for split in ("train", "valid"):
        with (directory / f"{split}.jsonl").open("x", encoding="utf-8") as stream:
            for topic in data["topics"]:
                questions = topic["train"] if split == "train" else [topic["valid"]]
                for question in questions:
                    row = {"messages": [
                        {"role": "system", "content": data["system"]},
                        {"role": "user", "content": question},
                        {"role": "assistant", "content": topic["answer"]},
                    ]}
                    stream.write(json.dumps(row, ensure_ascii=False) + "\n")
            for item in read_json(REPLAY)[split]:
                row = {"messages": [
                    {"role": "system", "content": data["system"]},
                    {"role": "user", "content": item["question"]},
                    {"role": "assistant", "content": item["answer"]},
                ]}
                stream.write(json.dumps(row, ensure_ascii=False) + "\n")
    for source, name in ((DATA, "dataset.json"), (REPLAY, "replay.json"),
                         (CONFIRMATION, "confirmation.json"), (CONFIG, "lora-config.json")):
        write_json(output / name, read_json(source))
    write_json(output / "manifest.json", {
        "model": MODEL, "revision": REVISION,
        "dataset_sha256": digest(DATA), "config_sha256": digest(CONFIG),
        "replay_sha256": digest(REPLAY), "confirmation_sha256": digest(CONFIRMATION),
        "evaluation_suite": "post-exploratory frozen confirmation",
        "train_sha256": digest(directory / "train.jsonl"),
        "valid_sha256": digest(directory / "valid.jsonl"),
        "minimum_accuracy_gain": data["minimum_accuracy_gain"],
        "max_tokens": MAX_TOKENS, "temperature": 0.0, "seed": 42,
        "additional_stop_token": STOP_TOKEN,
        "split_scope": "Unseen questions about taught facts; not unseen facts or entities.",
    })


def check_manifest(output: Path) -> dict:
    manifest = read_json(output / "manifest.json")
    current = {
        "model": MODEL, "revision": REVISION,
        "dataset_sha256": digest(DATA), "config_sha256": digest(CONFIG),
        "replay_sha256": digest(REPLAY), "confirmation_sha256": digest(CONFIRMATION),
        "evaluation_suite": "post-exploratory frozen confirmation",
        "train_sha256": digest(output / "data" / "train.jsonl"),
        "valid_sha256": digest(output / "data" / "valid.jsonl"),
        "max_tokens": MAX_TOKENS, "temperature": 0.0, "seed": 42,
        "additional_stop_token": STOP_TOKEN,
        "minimum_accuracy_gain": load_data()["minimum_accuracy_gain"],
    }
    if any(manifest[key] != value for key, value in current.items()):
        raise ValueError("Experiment inputs changed. Start a new output directory.")
    return manifest


def local_model() -> str:
    from huggingface_hub import snapshot_download

    return snapshot_download(MODEL, revision=REVISION)


def evaluate(output: Path, tuned: bool) -> None:
    manifest = check_manifest(output)
    target = output / ("tuned.json" if tuned else "baseline.json")
    if target.exists():
        raise FileExistsError(f"Refusing to overwrite {target}")
    adapter = output / "adapters"
    if tuned:
        training = read_json(output / "training.json")
        if training["adapter_sha256"] != digest(adapter / "adapters.safetensors"):
            raise ValueError("Adapter differs from the completed training run.")
    import mlx.core as mx
    from mlx_lm import generate, load
    from mlx_lm.sample_utils import make_sampler

    model, tokenizer = load(local_model(), adapter_path=str(adapter) if tuned else None,
                            tokenizer_config={"trust_remote_code": False})
    # This conversion's generation config omits Phi's end-of-turn token.
    tokenizer.add_eos_token(STOP_TOKEN)
    data = load_data()
    rows = []
    started = monotonic()
    for item in evaluation_items():
        mx.random.seed(manifest["seed"])
        question = item.get("test", item.get("prompt"))
        prompt = tokenizer.apply_chat_template([
            {"role": "system", "content": data["system"]},
            {"role": "user", "content": question},
        ], tokenize=False, add_generation_prompt=True)
        answer = generate(model, tokenizer, prompt=prompt, max_tokens=MAX_TOKENS,
                          sampler=make_sampler(temp=0.0), verbose=False)
        result = score(answer, item["required"], item["forbidden"])
        rows.append({
            "id": item["id"], "category": item.get("category", "control"),
            "question": question, "answer": answer,
            "reference": item.get("answer"), **result,
        })
        print(f"{item['id']}: {'PASS' if result['passed'] else 'FAIL'}: {answer}", flush=True)
    write_json(target, {
        "manifest": manifest, "adapter_sha256": digest(adapter / "adapters.safetensors") if tuned else None,
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        "runtime": {name: version(name) for name in ("mlx", "mlx-lm", "transformers")},
        "platform": platform.platform(), "seconds": monotonic() - started, "results": rows,
    })


def train(output: Path) -> None:
    check_manifest(output)
    baseline = read_json(output / "baseline.json")
    if baseline["manifest"] != read_json(output / "manifest.json") or baseline["adapter_sha256"] is not None:
        raise ValueError("A matching unadapted baseline is required before training.")
    if (output / "adapters").exists():
        raise FileExistsError("Adapter directory exists; refusing to overwrite training.")
    from mlx_lm import load
    from mlx_lm.lora import CONFIG_DEFAULTS, train_model
    from mlx_lm.tuner.datasets import load_dataset
    import numpy as np

    model_path = local_model()
    model, tokenizer = load(model_path, tokenizer_config={"trust_remote_code": False})
    # Check token lengths before training; MLX otherwise truncates long examples.
    config = read_json(CONFIG)
    for split in ("train", "valid"):
        for line in (output / "data" / f"{split}.jsonl").read_text(encoding="utf-8").splitlines():
            tokens = tokenizer.apply_chat_template(json.loads(line)["messages"], tokenize=True)
            if len(tokens) > config["max_seq_length"]:
                raise ValueError(f"{split} example exceeds max_seq_length; increase it before baseline.")
    args = SimpleNamespace(**(CONFIG_DEFAULTS | config | {
        "model": model_path, "train": True, "data": str(output / "data"),
        "adapter_path": str(output / "adapters"),
    }))
    np.random.seed(args.seed)
    train_set, valid_set, _ = load_dataset(args, tokenizer)
    started = monotonic()
    print(f"Training {args.iters} steps; progress is saved to {output / 'training.log'}.", flush=True)
    with (output / "training.log").open("x", encoding="utf-8", buffering=1) as log, redirect_stdout(log):
        train_model(args, model, train_set, valid_set)
    write_json(output / "training.json", {
        "trainer": "mlx_lm.lora.train_model (built-in tokenizer; trust_remote_code=False)",
        "config": config, "seconds": monotonic() - started,
        "adapter_sha256": digest(output / "adapters" / "adapters.safetensors"),
        "checkpoint_policy": f"Final fixed {config['iters']}-step adapter; no test-based checkpoint selection.",
    })


def metrics(rows: list[dict], control: bool) -> dict:
    selected = [row for row in rows if (row["category"] == "control") == control]
    if not selected:
        raise ValueError("Missing evaluation rows.")
    return {
        "passed": sum(row["passed"] for row in selected), "total": len(selected),
        "accuracy": sum(row["passed"] for row in selected) / len(selected),
        "fact_coverage": sum(row["facts_matched"] for row in selected) / sum(row["facts_total"] for row in selected),
    }


def compare(output: Path) -> dict:
    manifest = check_manifest(output)
    before, after = (read_json(output / name) for name in ("baseline.json", "tuned.json"))
    if before["manifest"] != manifest or after["manifest"] != manifest:
        raise ValueError("Baseline and tuned experiment inputs differ.")
    if before.get("runtime") != after.get("runtime"):
        raise ValueError("Baseline and tuned library versions differ.")
    if before["adapter_sha256"] is not None or not after["adapter_sha256"]:
        raise ValueError("Expected unadapted baseline and adapted final evaluation.")
    training = read_json(output / "training.json")
    if after["adapter_sha256"] != training["adapter_sha256"]:
        raise ValueError("Evaluation used a different adapter.")
    items = evaluation_items()
    expected = [(item["id"], item.get("test", item.get("prompt"))) for item in items]
    for report in (before, after):
        if [(row["id"], row["question"]) for row in report["results"]] != expected:
            raise ValueError("Evaluation questions are missing or mismatched.")
        for row, item in zip(report["results"], items):
            if row["category"] != item.get("category", "control"):
                raise ValueError("Evaluation category differs from the frozen dataset.")
            rescored = score(row["answer"], item["required"], item["forbidden"])
            if any(row[key] != value for key, value in rescored.items()):
                raise ValueError("Stored score differs from the frozen rubric.")
    base, tuned = (metrics(report["results"], False) for report in (before, after))
    base_control, tuned_control = (metrics(report["results"], True) for report in (before, after))
    gain = tuned["accuracy"] - base["accuracy"]
    passed = gain >= manifest["minimum_accuracy_gain"] and tuned_control["accuracy"] >= base_control["accuracy"]
    result = {
        "baseline": base, "tuned": tuned, "accuracy_gain": gain,
        "baseline_controls": base_control, "tuned_controls": tuned_control,
        "minimum_accuracy_gain": manifest["minimum_accuracy_gain"], "verified_improvement": passed,
        "limitations": [
            "Small lexical concept rubric, not an independent semantic judge.",
            "Held-out phrasing of taught facts, not held-out knowledge.",
            "Six controls cannot establish absence of general capability regressions.",
            "Inspect full answers for contradictions not covered by the rubric.",
        ],
    }
    report_path = output / "comparison.json"
    if report_path.exists():
        if read_json(report_path) != result:
            raise ValueError("Existing comparison differs from recomputed results.")
    else:
        write_json(report_path, result)
    print(json.dumps(result, indent=2))
    if not passed:
        raise RuntimeError("Required domain gain or control non-regression threshold was not met.")
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", required=True, choices=("prepare", "baseline", "train", "evaluate", "compare", "all"))
    parser.add_argument("--output", type=Path, default=ROOT / ".azure" / "polish-phi")
    args = parser.parse_args()
    if args.stage not in ("prepare", "compare") and (platform.system(), platform.machine()) != ("Darwin", "arm64"):
        parser.error("Local MLX training/inference requires an Apple silicon Mac; see requirements-polish.txt.")
    stages = ("prepare", "baseline", "train", "evaluate", "compare") if args.stage == "all" else (args.stage,)
    for stage in stages:
        if stage == "prepare":
            prepare(args.output)
        elif stage == "baseline":
            evaluate(args.output, tuned=False)
        elif stage == "train":
            train(args.output)
        elif stage == "evaluate":
            evaluate(args.output, tuned=True)
        else:
            compare(args.output)


if __name__ == "__main__":
    main()
