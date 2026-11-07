# Lab catalog

The repository contains **47 standalone labs (numbered 0-46)**. Each description states what
the implementation actually demonstrates, including where orchestration runs
and what it does not prove. Use the [repository guide](./README.md) to choose a
learning path and the [dated verification report](./tests.md) to distinguish
implementation, offline tests, Azure deployment and native live verification.
A description is not a claim that every preview or tenant-dependent gate passed.

0. Microsoft Foundry - Model basics: temperature, top_p, chat, batch and reasoning

   **Verification scope:** Offline validation only; Azure deployment and native inference/batch completion are not yet live-verified.

   **What the lab does:** Starts with explicitly synthetic next-token probabilities to show temperature scaling and cumulative-probability nucleus sampling. Repeats the same writing prompt while changing one sampling parameter at a time, then compares GPT-4.1-mini and o4-mini on the same scheduling problem with actual answers, latency, token usage and reference-answer agreement. Uses a separate GlobalBatch deployment of the same chat model to upload three independent JSONL requests, persist/resume the native job and validate downloaded results by unique `custom_id`. Pending or errored jobs fail explicitly rather than passing on submission. Batch is a processing mode, not a third model family; one small comparison does not establish model superiority or cost savings. [Lab instructions](./0-model-basics/README.md) | [Implementation](./0-model-basics/demo.py)

1. Microsoft Foundry - Model Router

   **What the lab does:** Sends a factual question, a writing task, and a reasoning problem to the same Model Router deployment using Microsoft Entra authentication. For each request, it displays the answer, reported serving model, client-observed latency, token usage, and finish reason. Participants can also supply a custom prompt. The exercise illustrates routing through one deployment; different prompts may select the same model, and this small demonstration is not a cost-savings benchmark. [Lab instructions](./1-model-router/README.md) | [Implementation](./1-model-router/demo.py)

2. Microsoft Foundry - Workflow Agent

   **What the lab does:** Builds a two-stage Microsoft Agent Framework workflow: a writer drafts a beginner workshop invitation, then an editor shortens it while preserving the supplied facts. Both the draft and final invitation are printed so participants can follow the handoff. Foundry provides model inference, but the workflow runs in the local Python process; it is not a hosted or portal-authored workflow and does not send invitations. [Lab instructions](./2-workflow-agent/README.md) | [Implementation](./2-workflow-agent/demo.py)

3. Microsoft Foundry - Public-web grounding with the built-in web-search tool

   **Verification scope:** The historical with-tool path passed; the current with/without-tool comparison has not had a separate live smoke test.

   **What the lab does:** Compares a temporary Foundry prompt agent with and without the web-search tool, using the same model, instructions, and Microsoft Learn question about Model Router in independent requests. The with-tool run requires completed search calls and structured URL citations and prints the answer and sources. The no-tool run displays its answer with zero search calls/citations, showing that instructions alone do not enable browsing; plain-text URLs are not verified retrieval evidence. Missing required evidence or incomplete responses fail visibly, and each temporary version is deleted afterward. The historical "WebIQ" folder name refers to this public-web grounding exercise; it does not call the separate Microsoft Web IQ APIs or demonstrate private enterprise search. [Lab instructions](./3-webiq-agent/README.md) | [Implementation](./3-webiq-agent/demo.py)

4. Microsoft Foundry - Azure Function pricing through Foundry Toolbox

   **What the lab does:** Publishes an OpenAPI definition for a deployed Azure Function into a temporary Foundry Toolbox, then connects a local Agent Framework assistant to its MCP endpoint. The assistant must call the read-only pricing function, which validates the attendee count and calculates a USD 25-per-person total. The script checks the returned tool result and its source marker rather than accepting an invented price, then deletes the toolbox version. [Lab instructions](./4-agent-tools/README.md) | [Implementation](./4-agent-tools/demo.py)

5. Microsoft Foundry - Typed ticket routing with Microsoft Decision Model

   **What the lab does:** Uses Microsoft's typed Decision API rather than a chat-completion prompt to route synthetic support tickets to billing, technical, or account teams. Each request defines an explicit choice question and the criteria for each team; the response must contain a valid typed choice. The demo prints predicted and expected teams, latency, and toy-example accuracy, and handles throttling with a bounded retry policy. A custom ticket can be supplied for comparison. [Lab instructions](./5-decision-model/README.md) | [Implementation](./5-decision-model/demo.py)

6. Microsoft Foundry - Content Safety classification and application policy

   **What the lab does:** Submits text to Azure AI Content Safety and displays severity scores for Hate, SelfHarm, Sexual, and Violence. Application code applies a configurable threshold of 2, 4, or 6 and prints an explicit ALLOW or BLOCK decision. Participants can change the text and threshold to separate service classification from application policy. This is text analysis, not a generative agent or a guarantee that allowed content is suitable for every audience. [Lab instructions](./6-content-safety/README.md) | [Implementation](./6-content-safety/demo.py)

7. Microsoft Foundry - Semantic caching through the Azure API Management AI gateway

   **Verification scope:** The replacement gateway/Redis implementation is validated offline; its live cache-hit verification is pending. The older application-cache pass does not apply.

   **What the lab does:** Sends ordinary chat requests through Azure API Management Consumption, the lowest-cost gateway tier for this workshop. Gateway policies use Foundry embeddings to look up and store semantically similar responses in Azure Managed Redis (Balanced B0 without high availability, with RediSearch). No caching or embedding logic lives in the application. The demo prints response IDs, observed response reuse, and latency; APIM tracing confirms cache hits and avoided generation calls. Embedding lookups and Redis still incur costs. [Lab instructions](./7-semantic-cache/README.md) | [Implementation](./7-semantic-cache/demo.py)

8. Microsoft Foundry - Cloud coherence evaluation of agent responses

   **What the lab does:** Creates a temporary workshop tutor agent, collects its actual answers to two questions, and submits those query/response pairs and response IDs to Foundry's cloud `builtin.coherence` evaluator. It polls the evaluation, prints aggregate and per-item results, and verifies that every result matches the submitted response. Incomplete or errored evaluations fail explicitly. Coherence measures how well an answer reads, not whether its claims are factually correct or grounded. [Lab instructions](./8-agent-evaluation/README.md) | [Implementation](./8-agent-evaluation/demo.py)

9. Microsoft Foundry - A small managed red-team scan

   **What the lab does:** Wraps a Foundry-backed workshop tutor as the target of a small managed red-team scan. The scanner uses one baseline objective in the HateUnfairness risk category, exercises the tutor's safety instructions, and saves a JSON report and SDK logs in a new output directory. The script checks scan completion and result counts before displaying the scorecard. This is a narrowly scoped safety smoke test, not comprehensive assurance; generated safety-test reports require careful handling. [Lab instructions](./9-agent-red-teaming/README.md) | [Implementation](./9-agent-red-teaming/demo.py)

10. Supervised fine-tuning - managed classification and local Phi knowledge adaptation

   **What the lab does:** Keeps the managed support-ticket example: upload labeled JSONL, submit a one-epoch Global Standard training job, and retain/resume job monitoring without another paid submission. A separate local Apple-silicon example fine-tunes 4-bit Phi-3.5-mini on Polish regional dishes and traditions using QLoRA, then compares actual base/adapted answers on frozen confirmation questions. Verified whole-answer audit improved from 1/16 to 11/16; the narrower concept rubric improved from 2/16 to 15/16, with six controls maintained. This tests unseen wording of taught facts, not broad knowledge or Foundry-hosted Phi training. [Lab instructions](./10-fine-tuning/README.md) | [Managed example](./10-fine-tuning/demo.py) | [Local Phi example](./10-fine-tuning/polish_demo.py)

11. Microsoft Foundry - Minimal Foundry IQ knowledge base

   **Verification scope:** Historical managed retrieval passed before the Foundry project was added; the current project-enabled template has not been live-verified.

   **What the lab does:** Uploads three synthetic workshop fact sheets into a semantic Azure AI Search index, registers that index as a knowledge source, and creates a Foundry IQ knowledge base in answer-synthesis mode. A question combines schedule and equipment facts; the script prints the grounded answer, retrieval activity, and source references. Unlike lab 27, documents are uploaded directly into Search: there is no Blob-backed managed ingestion pipeline or persistent Foundry agent. [Lab instructions](./11-foundry-iq/README.md) | [Implementation](./11-foundry-iq/demo.py)

12. Microsoft Agent Framework - Inference with Microsoft Entra Agent ID

   **What the lab does:** The setup script creates an Entra agent identity blueprint, its principal, a child agent identity, and a short-lived blueprint credential. The Python demo performs the blueprint-to-agent token exchange, obtains a model-access token for the child identity, and runs a one-question Agent Framework tutor. Participants see which identity makes the inference call: the agent, not the presenter's Azure CLI user. Tenant permissions, agent RBAC, and identity-object cleanup are essential prerequisites. [Lab instructions](./12-entra-agent-id/README.md) | [Implementation](./12-entra-agent-id/demo.py)

13. Microsoft Agent Framework - Sequential, concurrent, group-chat and Magentic orchestration

   **What the lab does:** Runs four separate local Agent Framework workflows backed by Foundry inference. Sequential passes an invitation from writer to editor; concurrent independently generates a workshop title and snack suggestion; group chat alternates an agenda planner and reviewer for four assistant turns; Magentic lets a manager coordinate title and checklist specialists into a workshop card. The runner displays participant updates and final outputs, with bounded rounds and a per-workflow timeout. Each pattern can be run separately. [Lab instructions](./13-agent-orchestrations/README.md) | [Implementation](./13-agent-orchestrations/demo.py)

14. Microsoft Foundry - First responses, streaming, endpoints and authorization errors

   **What the lab does:** Calls a workshop tutor once normally and once as a stream, showing the serving model and usage returned by the service. It then probes a deliberately missing deployment for HTTP 404 and a separate denied endpoint for an explicit inference RBAC denial, printing the real errors and suggested fixes. Authentication uses an Entra credential chain or an explicitly configured certificate service principal. Unexpected probe success is reported as a failed demonstration, not fabricated permission-denial evidence. [Lab instructions](./14-foundry-fundamentals/README.md) | [Implementation](./14-foundry-fundamentals/demo.py)

15. Microsoft Foundry - Repeatable model and router benchmarking

   **What the lab does:** Repeats six labeled ticket-classification cases across mini, full, and router deployments, rotating deployment order between repeats. Cases include resolved incidents, missing impact information, and embedded misleading instructions. A JSON report records exact category/priority accuracy, schema compliance, serving models, p50/p95 latency, usage, failures, and results by difficulty. Cost estimates use explicitly illustrative rates; router cost remains unknown unless rates are supplied. Invalid, refused, incomplete, and failed requests count as incorrect. [Lab instructions](./15-model-benchmark/README.md) | [Implementation](./15-model-benchmark/demo.py)

16. Microsoft Foundry - Prompt design and schema-constrained outputs

   **What the lab does:** Runs four labeled tickets through instruction-only, few-shot, and strict JSON Schema modes, then independently checks schema validity and exact field values, including explicit nulls for unknown information. A tiny output-token budget probes incomplete responses, while a separate request attempts to elicit a refusal. A refusal is not guaranteed: the script distinguishes observed refusal, content filtering, and valid output. Clearly labeled offline fixtures demonstrate error handling without presenting fixtures as live Azure results. [Lab instructions](./16-structured-outputs/README.md) | [Implementation](./16-structured-outputs/demo.py)

17. Microsoft Foundry - Application RAG, chunking and retrieval comparisons

   **What the lab does:** Application code splits three workshop documents into 12-word and 48-word chunks, generates embeddings, and creates a temporary Search index for each setting. The same four questions run through keyword, vector, and hybrid retrieval, displaying passages before generation. Answers must cite retrieved chunk IDs and include matching verbatim quotes; the unanswerable question must produce "Insufficient evidence." The summary compares document-level recall@3 and reciprocal rank, and temporary indexes are deleted afterward. [Lab instructions](./17-rag-fundamentals/README.md) | [Implementation](./17-rag-fundamentals/demo.py)

18. Microsoft Foundry - Correlated agent, model and tool tracing

   **What the lab does:** Explicitly instruments a small pricing orchestration with parent agent, pricing-tool, and model-call OpenTelemetry spans, exported through Azure Monitor. Normal, deliberately slow, and failed-tool scenarios expose timing and error differences; the failed tool is passed to the model as an unavailable-pricing result. Client request IDs and trace IDs support correlation, while prompts, answers, and automatic exception content are excluded from telemetry. A successful exporter flush is not claimed as proof of Application Insights ingestion. [Lab instructions](./18-agent-tracing/README.md) | [Implementation](./18-agent-tracing/demo.py)

19. Microsoft Foundry - Local conversation persistence and context boundaries

   **What the lab does:** Stores complete tutor turns in SQLite under a composite user/conversation key and resumes history through a new store instance. Alice supplies a notebook marker; Bob uses the same conversation ID without receiving Alice's history. A conservative byte-based context bound and output reserve drive oldest-pair truncation until the original marker is evicted. The demonstration checks scoped reset and deletion. These are logical users in a trusted local CLI, not an authentication or production multi-tenant authorization system. [Lab instructions](./19-conversation-state/README.md) | [Implementation](./19-conversation-state/demo.py)

20. Microsoft Foundry - Exact-action approval and idempotent synthetic bookings

   **What the lab does:** Forces a model to propose a synthetic booking, validates its workshop and attendee count, and stores a pending proposal bound to the authenticated Entra actor, exact payload digest, and 15-minute expiry. Separate approve/reject commands control whether a local SQLite booking is created. Transactional checks reject mismatched actions or actors, while replaying an executed proposal returns the existing booking. The model cannot execute bookings itself; no real reservation or payment service is connected. [Lab instructions](./20-human-approval/README.md) | [Implementation](./20-human-approval/demo.py)

21. Microsoft Foundry - Identity-derived application retrieval permissions

   **What the lab does:** Authenticates two real Entra users through separate Azure CLI sessions and Microsoft Graph, resolving their user IDs and transitive group memberships. Trusted application code derives a Search security filter for public and user/group-restricted documents. Both users ask the same questions, including a false administrator claim; generated answers are limited to validated quotes from authorized passages. The runner checks public access, allowed restricted access, and absence of unauthorized passages, answers, or citations, then deletes the temporary index. [Lab instructions](./21-permission-aware-retrieval/README.md) | [Implementation](./21-permission-aware-retrieval/demo.py)

22. Microsoft Foundry - Prompt-injection trust boundaries and Prompt Shields

   **What the lab does:** Tests two synthetic text sources representing a retrieved document and a tool response, each mixing a workshop start time with an unrelated-marker instruction. It reports raw baseline behavior and, by default, separate Prompt Shields classifications. The protected path extracts only the allowed time field, requests schema-constrained output, and requires an exact factual match. No executable tools are exposed. This narrowly defined projection illustrates trust boundaries; it is not a general document sanitizer or an injection-proof guarantee. [Lab instructions](./22-prompt-injection/README.md) | [Implementation](./22-prompt-injection/demo.py)

23. Microsoft Foundry - Evaluation regression gates with GitHub OIDC

   **What the lab does:** Generates real support-agent answers using either a baseline prompt or a friendlier prompt containing deliberately incorrect facts. It combines deterministic exact-answer checks with Foundry cloud coherence evaluation; default release thresholds require both pass rates to reach 100%. GitHub Actions authenticates through Azure federation and uploads the evaluation report even after failure. The report records prompt/dataset hashes, actual deployment metadata, response IDs, evaluator results, and errors. This is a regression gate, not an automatic production deployment pipeline. [Lab instructions](./23-evaluation-cicd/README.md) | [Implementation](./23-evaluation-cicd/demo.py)

24. Microsoft Foundry - Legacy-preview agent application publishing and rollback

   **What the lab does:** Creates an actual Agent Application and managed deployment through the documented legacy preview publishing route. It verifies a version answering "USD 25," switches the same application endpoint to a version answering "USD 40," then restores and retests the original version in a finally block. A JSON report records ARM-read-back configured versions, responses, usage, request IDs, and rollback evidence. Monitoring queries the Azure Activity Log for deployment changes; this lab does not configure Application Insights tracing or publish to Teams. [Lab instructions](./24-agent-lifecycle/README.md) | [Implementation](./24-agent-lifecycle/demo.py)

25. Microsoft Foundry - Application retries, deadlines and request budgets

   **What the lab does:** Runs two model requests with at most two concurrent workers, injecting a local throttle or timeout before real inference. A shared budget bounds attempts and deadlines, and retry logic honors Retry-After or exponential backoff. A synthetic booking loses its first response after committing, then recovers without a duplicate; further exercises show persistent-outage budget exhaustion, non-retried HTTP 403, and expired-deadline stops. Actual successful token usage feeds an illustrative cost estimate. Injected faults do not consume Azure quota. [Lab instructions](./25-resilience-cost/README.md) | [Implementation](./25-resilience-cost/demo.py)

26. Microsoft Foundry - Vision extraction, ground truth and uncertainty

   **What the lab does:** Generates two synthetic receipt PNGs and their ground-truth labels: one has a readable USD 12.50 total, while the other omits currency and obscures the amount. An image-capable model extracts merchant, currency, total, and uncertainty labels under a strict schema. Independent scoring compares every field with ground truth and reports schema compliance separately from visual accuracy, alongside usage and illustrative cost. Generate-only mode creates the teaching images without making network calls. [Lab instructions](./26-multimodal/README.md) | [Implementation](./26-multimodal/demo.py)

27. Microsoft Foundry - Managed Blob knowledge sources and a persistent IQ agent

   **What the lab does:** Uploads and reads back eight synthetic case/runbook documents, configures two managed Blob knowledge sources with hourly ingestion, and verifies successful indexing and document coverage. A knowledge base returns extractive evidence, while a persistent Foundry agent uses its MCP retrieval tool through a project-managed-identity connection to answer compound Surface-support questions with source IDs. Ask-only and refresh-only modes support portal demonstrations and content updates. Unlike lab 11, ingestion is service-managed; document ACL enforcement, Purview, and end-user authorization are outside this shared-corpus demonstration. [Lab instructions](./27-FoundryIQwithKB/README.md) | [Implementation](./27-FoundryIQwithKB/demo.py)

28. Microsoft Foundry - Agent using a reusable skill

   **What the lab does:** Packages a workshop-brief `SKILL.md`, uploads it to the preview Foundry Skills API, and downloads the exact immutable version. A small application harness injects the verified downloaded skill into a temporary Foundry prompt agent. Two independent requests use the same model and workshop title, first without the skill and then with it. The script displays both answers and checks the skill-enabled answer against the skill's exact JSON checklist contract, then deletes the temporary agents and skill. This demonstrates centrally stored, version-pinned instructions through direct injection, not automatic skill discovery, Toolbox/MCP loading, or execution of skill scripts. [Lab instructions](./28-agent-skills/README.md) | [Implementation](./28-agent-skills/demo.py)

29. Microsoft Foundry - The agent discovers the right skill through Toolbox

   **What the lab does:** Uploads three skills for workshop briefing, support triage, and incident summary, pins their versions in a temporary Foundry Toolbox, and discovers their metadata through MCP resources. A local Python provider exposes a read-only loading function to a Foundry-backed Responses agent. Three independent requests must each read only the relevant skill and satisfy its exact output contract. The briefing additionally loads its bundled checklist on demand; the other requests must not load that reference. This is progressive disclosure, not eager injection of every skill, a native prompt-agent `skills=` option, or a hosted container. Temporary Toolbox and skills are deleted on success or failure. [Lab instructions](./29-toolbox-skill-discovery/README.md) | [Implementation](./29-toolbox-skill-discovery/demo.py)

30. Microsoft Foundry - A skill-powered data analyst creates verified artifacts

   **What the lab does:** Stores and downloads a version-pinned analysis skill, injects its instructions into a Foundry agent with native Code Interpreter, and uploads a synthetic workshop CSV. The model executes Python in the managed sandbox and produces a machine-readable analysis report and PNG chart. The harness requires real completed execution and generated-file citations, downloads the actual artifacts, and checks the reported totals against fixed ground truth. Skills supply the analysis procedure; Code Interpreter supplies execution. No local computation replaces a failed cloud run. [Lab instructions](./30-skill-data-analyst/README.md) | [Implementation](./30-skill-data-analyst/demo.py)

31. Microsoft Foundry - A tutor remembers across independent conversations

   **What the lab does:** Uses the low-level native Foundry Memory Store API for extraction, search, and forgetting rather than replaying local conversation history. Two real Graph-authenticated users have backend-derived scopes; application code explicitly injects each user's native search results into a fresh Foundry prompt-agent conversation. The lab checks scoped isolation and deletion and requires actual memory-operation evidence rather than a plausible model answer. It does not attach an auto-writing memory tool. Two-user authentication is preflighted before provisioning; service failures are not replaced with in-memory or SQLite substitutes. [Lab instructions](./31-native-agent-memory/README.md) | [Implementation](./31-native-agent-memory/demo.py)

32. Microsoft Foundry - An agent follows a browser runbook with approval

   **What the lab does:** Combines a reusable browser procedure with native Browser Automation through the Playwright Workspaces remote MCP endpoint. A controlled synthetic registration scenario demonstrates finding a workshop and preparing a draft, while submission requires explicit approval for the exact action. The lab checks browser/tool evidence and resulting state rather than trusting model narration. The default path does not automatically approve submission. Managed-browser connection, controlled-site setup, costs, and availability requirements are documented; there is no local browser fallback. [Lab instructions](./32-browser-runbook/README.md) | [Implementation](./32-browser-runbook/demo.py)

33. Microsoft Foundry - Evaluate, promote, pin, and roll back a skill

   **What the lab does:** Creates two immutable versions of a synthetic pricing skill, checks a candidate against two fixed pricing facts before promotion, and changes the skill's centrally managed default. Fresh default-resolving and v1-pinned consumers show which version they actually download and use. The default is restored in a finally block and both consumers are tested again. An optional incorrect-price candidate must fail the gate without promotion. This is application-managed snapshot injection and deterministic release checking, not hot reload, agent application deployment, or cloud evaluation. [Lab instructions](./33-skill-release-governance/README.md) | [Implementation](./33-skill-release-governance/demo.py)

34. Microsoft Foundry - An agent extracts invoices with Document Intelligence

   **What the lab does:** Deploys a persistent Foundry prompt agent and a key-protected Azure Function OpenAPI tool. The Function uses managed identity to analyze twelve repository-stored synthetic invoice PDFs with Document Intelligence's `prebuilt-invoice` model and returns extracted fields, confidence, page evidence, submitted-file SHA-256 and real service operation IDs. The agent runs its tool in Azure, including from the Foundry playground, without a local function-call runner. Repeated live quality gates compare complete, zero-total, EUR and missing-total examples against fixed ground truth and the exact repository PDF bytes, require completed cloud tool evidence, and reject invented amounts or ungrounded agent output. Missing fields require human review. Resources and the agent remain deployed for presentation; paid page/model usage, fixed Function-plan costs and cleanup are documented. [Lab instructions](./34-document-intelligence/README.md) | [Implementation](./34-document-intelligence/demo.py) | [Sample PDFs](./34-document-intelligence/samples/)

35. Microsoft Foundry - A Work IQ agent uses the calling user's Microsoft 365 context

   **What the lab does:** Saves a persistent Foundry prompt agent with the native Work IQ A2A tool and an OAuth2 identity-passthrough connection. Each caller authorizes their own same-tenant Microsoft 365 account; no shared presenter token or application-only retrieval is used. An identity-only smoke test forces the native tool and compares its answer with an independent Graph-authenticated caller profile that is never supplied to the model. Consent continuation is bound to the caller and agent version, and a two-user comparison checks independent responses from the same agent/connection. Tenant admin consent, Copilot Credits billing, per-user browser authorization, privacy boundaries, costs and cleanup are documented. Live Work IQ validation remains blocked until those mandatory tenant/user authorization steps are completed; deployment or mocked tests alone are not reported as a live pass. [Lab instructions](./35-work-iq/README.md) | [Implementation](./35-work-iq/demo.py)

36. Microsoft Foundry - Your custom code on the hosted agent runtime

   **What the lab does:** Builds a custom Python container in Azure Container Registry and deploys a persistent Foundry hosted agent using the current automatic-provisioning API and Invocations container protocol 2.0.0. GPT-5.4 mini proposes a read-only pricing call; cloud code validates and executes it, then independently verifies the model's structured answer against the actual tool result. A local UI displays real response/tool/trace IDs and hosted session state. Live gates require correct prices, same-session continuity, independent-session state, and ingested Azure Monitor telemetry. The workflow never runs in the UI process; this is not a two-user authorization or load test. [Lab instructions](./36-hosted-agent/README.md) | [Implementation](./36-hosted-agent/demo.py)

37. Microsoft Foundry - Detect and recover a live agent quality regression

   **Live status (2026-10-27):** Infrastructure, agents, UI scenarios and traces verified; native automatic evaluation did not trigger. The complete live gate is blocked, not passed.

   **What the lab does:** Publishes two persistent GPT-5.4 mini agents and native Foundry continuous evaluation rules that sample completed live responses. A simple local operations UI switches the monitored agent from a correct pricing version to an intentionally wrong version while a stable control stays healthy, then restores the good route. Cloud exact-match verdicts must correlate to actual response IDs and demonstrate baseline PASS, isolated regression FAIL, and recovery PASS; missing results remain pending. The live gate also queries actual server-side telemetry for both agents. This is a focused single-project production-monitoring sample, not a complete cross-project Control Plane, and it never substitutes local scoring for native evaluation. [Lab instructions](./37-production-monitoring/README.md) | [Implementation](./37-production-monitoring/demo.py)

38. Microsoft Foundry - Native runtime guardrails

   **Selected review priority:** 1.

   **Live status (2026-11-03):** Passed native prompt-agent user-input intervention and the model baseline; 14 offline tests passed. Tool-call and tool-response controls are not configured.

   **What the lab demonstrates:** Configure and attach native Foundry guardrails, then compare an allowed workshop request with a controlled policy intervention. Inspect actual service evidence and distinguish runtime enforcement from standalone Content Safety classification and application authorization. Supported intervention points, preview restrictions, deployment, costs and cleanup are documented. [Lab instructions](./38-native-guardrails/README.md) | [Implementation](./38-native-guardrails/demo.py)

39. Microsoft Foundry - Enforced AI Gateway token limits

   **Selected review priority:** 3.

   **Live status (2026-11-03):** Passed real gateway 429/403 and independent-project access, including repeat runs and the shared runner; 19 offline tests passed.

   **What the lab demonstrates:** Send real model requests through an Azure API Management AI gateway with small centrally configured token rate limits and quotas. Verify actual rate-limit and quota responses rather than injecting local faults, and distinguish gateway containment from deployment capacity and billing alerts. Use isolated workshop resources and bounded requests. [Lab instructions](./39-gateway-token-limits/README.md) | [Implementation](./39-gateway-token-limits/demo.py)

40. Microsoft Foundry - A managed voice-based prompt agent

   **Selected review priority:** 4.

   **Live status (2026-11-03):** Passed, including the shared deployment runner; 16 offline tests passed.

   **What the lab demonstrates:** Create a native voice-based prompt agent and exercise managed voice orchestration through a real audio session. Verify service audio, transcript and conversation evidence using synthetic workshop input, without presenting a local speech-to-text wrapper as native voice-agent execution. Regional and preview access requirements are explicit. [Lab instructions](./40-voice-agent/README.md) | [Implementation](./40-voice-agent/demo.py)

41. Microsoft Foundry - Schema-based Content Understanding

   **Selected review priority:** 6 (the repeated selection is included once).

   **Live status (2026-11-03):** Passed for synthetic document and audio inputs, including the shared deployment runner; 32 offline tests passed. Audio field confidence is not fabricated when the GA service does not emit it.

   **What the lab demonstrates:** Analyze synthetic content with the actual Content Understanding service and a small schema, compare extracted fields with ground truth, and inspect confidence and source grounding. Explain how this differs from direct image-model inference and Document Intelligence's prebuilt models. Unsupported modalities or missing model configuration fail visibly. [Lab instructions](./41-content-understanding/README.md) | [Implementation](./41-content-understanding/demo.py)

42. Microsoft Foundry - Managed Copilot harness and native Tool Search

   **Selected review priority:** 7.

   **Observed status:** 12 tests, Azure deployment in Sweden Central and shared-runner native live replay passed. The managed Copilot harness and native Toolbox discovery/invocation are separate verified demonstrations.

   **What the lab demonstrates:** Select the managed Copilot harness for a prompt agent and configure native Toolbox Tool Search. Require service evidence of discovering and using relevant capabilities rather than eagerly injecting every skill or exposing a local resource-discovery function. Document the managed runtime, identity, supported tools and preview boundaries. [Lab instructions](./42-managed-harness-tool-search/README.md) | [Implementation](./42-managed-harness-tool-search/demo.py)

43. Microsoft Foundry - Run an agent with a native routine

   **Selected review priority:** 8.

   **Observed status:** 24 tests and Azure deployment in East US 2 passed. An earlier direct native timer run passed, but repeated shared-runner live attempts are currently **BLOCKED** by HTTP 400 agentic-token issuance despite persisted creator dispatch identity. The tenant/service cause is unresolved; temporary assets were cleaned up. Do not present this as currently repeatable live success.

   **What the lab demonstrates:** Create a short native timer routine, inspect its configuration and run history, and verify that the service dispatches the agent with a real response. Demonstrate lifecycle controls and remove temporary triggers after the test. A local scheduler or direct client invocation cannot satisfy the routine live gate. [Lab instructions](./43-agent-routines/README.md) | [Implementation](./43-agent-routines/demo.py)

44. Microsoft Foundry - Evaluation breadth and native agent optimization

   **Selected review priority:** 9.

   **Observed status:** 27 tests, Azure deployment in East US 2 and final shared-runner native live replay passed. Generated-dataset tags were verified through native PUT/GET. The completed optimizer candidate improved independent held-out score from 0.4 to 1.0: APPROVE, without automatic promotion. An earlier tied candidate was correctly rejected.

   **What the lab demonstrates:** Evaluate a synthetic agent with appropriate cloud quality criteria and exercise the native optimizer against a fixed dataset. Compare baseline and service-generated candidate results and retain an independent release check. Missing optimization results or evaluator failures are blockers, not passing scores or locally invented candidates. [Lab instructions](./44-evaluation-optimizer/README.md) | [Implementation](./44-evaluation-optimizer/demo.py)

45. Microsoft Foundry - Permission-native Foundry IQ and Fabric IQ

   **Selected review priority:** 10.

   **Observed status:** 18 tests and Azure deployment passed (Foundry East US 2, Search West US 2). Native source-permission sync, authorized retrieval and omitted-source-identity denial passed. Full live verification is **BLOCKED**: a second real user is unavailable, Fabric/Power BI token issuance is disabled and the subscription has no Fabric capacity. The omission check does not count as two-user verification.

   **What the lab demonstrates:** Separate source-native permission enforcement in Foundry IQ from application-generated Search filters, and connect an agent to an actual Fabric IQ endpoint. Verify authorized evidence and denied access using real identities and approved synthetic data. Fabric licensing, source permissions, second-user setup and connection availability are explicit prerequisites; unavailable tenant integrations remain blocked. [Lab instructions](./45-governed-iq/README.md) | [Implementation](./45-governed-iq/demo.py)

46. Microsoft Foundry - Cross-project fleet governance

   **Selected review priority:** 11.

   **Observed status:** 16 tests, Azure deployment in East US 2 and shared-runner API live gates passed: two projects, three agents including native external registration, correlated telemetry and an isolated Compliant Azure Policy assignment. Portal Control Plane remains **NOT_VERIFIED**; optional Defender/Purview integration is not configured.

   **What the lab demonstrates:** Inventory and inspect agents across multiple projects, correlate actual telemetry and exercise supported governance and external-agent registration surfaces. Distinguish programmatic asset evidence from portal-only Control Plane verification and tenant-dependent Defender/Purview integrations. Do not treat one project's monitoring view as complete fleet governance. [Lab instructions](./46-fleet-governance/README.md) | [Implementation](./46-fleet-governance/demo.py)

The selected additions are review priorities **1, 3, 4, 6, 7, 8, 9, 10 and 11**.
Priorities **2 (private networking), 5 (File Search) and 12 (Teams/Copilot and
general A2A publication)** are not implemented by this addition. See [test
results](./tests.md) for separate offline, deployment and live-feature status.