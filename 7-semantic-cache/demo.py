"""Send normal chat requests through the APIM semantic-caching AI gateway."""

import argparse
import os
from time import perf_counter, sleep
from uuid import uuid4

from azure.identity import AzureCliCredential, get_bearer_token_provider
from openai import AzureOpenAI


PROMPTS = [
    "What is 2 + 2? Reply with the integer only.",
    "What is two plus two? Reply with the integer only.",
    "What is 2 + 2? Reply with the integer only.",
]


def answer(client: AzureOpenAI, deployment: str, prompt: str) -> tuple[str, str]:
    response = client.chat.completions.create(
        model=deployment, messages=[{"role": "user", "content": prompt}],
        max_completion_tokens=120, stream=False, timeout=60,
    )
    if not response.choices:
        raise RuntimeError("Model returned no choices.")
    choice = response.choices[0]
    text = choice.message.content
    if choice.finish_reason != "stop" or not text or not text.strip():
        raise RuntimeError("Model did not return a complete, nonempty answer.")
    if not response.id or not response.id.strip():
        raise RuntimeError("Response has no ID; cannot observe gateway response reuse.")
    return text, response.id


def present(client: AzureOpenAI, deployment: str) -> None:
    seen_ids: set[str] = set()
    reused = 0
    for index, prompt in enumerate(PROMPTS):
        if index:
            # Redis storage/index visibility can lag behind the first response.
            sleep(2)
        start = perf_counter()
        text, response_id = answer(client, deployment, prompt)
        if text.strip() != "4":
            raise RuntimeError(f"Unexpected arithmetic answer {text!r}; expected exactly 4.")
        repeated = response_id in seen_ids
        reused += repeated
        seen_ids.add(response_id)
        print(f"\n{'REUSED RESPONSE' if repeated else 'NEW RESPONSE ID'}; "
              f"{perf_counter() - start:.2f}s; id={response_id}\n{prompt}\n{text}")
    print(f"\nChat requests sent to APIM: {len(PROMPTS)}; repeated response IDs: {reused}.")
    print("APIM owns embedding calls, cache lookup, and storage. "
          "Confirm HIT/MISS and backend calls in the APIM portal trace.")
    if not reused:
        raise RuntimeError(
            "No response reuse observed. Inspect APIM tracing, Redis connectivity, "
            "managed-identity RBAC, threshold, and cache storage/index propagation."
        )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--endpoint", default=os.getenv("AZURE_OPENAI_ENDPOINT"),
                        help="APIM endpoint output, including /ai; not the direct Foundry URL.")
    parser.add_argument("--deployment", default=os.getenv("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini"))
    parser.add_argument("--session", default=str(uuid4()),
                        help="Cache partition; reuse this value across processes to show persistence.")
    args = parser.parse_args()
    if not args.endpoint or not args.endpoint.startswith("https://"):
        parser.error("Set AZURE_OPENAI_ENDPOINT to the Bicep APIM endpoint HTTPS URL.")
    if not args.deployment.strip() or not args.session.strip():
        parser.error("Deployment and session must not be empty.")
    print(f"Gateway: {args.endpoint}; cache session: {args.session}")
    with AzureCliCredential() as credential:
        with AzureOpenAI(
            azure_endpoint=args.endpoint.rstrip("/"),
            api_version="2024-10-21",
            azure_ad_token_provider=get_bearer_token_provider(
                credential, "https://cognitiveservices.azure.com/.default",
            ),
            default_headers={"x-workshop-cache-session": args.session},
        ) as client:
            present(client, args.deployment)


if __name__ == "__main__":
    main()
