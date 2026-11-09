"""Evaluate actual agent answers with Foundry's cloud coherence evaluator."""

import argparse
import os
from time import monotonic, sleep
from uuid import uuid4

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import PromptAgentDefinition, TestingCriterionAzureAIEvaluator
from azure.identity import AzureCliCredential


QUESTIONS = [
    "Microsoft Foundry is an Azure platform for building and operating AI applications and agents. "
    "What can developers use it for? Explain in two short sentences.",
    "How is model inference different from supervised model training using labeled data?",
]


def evaluate(client, responses: list[dict[str, str]], deployment: str, timeout: float = 600):
    fields = ("query", "response", "response_id")
    if not responses or any(
        not all(isinstance(item.get(key), str) and item[key].strip() for key in fields)
        for item in responses
    ):
        raise ValueError("Nonempty questions, actual answers, and response IDs are required.")
    expected = {item["response_id"]: {key: item[key] for key in fields} for item in responses}
    if len(expected) != len(responses):
        raise ValueError("Response IDs must be unique.")
    evaluation = client.evals.create(
        name="Workshop agent coherence",
        data_source_config={
            "type": "custom",
            "item_schema": {
                "type": "object",
                "properties": {key: {"type": "string"} for key in fields},
                "required": list(fields),
            },
        },
        testing_criteria=[TestingCriterionAzureAIEvaluator(
            type="azure_ai_evaluator", name="coherence", evaluator_name="builtin.coherence",
            initialization_parameters={"model": deployment},
            data_mapping={"query": "{{item.query}}", "response": "{{item.response}}"},
        )],
    )
    run = client.evals.runs.create(
        eval_id=evaluation.id, name="two-question-smoke-test",
        data_source={
            "type": "jsonl",
            "source": {
                "type": "file_content",
                "content": [{"item": item} for item in expected.values()],
            },
        },
    )
    print(f"Evaluation: {evaluation.id}; run: {run.id}", flush=True)
    deadline = monotonic() + timeout
    while run.status in ("queued", "in_progress"):
        if monotonic() >= deadline:
            raise TimeoutError(f"Evaluation still running: {evaluation.id}/{run.id}")
        sleep(5)
        run = client.evals.runs.retrieve(run.id, eval_id=evaluation.id)
    if run.status != "completed":
        raise RuntimeError(f"Evaluation failed: {run.model_dump_json(indent=2)}")
    counts = run.result_counts
    if counts.total != len(responses) or counts.errored or counts.passed + counts.failed != counts.total:
        raise RuntimeError(f"Incomplete evaluation results: {counts}")
    print(run.model_dump_json(indent=2))
    items = list(client.evals.runs.output_items.list(run.id, eval_id=evaluation.id))
    if len(items) != len(responses) or any(item.status != "completed" for item in items):
        raise RuntimeError("Evaluation returned missing or errored output items.")
    seen = set()
    passed = 0
    for item in items:
        payload = item.model_dump(mode="json")
        source = payload.get("datasource_item", {})
        response_id = source.get("response_id")
        results = payload.get("results", [])
        if (response_id not in expected or response_id in seen or source != expected[response_id]
                or len(results) != 1 or results[0].get("name") != "coherence"
                or results[0].get("status") != "completed"
                or type(results[0].get("passed")) is not bool):
            raise RuntimeError("Missing, mismatched, or errored coherence result.")
        seen.add(response_id)
        passed += results[0]["passed"]
        print(item.model_dump_json(indent=2))
    if passed != counts.passed:
        raise RuntimeError("Per-item coherence results disagree with aggregate counts.")
    return run


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-endpoint", default=os.getenv("FOUNDRY_PROJECT_ENDPOINT"))
    parser.add_argument("--deployment", default=os.getenv("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini"))
    args = parser.parse_args()
    if not args.project_endpoint or not args.project_endpoint.startswith("https://"):
        parser.error("Set FOUNDRY_PROJECT_ENDPOINT to the Bicep projectEndpoint HTTPS URL.")
    if not args.deployment.strip():
        parser.error("Deployment must not be empty.")
    with (
        AzureCliCredential() as credential,
        AIProjectClient(endpoint=args.project_endpoint, credential=credential) as project,
    ):
        agent = project.agents.create_version(
            agent_name=f"eval-demo-{uuid4().hex[:8]}",
            definition=PromptAgentDefinition(
                model=args.deployment, instructions="You are a concise AI workshop tutor.",
            ),
        )
        try:
            with project.get_openai_client(agent_name=agent.name) as agent_client:
                responses = []
                for question in QUESTIONS:
                    response = agent_client.responses.create(input=question, store=True, timeout=120)
                    if response.status != "completed" or not response.output_text.strip():
                        raise RuntimeError(f"Agent response failed: {response.status}; {response.error}")
                    print(f"{question}\n{response.output_text}\nStored response: {response.id}")
                    responses.append({
                        "query": question, "response": response.output_text, "response_id": response.id,
                    })
            with project.get_openai_client() as evaluator_client:
                evaluate(evaluator_client, responses, args.deployment)
        finally:
            project.agents.delete_version(agent_name=agent.name, agent_version=agent.version)
            print(f"Temporary agent version deleted: {agent.name}/{agent.version}")


if __name__ == "__main__":
    main()
