"""Generate a versioned dataset, evaluate real tool traces, and run a native optimizer."""

import argparse
import json
import math
import os
from pathlib import Path
from time import monotonic, sleep
from uuid import uuid4

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    AgentOptimizationCandidateSearchConfiguration, AgentOptimizationConfiguration,
    AgentOptimizationEvaluationConfiguration, AgentOptimizationEvaluator,
    AgentOptimizationFoundryAgentTargetConfiguration, AgentOptimizationJob,
    AgentOptimizationModelConfiguration, AgentOptimizationSpace,
    AgentOptimizationTargetCompletionDatasetReferenceDataSource,
    AgentOptimizationTargetCompletionEvaluationSet, AgentOptimizationTargetCompletionInlineDataSource,
    AgentOptimizationTargetCompletionTestCase, DataGenerationModelOptions,
    EvaluationDataGenerationJobInputs, EvaluationDataGenerationJobOutputConfiguration,
    EvaluationModelConfiguration, PromptAgentDefinition,
    PromptDataGenerationJobSource, SimpleQnADataGenerationJobConfiguration,
    TestingCriterionAzureAIEvaluator,
)
from azure.identity import AzureCliCredential, get_bearer_token_provider
from openai import OpenAI

ROOT = Path(__file__).resolve().parent
STATE = ROOT / ".azure"
POLICY = "Synthetic workshop catalog: Atlas costs USD 25. Boreal costs USD 40. Prices include materials. Refunds are allowed within 7 days."
TOOL = {
    "type": "function", "name": "lookup_price",
    "description": "Read the synthetic catalog price for one workshop. Has no side effects.",
    "parameters": {"type": "object", "properties": {"workshop": {"type": "string", "enum": ["Atlas", "Boreal"]}},
                   "required": ["workshop"], "additionalProperties": False},
    "strict": True,
}
TRAINING = [
    {"query": "What is the Atlas workshop price?", "ground_truth": "Atlas costs USD 25."},
    {"query": "What is the Boreal workshop price?", "ground_truth": "Boreal costs USD 40."},
]
HELDOUT = [{"query": "What is the refund deadline?", "ground_truth": "Refunds are allowed within 7 days."}]
WAIT_SECONDS = 600
SECURITY_TAGS = {"SecurityControl": "Ignore", "SecurityExemption": "StorageAccountKeyAccess"}
BASELINE_INSTRUCTIONS = (
    "For every catalog question, reply only 'Please consult the catalog.' "
    "Available catalog facts: " + POLICY
)


def settings() -> dict:
    path = STATE / "deployment.json"
    saved = json.loads(path.read_text()).get("outputs", {}) if path.exists() else {}
    config = {key: value["value"] for key, value in saved.items()}
    for output, variable in (
        ("endpoint", "AZURE_OPENAI_ENDPOINT"), ("projectEndpoint", "FOUNDRY_PROJECT_ENDPOINT"),
        ("deployment", "AZURE_OPENAI_DEPLOYMENT"), ("optimizationDeployment", "FOUNDRY_OPTIMIZATION_DEPLOYMENT"),
    ):
        if os.getenv(variable):
            config[output] = os.environ[variable]
    for key in ("endpoint", "projectEndpoint", "deployment", "optimizationDeployment"):
        if not config.get(key):
            raise RuntimeError(f"BLOCKED: missing {key}; deploy with python3 deploy.py --demo 44")
    return config


def save(report: dict) -> None:
    STATE.mkdir(exist_ok=True)
    pending = STATE / "live-report.pending"
    pending.write_text(json.dumps(report, indent=2, default=str) + "\n")
    pending.replace(STATE / "live-report.json")


def poll_job(get_job, cancel_job, job_id: str, report: dict, key: str, timeout: int = WAIT_SECONDS):
    deadline = monotonic() + timeout
    while monotonic() < deadline:
        job = get_job(job_id)
        report[key] = job.as_dict()
        save(report)
        if job.status == "succeeded":
            return job
        if job.status in ("failed", "cancelled"):
            raise RuntimeError(f"Native {key} {job.status}: {job.as_dict().get('error')}")
        sleep(min(10, max(0, deadline - monotonic())))
    cancelled = cancel_job(job_id)
    report[key + "_cancel_requested"] = cancelled.as_dict() if hasattr(cancelled, "as_dict") else True
    save(report)
    raise TimeoutError(f"Native {key} exceeded {timeout}s; cancellation requested, never resubmitted.")


def tag_generated_dataset(project, name: str, version: str, report: dict) -> dict:
    asset = project.datasets.get(name=name, version=version)
    before = asset.as_dict()
    report["generated_dataset_before_tag_update"] = before
    if not before.get("id") or not before.get("version"):
        raise RuntimeError("Generated cloud dataset lacks a service id or immutable version.")
    if any(before.get("tags", {}).get(k) != v for k, v in SECURITY_TAGS.items()):
        asset.tags = {**(asset.tags or {}), **SECURITY_TAGS}
        project.datasets.create_or_update(name=name, version=version, dataset_version=asset)
        report["dataset_tag_update_submitted"] = True
    dataset = project.datasets.get(name=name, version=version).as_dict()
    report["generated_dataset"] = dataset
    save(report)
    if any(dataset.get("tags", {}).get(k) != v for k, v in SECURITY_TAGS.items()):
        raise RuntimeError("Generated cloud dataset did not retain mandatory security tags after native metadata update.")
    for key in ("id", "name", "version", "type", "data_uri", "dataUri"):
        if dataset.get(key) != before.get(key):
            raise RuntimeError(f"Dataset metadata update unexpectedly changed immutable {key}.")
    return dataset


def generate_dataset(project, config: dict, report: dict) -> dict:
    name = "catalog-qna-" + uuid4().hex[:10]
    # The generator cannot see the separate refund holdout.
    job = EvaluationDataGenerationJobInputs(
        name=name,
        sources=[PromptDataGenerationJobSource(
            description="Synthetic pricing-only training source",
            prompt="Synthetic workshop catalog: Atlas costs USD 25. Boreal costs USD 40.",
        )],
        generation_configuration=SimpleQnADataGenerationJobConfiguration(
            max_samples=2, model_options=DataGenerationModelOptions(model=config["deployment"]),
        ),
        output_configuration=EvaluationDataGenerationJobOutputConfiguration(
            name=name, description="Exactly two synthetic pricing training samples; refund holdout excluded.",
            tags={"lab": "44", "split": "training", "SecurityControl": "Ignore",
                  "SecurityExemption": "StorageAccountKeyAccess"},
        ),
    )
    poller = project.datasets.begin_create_generation_job(job=job, polling=False)
    job_id = poller.details.get("job_id")
    if not job_id:
        raise RuntimeError("Native generation response has no job id.")
    report["generation_job_id"] = job_id
    save(report)
    result = poll_job(project.datasets.get_generation_job, project.datasets.cancel_generation_job,
                      job_id, report, "generation_job")
    outputs = result.as_dict().get("result", {}).get("outputs", [])
    datasets = [o for o in outputs if o.get("name") and o.get("version")]
    if len(datasets) != 1:
        raise RuntimeError(f"Native generation did not produce one immutable dataset version: {outputs}")
    dataset = tag_generated_dataset(project, datasets[0]["name"], datasets[0]["version"], report)
    report["generated_dataset"] = dataset
    report["dataset_status"] = "PASS"
    save(report)
    return dataset


def lookup_price(arguments: dict) -> dict:
    workshop = arguments.get("workshop")
    prices = {"Atlas": 25, "Boreal": 40}
    if workshop not in prices:
        raise ValueError("Unknown synthetic workshop.")
    return {"workshop": workshop, "currency": "USD", "price": prices[workshop], "context": POLICY}


def actual_tool_trace(ai, model: str, row: dict) -> dict:
    response = ai.responses.create(
        model=model, input=row["query"], tools=[TOOL], max_output_tokens=256,
        instructions="Always use lookup_price exactly once to answer pricing questions. Answer only from its result.",
    )
    calls = [item for item in response.output if item.type == "function_call"]
    if len(calls) != 1 or calls[0].name != "lookup_price":
        raise RuntimeError("Real agent did not issue exactly one read-only catalog tool call.")
    call = calls[0]
    arguments = json.loads(call.arguments)
    output = lookup_price(arguments)
    completed = ai.responses.create(
        previous_response_id=response.id,
        input=[{"type": "function_call_output", "call_id": call.call_id, "output": json.dumps(output)}],
        model=model, tools=[TOOL], max_output_tokens=256,
    )
    if completed.status != "completed" or not completed.output_text:
        raise RuntimeError("Real tool-assisted response did not complete.")
    return {
        "query": row["query"], "ground_truth": row["ground_truth"], "context": POLICY,
        "response": completed.output_text, "tool_definitions": [TOOL],
        "tool_calls": [{"type": "tool_call", "tool_call_id": call.call_id, "name": call.name, "arguments": arguments}],
        "response_id": completed.id, "tool_response_id": response.id,
    }


def criteria(model: str) -> list:
    return [
        TestingCriterionAzureAIEvaluator(
            type="azure_ai_evaluator", name="groundedness", evaluator_name="builtin.groundedness",
            initialization_parameters={"model": model},
            data_mapping={"query": "{{item.query}}", "response": "{{item.response}}", "context": "{{item.context}}",
                          "tool_definitions": "{{item.tool_definitions}}"},
        ),
        TestingCriterionAzureAIEvaluator(
            type="azure_ai_evaluator", name="relevance", evaluator_name="builtin.relevance",
            initialization_parameters={"model": model},
            data_mapping={"query": "{{item.query}}", "response": "{{item.response}}"},
        ),
        TestingCriterionAzureAIEvaluator(
            type="azure_ai_evaluator", name="tool_call_accuracy", evaluator_name="builtin.tool_call_accuracy",
            initialization_parameters={"deployment_name": model},
            data_mapping={"query": "{{item.query}}", "response": "{{item.response}}",
                          "tool_definitions": "{{item.tool_definitions}}", "tool_calls": "{{item.tool_calls}}"},
        ),
    ]


def verify_evaluator_results(items: list[dict], expected_rows: int) -> None:
    if len(items) != expected_rows:
        raise RuntimeError("Cloud evaluation output count differs from the actual trace dataset.")
    required = {"groundedness", "relevance", "tool_call_accuracy"}
    for item in items:
        results = item.get("results", [])
        names = {
            r.get("name") for r in results
            if isinstance(r.get("score"), (int, float)) and math.isfinite(r["score"]) and not r.get("error")
        }
        if not required <= names:
            raise RuntimeError(f"Cloud evaluators are unavailable or missing scores: {results}")


def cloud_evaluation(project, config: dict, report: dict) -> None:
    with AzureCliCredential() as credential, OpenAI(
        base_url=config["endpoint"].rstrip("/") + "/openai/v1/",
        api_key=get_bearer_token_provider(credential, "https://ai.azure.com/.default"),
        timeout=120, max_retries=1,
    ) as inference:
        rows = report.get("actual_agent_traces")
        if rows:
            for row in rows:
                response = inference.responses.retrieve(row["response_id"])
                if response.status != "completed" or response.output_text != row["response"]:
                    raise RuntimeError("Saved trace does not match its actual native response read-back.")
            report["saved_trace_readback_verified"] = True
        else:
            rows = [actual_tool_trace(inference, config["deployment"], row) for row in TRAINING]
    with project.get_openai_client(timeout=120, max_retries=1) as ai:
        report["actual_agent_traces"] = rows
        report["tool_trace_source"] = "Actual deployed-model Responses API tool calls, not an agent-reference endpoint."
        save(report)
        schema = {
            "type": "object", "properties": {
                "query": {"type": "string"}, "response": {"type": "string"},
                "context": {"type": "string"}, "ground_truth": {"type": "string"},
                "tool_definitions": {"type": "array", "items": {"type": "object"}},
                "tool_calls": {"type": "array", "items": {"type": "object"}},
            }, "required": ["query", "response", "context", "tool_definitions", "tool_calls"],
        }
        evaluation = ai.evals.create(
            name="44 native evaluator breadth",
            data_source_config={"type": "custom", "item_schema": schema, "include_sample_schema": True},
            testing_criteria=criteria(config["deployment"]),
        )
        report["evaluation"] = evaluation.model_dump(warnings=False)
        run = ai.evals.runs.create(
            eval_id=evaluation.id, name="two-real-tool-traces",
            data_source={"type": "jsonl", "source": {"type": "file_content", "content": [{"item": r} for r in rows]}},
        )
        report["evaluation_run"] = run.model_dump()
        save(report)
        deadline = monotonic() + WAIT_SECONDS
        while run.status not in ("completed", "failed", "canceled"):
            if monotonic() >= deadline:
                ai.evals.runs.cancel(run.id, eval_id=evaluation.id)
                raise TimeoutError("Cloud evaluator run exceeded its bound; cancellation requested.")
            sleep(10)
            run = ai.evals.runs.retrieve(run.id, eval_id=evaluation.id)
            report["evaluation_run"] = run.model_dump()
            save(report)
        if run.status != "completed":
            raise RuntimeError(f"Native evaluator run failed: {run.model_dump().get('error')}")
        items = [item.model_dump() for item in ai.evals.runs.output_items.list(run.id, eval_id=evaluation.id)]
        report["evaluation_output_items"] = items
        verify_evaluator_results(items, len(rows))
        report["evaluator_status"] = "PASS"
    save(report)


def evaluation_set(rows: list[dict]):
    return AgentOptimizationTargetCompletionEvaluationSet(
        source=AgentOptimizationTargetCompletionInlineDataSource(
            test_cases=[AgentOptimizationTargetCompletionTestCase(**row) for row in rows],
        ),
    )


def optimization_config(model: str, dataset: dict | None = None):
    training = evaluation_set(TRAINING)
    if dataset:
        training = AgentOptimizationTargetCompletionEvaluationSet(
            source=AgentOptimizationTargetCompletionDatasetReferenceDataSource(
                name=dataset["name"], version=str(dataset["version"]),
            ),
        )
    return AgentOptimizationConfiguration(
        evaluation_configuration=AgentOptimizationEvaluationConfiguration(
            training_set=training, validation_set=evaluation_set(HELDOUT),
            evaluators=[AgentOptimizationEvaluator(name="builtin.relevance")],
            evaluation_model=EvaluationModelConfiguration(model=model), max_concurrent_agent_runs=1,
        ),
        candidate_search_configuration=AgentOptimizationCandidateSearchConfiguration(max_candidates=1),
        agent_optimization_space=AgentOptimizationSpace(target_attributes=["instructions"]),
    )


def native_heldout_evaluation(project, config: dict, report: dict, candidates: list[dict], summary: dict) -> dict:
    baseline = next(c for c in candidates if c["candidate_id"] == summary["baseline_id"])
    alternatives = [c for c in candidates if c["candidate_id"] != baseline["candidate_id"] and c["status"] == "completed"]
    if not alternatives:
        failures = [{"candidate_id": c.get("candidate_id"), "status": c.get("status"), "error": c.get("error")}
                    for c in candidates if c.get("candidate_id") != baseline["candidate_id"]]
        raise RuntimeError(f"Native optimizer produced no completed candidate: summary={summary}; candidates={failures}")
    candidate = max(alternatives, key=lambda c: c["evaluation"]["score"])
    mutation = next((m for m in candidate.get("output", {}).get("mutations", [])
                     if m.get("type") == "instructions" and m.get("value")), None)
    if not mutation:
        raise RuntimeError("Native candidate has no generated instruction mutation to evaluate.")
    baseline_prompt = report["optimizer_baseline_agent"]["definition"]["instructions"]
    rows = []
    with AzureCliCredential() as credential, OpenAI(
        base_url=config["endpoint"].rstrip("/") + "/openai/v1/",
        api_key=get_bearer_token_provider(credential, "https://ai.azure.com/.default"),
        timeout=120, max_retries=1,
    ) as inference:
        for target, prompt in ((baseline, baseline_prompt), (candidate, mutation["value"])):
            response = inference.responses.create(
                model=config["deployment"], instructions=prompt, input=HELDOUT[0]["query"], max_output_tokens=256,
            )
            if response.status != "completed" or not response.output_text:
                raise RuntimeError("Actual native baseline/candidate held-out response did not complete.")
            rows.append({**HELDOUT[0], "candidate_id": target["candidate_id"],
                         "response": response.output_text, "response_id": response.id})
    report["explicit_holdout_responses"] = rows
    save(report)
    with project.get_openai_client(timeout=120, max_retries=1) as ai:
        evaluation = ai.evals.create(
            name="44 explicit native baseline/candidate held-out gate",
            data_source_config={"type": "custom", "include_sample_schema": True, "item_schema": {
                "type": "object", "properties": {
                    key: {"type": "string"} for key in ("query", "ground_truth", "response", "response_id", "candidate_id")
                }, "required": ["query", "response", "response_id", "candidate_id"],
            }},
            testing_criteria=[TestingCriterionAzureAIEvaluator(
                type="azure_ai_evaluator", name="relevance", evaluator_name="builtin.relevance",
                initialization_parameters={"model": config["deployment"]},
                data_mapping={"query": "{{item.query}}", "response": "{{item.response}}"},
            )],
        )
        run = ai.evals.runs.create(
            eval_id=evaluation.id, name="same-disjoint-refund-question-both-configurations",
            data_source={"type": "jsonl", "source": {"type": "file_content", "content": [{"item": r} for r in rows]}},
        )
        report["explicit_holdout_eval"] = evaluation.model_dump(warnings=False)
        deadline = monotonic() + WAIT_SECONDS
        while run.status not in ("completed", "failed", "canceled"):
            report["explicit_holdout_run"] = run.model_dump()
            save(report)
            if monotonic() >= deadline:
                ai.evals.runs.cancel(run.id, eval_id=evaluation.id)
                raise TimeoutError("Native held-out gate exceeded its bound; cancellation requested.")
            sleep(10)
            run = ai.evals.runs.retrieve(run.id, eval_id=evaluation.id)
        report["explicit_holdout_run"] = run.model_dump()
        if run.status != "completed":
            raise RuntimeError(f"Native held-out evaluator failed: {run.model_dump().get('error')}")
        items = [x.model_dump() for x in ai.evals.runs.output_items.list(run.id, eval_id=evaluation.id)]
        report["explicit_holdout_output_items"] = items
    validation = {"eval_id": evaluation.id, "eval_run_id": run.id, "query": HELDOUT[0]["query"]}
    if len(items) != 2:
        raise RuntimeError("Native held-out evaluation must score exactly one baseline and one candidate response.")
    for item in items:
        source = item.get("datasource_item", {})
        if source.get("query") != HELDOUT[0]["query"]:
            raise RuntimeError("Native gate scored training data instead of the actual refund holdout.")
        target = "baseline" if source.get("candidate_id") == baseline["candidate_id"] else "candidate"
        expected = baseline if target == "baseline" else candidate
        expected_row = next(r for r in rows if r["candidate_id"] == expected["candidate_id"])
        if source.get("candidate_id") != expected["candidate_id"] or source.get("response_id") != expected_row["response_id"]:
            raise RuntimeError("Native held-out output is not linked to the correct returned configuration's response.")
        result = next((r for r in item.get("results", []) if r.get("name") == "relevance"), {})
        score = result.get("score")
        if not isinstance(score, (int, float)) or not math.isfinite(score) or not 1 <= score <= 5 or result.get("error"):
            raise RuntimeError("Native held-out relevance evaluator did not return a valid 1–5 score.")
        validation[target] = {"candidate_id": expected["candidate_id"], "response_id": source["response_id"],
                              "raw_score": score, "score": score / 5}
    if not {"baseline", "candidate"} <= validation.keys():
        raise RuntimeError("Native held-out output lacks a distinct baseline and candidate.")
    report["native_holdout_validation"] = validation
    save(report)
    return validation


def heldout_gate(candidates: list[dict], summary: dict, validation: dict) -> dict:
    baseline_id = summary.get("baseline_id")
    baseline = next((c for c in candidates if c.get("candidate_id") == baseline_id), None)
    alternatives = [c for c in candidates if c.get("candidate_id") != baseline_id and c.get("status") == "completed"]
    if not baseline or baseline.get("status") != "completed" or not alternatives:
        raise RuntimeError("Native optimizer did not actually compare a baseline and generated candidate.")
    for candidate in [baseline, *alternatives]:
        evaluation = candidate.get("evaluation", {})
        score = evaluation.get("score")
        if not isinstance(score, (int, float)) or not math.isfinite(score) or not 0 <= score <= 1:
            raise RuntimeError("Native held-out evaluation lacks a bounded numeric score.")
        if not evaluation.get("eval_id") or not evaluation.get("eval_run_id"):
            raise RuntimeError("Native candidate score lacks linked cloud evaluation evidence.")
    candidate = max(alternatives, key=lambda c: c["evaluation"]["score"])
    mutations = candidate.get("output", {}).get("mutations", [])
    if not any(m.get("type") == "instructions" and m.get("value") for m in mutations):
        raise RuntimeError("Optimizer candidate has no native generated instruction mutation.")
    if not validation.get("eval_id") or not validation.get("eval_run_id") or validation.get("query") != HELDOUT[0]["query"]:
        raise RuntimeError("Gate requires a separate actual native held-out evaluation, not training-screen scores.")
    if validation.get("baseline", {}).get("candidate_id") != baseline_id or \
       validation.get("candidate", {}).get("candidate_id") != candidate["candidate_id"]:
        raise RuntimeError("Held-out evaluation targets do not match the actual native baseline/candidate.")
    baseline_score = validation["baseline"].get("score")
    candidate_score = validation["candidate"].get("score")
    for target in ("baseline", "candidate"):
        score = validation[target].get("score")
        if not isinstance(score, (int, float)) or not math.isfinite(score) or not 0 <= score <= 1 or \
           not validation[target].get("response_id"):
            raise RuntimeError("Native held-out score or response link is missing.")
    approved = candidate_score > baseline_score and candidate_score >= 0.8
    return {"decision": "APPROVE" if approved else "REJECT", "baseline_score": baseline_score,
            "candidate_score": candidate_score, "candidate_id": candidate["candidate_id"],
            "heldout_source": "Explicit native cloud evaluation of both returned configurations on the same disjoint refund query.",
            "native_validation_eval_id": validation["eval_id"], "native_validation_run_id": validation["eval_run_id"],
            "promoted": False}


def native_optimizer(project, config: dict, dataset: dict | None, report: dict) -> None:
    name = "optimizer-agent-" + uuid4().hex[:10]
    agent = project.agents.create_version(
        agent_name=name,
        definition=PromptAgentDefinition(
            model=config["deployment"], instructions=BASELINE_INSTRUCTIONS,
        ),
    )
    job_id = None
    try:
        job = AgentOptimizationJob(
            display_name="44 one-candidate bounded native optimization",
            target_configuration=AgentOptimizationFoundryAgentTargetConfiguration(name=name, version=agent.version),
            optimization_model_configuration=AgentOptimizationModelConfiguration(model=config["optimizationDeployment"]),
            optimization_configuration=optimization_config(config["deployment"], dataset),
        )
        report["optimizer_request"] = job.as_dict()
        report["optimizer_baseline_agent"] = agent.as_dict()
        report["optimizer_dataset_mode"] = "generated_versioned" if dataset else "documented_inline_fallback"
        save(report)
        poller = project.agents.begin_create_optimization_job(job=job, polling=False)
        job_id = poller.details.get("job_id")
        if not job_id:
            raise RuntimeError("Native optimizer response has no job id.")
        report["optimizer_job_id"] = job_id
        save(report)
        result = poll_job(project.agents.get_optimization_job, project.agents.cancel_optimization_job,
                          job_id, report, "optimization_job")
        candidates = [
            project.agents.get_optimization_candidate(job_id=job_id, candidate_id=c.candidate_id).as_dict()
            for c in project.agents.list_optimization_candidates(job_id=job_id, limit=5)
        ]
        report["optimizer_candidates"] = candidates
        summary = result.as_dict().get("result", {}).get("candidate_summary", {})
        report["optimizer_native_outcome"] = {
            "job_status": result.status, "termination_reason": result.as_dict().get("result", {}).get("termination_reason"),
            "candidate_summary": summary,
            "candidate_failures": [{"candidate_id": c.get("candidate_id"), "status": c.get("status"),
                                    "error": c.get("error")} for c in candidates if c.get("status") != "completed"],
        }
        save(report)
        validation = native_heldout_evaluation(project, config, report, candidates, summary)
        report["heldout_gate"] = heldout_gate(candidates, summary, validation)
        report["optimizer_status"] = "PASS"
    finally:
        if job_id:
            current = project.agents.get_optimization_job(job_id)
            if current.status in ("queued", "in_progress"):
                project.agents.cancel_optimization_job(job_id)
                report["optimizer_cancel_requested"] = True
        project.agents.delete(agent_name=name)
        report["optimizer_agent_deleted"] = True
        save(report)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight", action="store_true")
    parser.add_argument("--stage", choices=["all", "dataset", "evaluator", "optimizer", "holdout"], default="all",
                        help="Retry only a specific native stage, preserving other stage evidence and avoiding job resubmission.")
    args = parser.parse_args(argv)
    report = {"status": "BLOCKED", "dataset_status": "NOT_RUN", "evaluator_status": "NOT_RUN",
              "optimizer_status": "NOT_RUN", "blockers": []}
    if args.stage != "all" and (STATE / "live-report.json").exists():
        report.update(json.loads((STATE / "live-report.json").read_text()))
    try:
        config = settings()
        with AzureCliCredential() as credential, AIProjectClient(
            config["projectEndpoint"], credential, allow_preview=True,
            connection_timeout=30, read_timeout=120, retry_total=2,
        ) as project:
            if args.preflight:
                credential.get_token("https://ai.azure.com/.default")
                models = {d.name for d in project.deployments.list()}
                if not {config["deployment"], config["optimizationDeployment"]} <= models:
                    raise RuntimeError("Baseline/evaluation or supported optimization model deployment is missing.")
                print("PASS preflight: native SDK and two deployed Entra-accessible models.")
                return 0
            dataset = report.get("generated_dataset") if args.stage != "all" else None
            features = ("dataset", "evaluator", "optimizer") if args.stage == "all" else (args.stage,)
            report["status"] = "BLOCKED"
            for feature in features:
                effective_feature = "optimizer" if feature == "holdout" else feature
                report[effective_feature + "_status"] = "RUNNING"
                save(report)
                try:
                    if feature == "dataset":
                        dataset = generate_dataset(project, config, report)
                    elif feature == "evaluator":
                        cloud_evaluation(project, config, report)
                    elif feature == "holdout":
                        candidates = report["optimizer_candidates"]
                        summary = report["optimization_job"]["result"]["candidate_summary"]
                        validation = native_heldout_evaluation(project, config, report, candidates, summary)
                        report["heldout_gate"] = heldout_gate(candidates, summary, validation)
                        report["optimizer_status"] = "PASS"
                    else:
                        native_optimizer(project, config, dataset, report)
                    effective_feature = "optimizer" if feature == "holdout" else feature
                    report["blockers"] = [b for b in report["blockers"] if b.get("feature") != effective_feature]
                except Exception as error:
                    feature = "optimizer" if feature == "holdout" else feature
                    report[feature + "_status"] = "BLOCKED"
                    report["blockers"].append({
                        "feature": feature, "type": type(error).__name__, "message": str(error),
                        "cause": str(error.__cause__) if error.__cause__ else None,
                    })
                    save(report)
        report["status"] = "PASS" if all(
            report.get(f + "_status") == "PASS" for f in ("dataset", "evaluator", "optimizer")
        ) and not report["blockers"] else "BLOCKED"
    except Exception as error:
        report["blockers"].append({"feature": "preflight", "type": type(error).__name__, "message": str(error)})
    save(report)
    print(json.dumps({"status": report["status"], "dataset": report["dataset_status"],
                      "evaluators": report["evaluator_status"], "optimizer": report["optimizer_status"],
                      "gate": report.get("heldout_gate"), "blockers": report["blockers"]}, default=str))
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
