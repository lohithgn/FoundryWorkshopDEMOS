#:property TargetFramework=net10.0
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const int WaitSeconds = 600;
const string Policy = "Synthetic workshop catalog: Atlas costs USD 25. Boreal costs USD 40. Prices include materials. Refunds are allowed within 7 days.";
const string Baseline = "For every catalog question, reply only 'Please consult the catalog.' Available catalog facts: " + Policy;
var training = new JsonArray(
    new JsonObject { ["query"] = "What is the Atlas workshop price?", ["ground_truth"] = "Atlas costs USD 25." },
    new JsonObject { ["query"] = "What is the Boreal workshop price?", ["ground_truth"] = "Boreal costs USD 40." });
var heldout = new JsonArray(new JsonObject { ["query"] = "What is the refund deadline?", ["ground_truth"] = "Refunds are allowed within 7 days." });
var lab = Environment.CurrentDirectory;
var state = Path.Combine(lab, ".azure");
if (!File.Exists(Path.Combine(lab, "infra", "main.bicep")))
    throw new DirectoryNotFoundException("Run this file from 44-evaluation-optimizer.");
if (ValidateArguments(args, ["--self-test", "--help", "-h", "--preflight"], ["--stage"]) is { } argumentError)
{
    Console.Error.WriteLine($"ERROR: {argumentError}");
    Environment.ExitCode = 2;
    return;
}
if (args.Contains("--self-test")) { OfflineTests(); return; }
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Generate versioned training data, evaluate actual tool traces, and gate a native optimizer.\nUsage: dotnet run --file demo.cs -- [--preflight] [--stage all|dataset|evaluator|optimizer|holdout]");
    return;
}
var preflight = args.Contains("--preflight");
var stage = Option(args, "--stage") ?? "all";
if (stage is not ("all" or "dataset" or "evaluator" or "optimizer" or "holdout"))
{
    Console.Error.WriteLine("ERROR: --stage must be all, dataset, evaluator, optimizer, or holdout.");
    Environment.ExitCode = 2;
    return;
}
Directory.CreateDirectory(state);
var reportPath = Path.Combine(state, "live-report.json");
var report = new JsonObject { ["status"] = "BLOCKED", ["dataset_status"] = "NOT_RUN",
    ["evaluator_status"] = "NOT_RUN", ["optimizer_status"] = "NOT_RUN", ["blockers"] = new JsonArray() };
if (stage != "all" && File.Exists(reportPath))
    report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))!.AsObject();
try
{
    var config = Settings(state);
    var token = await AzCli.Token("https://ai.azure.com/.default");
    var project = new NativeProject(config["projectEndpoint"]!.ToString(), token);
    if (preflight)
    {
        var deployments = await project.Get("deployments");
        var names = (deployments["data"]?.AsArray() ?? deployments["value"]?.AsArray() ?? new JsonArray())
            .Select(item => item?["name"]?.ToString()).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(config["deployment"]!.ToString()) || !names.Contains(config["optimizationDeployment"]!.ToString()))
            throw new InvalidOperationException("Baseline/evaluation or supported optimization model deployment is missing.");
        Console.WriteLine("PASS preflight: native project access and both model deployments.");
        return;
    }
    var stages = stage == "all" ? new[] { "dataset", "evaluator", "optimizer" } : new[] { stage };
    foreach (var current in stages)
    {
        var reportKey = current == "holdout" ? "optimizer" : current;
        report[reportKey + "_status"] = "RUNNING";
        await Save(report);
        try
        {
            switch (current)
            {
                case "dataset":
                    report["generated_dataset"] = await GenerateDataset(project, config, report);
                    report["dataset_status"] = "PASS";
                    break;
                case "evaluator":
                    await EvaluateTraces(config, report);
                    report["evaluator_status"] = "PASS";
                    break;
                case "optimizer":
                    await StartOptimizer(project, config, report);
                    break;
                case "holdout":
                    var candidates = report["optimizer_candidates"]?.AsArray().Select(n => n!.AsObject()).ToList()
                        ?? throw new InvalidOperationException("No native optimizer candidates are saved for held-out validation.");
                    var summary = report["optimization_job"]?["result"]?["candidate_summary"]?.AsObject()
                        ?? throw new InvalidOperationException("Native optimizer candidate summary is missing.");
                    var validation = await NativeHeldoutEvaluation(config, report, candidates, summary);
                    report["heldout_gate"] = HeldoutGate(candidates, summary, validation);
                    report["optimizer_status"] = "PASS";
                    break;
            }
            report["blockers"] = new JsonArray((report["blockers"]?.AsArray() ?? new JsonArray())
                .Where(b => b?["feature"]?.ToString() != reportKey).Select(b => b?.DeepClone()).ToArray());
        }
        catch (Exception error)
        {
            report[reportKey + "_status"] = "BLOCKED";
            report["blockers"]!.AsArray().Add(new JsonObject { ["feature"] = reportKey,
                ["type"] = error.GetType().Name, ["message"] = error.Message });
        }
        await Save(report);
    }
    report["status"] = new[] { "dataset", "evaluator", "optimizer" }
        .All(key => report[key + "_status"]?.ToString() == "PASS") && report["blockers"]!.AsArray().Count == 0 ? "PASS" : "BLOCKED";
}
catch (Exception error)
{
    report["blockers"]!.AsArray().Add(new JsonObject { ["feature"] = "preflight",
        ["type"] = error.GetType().Name, ["message"] = error.Message });
}
await Save(report);
Console.WriteLine(new JsonObject { ["status"] = report["status"]!.DeepClone(),
    ["dataset"] = report["dataset_status"]!.DeepClone(), ["evaluators"] = report["evaluator_status"]!.DeepClone(),
    ["optimizer"] = report["optimizer_status"]!.DeepClone(), ["gate"] = report["heldout_gate"]?.DeepClone(),
    ["blockers"] = report["blockers"]!.DeepClone() }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
if (report["status"]?.ToString() != "PASS") Environment.ExitCode = 1;

static string? Option(string[] values, string option)
{
    var index = Array.IndexOf(values, option);
    if (index < 0) return null;
    if (index + 1 >= values.Length) throw new ArgumentException($"{option} requires a value.");
    return values[index + 1];
}

static string? ValidateArguments(string[] values, string[] switches, string[] options)
{
    for (var index = 0; index < values.Length; index++)
    {
        if (switches.Contains(values[index], StringComparer.Ordinal)) continue;
        if (!options.Contains(values[index], StringComparer.Ordinal)) return $"Unknown option: {values[index]}";
        if (index + 1 >= values.Length || values[index + 1].StartsWith("--", StringComparison.Ordinal))
            return $"{values[index]} requires a value.";
        index++;
    }
    return null;
}

static JsonObject Settings(string state)
{
    var outputs = File.Exists(Path.Combine(state, "deployment.json"))
        ? JsonNode.Parse(File.ReadAllText(Path.Combine(state, "deployment.json")))?["outputs"]?.AsObject() ?? new JsonObject()
        : new JsonObject();
    var mapping = new Dictionary<string, string> {
        ["endpoint"] = "AZURE_OPENAI_ENDPOINT", ["projectEndpoint"] = "FOUNDRY_PROJECT_ENDPOINT",
        ["deployment"] = "AZURE_OPENAI_DEPLOYMENT", ["optimizationDeployment"] = "FOUNDRY_OPTIMIZATION_DEPLOYMENT"
    };
    var config = new JsonObject();
    foreach (var (key, env) in mapping)
        config[key] = Environment.GetEnvironmentVariable(env) is { Length: > 0 } value ? value : outputs[key]?["value"]?.ToString();
    foreach (var key in mapping.Keys)
        if (string.IsNullOrWhiteSpace(config[key]?.ToString()))
            throw new InvalidOperationException($"BLOCKED: missing {key}; deploy infra/main.bicep and save its outputs in .azure/deployment.json or set the documented environment variables.");
    return config;
}

static JsonObject LookupPrice(JsonObject arguments)
{
    var workshop = arguments["workshop"]?.ToString();
    var price = workshop switch { "Atlas" => 25, "Boreal" => 40, _ => throw new ArgumentException("Unknown synthetic workshop.") };
    return new JsonObject { ["workshop"] = workshop, ["currency"] = "USD", ["price"] = price, ["context"] = Policy };
}

static void VerifyEvaluatorResults(JsonArray items, int expectedRows)
{
    if (items.Count != expectedRows) throw new InvalidDataException("Cloud evaluation output count differs from the actual trace dataset.");
    var required = new[] { "groundedness", "relevance", "tool_call_accuracy" };
    foreach (var item in items)
    {
        var results = item?["results"]?.AsArray() ?? new JsonArray();
        var valid = results.Where(result => result?["error"] is null && FiniteJsonNumber(result?["score"]))
            .Select(result => result?["name"]?.ToString()).ToHashSet(StringComparer.Ordinal);
        if (!required.All(valid.Contains))
            throw new InvalidDataException("Cloud evaluators unavailable or missing scores: expected finite numeric scores.");
    }
}

static JsonObject HeldoutGate(List<JsonObject> candidates, JsonObject summary, JsonObject validation)
{
    var baselineId = summary["baseline_id"]?.ToString();
    var baseline = candidates.FirstOrDefault(c => c["candidate_id"]?.ToString() == baselineId);
    var alternatives = candidates.Where(c => c["candidate_id"]?.ToString() != baselineId && c["status"]?.ToString() == "completed").ToList();
    if (baseline?["status"]?.ToString() != "completed" || alternatives.Count == 0)
        throw new InvalidOperationException("Native optimizer did not actually compare a baseline and generated candidate.");
    foreach (var candidate in new[] { baseline }.Concat(alternatives))
    {
        if (!FiniteBetween(candidate["evaluation"]?["score"], 0, 1))
            throw new InvalidOperationException("Native held-out evaluation lacks a bounded numeric score.");
        if (string.IsNullOrEmpty(candidate["evaluation"]?["eval_id"]?.ToString())
            || string.IsNullOrEmpty(candidate["evaluation"]?["eval_run_id"]?.ToString()))
            throw new InvalidOperationException("Native candidate lacks linked cloud evaluation evidence.");
    }
    var best = alternatives.OrderByDescending(c => Number(c["evaluation"]?["score"])).First();
    if (!(best["output"]?["mutations"]?.AsArray() ?? new JsonArray()).Any(m =>
        m?["type"]?.ToString() == "instructions" && !string.IsNullOrWhiteSpace(m?["value"]?.ToString())))
        throw new InvalidOperationException("Optimizer candidate has no native generated instruction mutation.");
    if (string.IsNullOrEmpty(validation["eval_id"]?.ToString()) || string.IsNullOrEmpty(validation["eval_run_id"]?.ToString())
        || validation["query"]?.ToString() != "What is the refund deadline?")
        throw new InvalidOperationException("Gate requires a separate actual native held-out evaluation, not training-screen scores.");
    if (validation["baseline"]?["candidate_id"]?.ToString() != baselineId
        || validation["candidate"]?["candidate_id"]?.ToString() != best["candidate_id"]?.ToString())
        throw new InvalidOperationException("Held-out evaluation targets do not match the actual native baseline/candidate.");
    var baselineScore = Number(validation["baseline"]?["score"]);
    var candidateScore = Number(validation["candidate"]?["score"]);
    if (!FiniteJsonNumber(validation["baseline"]?["score"]) || !FiniteJsonNumber(validation["candidate"]?["score"])
        || baselineScore is < 0 or > 1 || candidateScore is < 0 or > 1
        || string.IsNullOrEmpty(validation["baseline"]?["response_id"]?.ToString())
        || string.IsNullOrEmpty(validation["candidate"]?["response_id"]?.ToString()))
        throw new InvalidOperationException("Native held-out score or response link is missing.");
    return new JsonObject { ["decision"] = candidateScore > baselineScore && candidateScore >= .8 ? "APPROVE" : "REJECT",
        ["baseline_score"] = baselineScore, ["candidate_score"] = candidateScore,
        ["candidate_id"] = best["candidate_id"]?.DeepClone(),
        ["heldout_source"] = "Explicit native cloud evaluation of both returned configurations on the same disjoint refund query.",
        ["native_validation_eval_id"] = validation["eval_id"]?.DeepClone(),
        ["native_validation_run_id"] = validation["eval_run_id"]?.DeepClone(), ["promoted"] = false };
}

static double Number(JsonNode? value) => double.TryParse(value?.ToString(), out var score) ? score : double.NaN;
static (DateTimeOffset PollingDeadline, DateTimeOffset StageDeadline) StageDeadlines(DateTimeOffset started, int timeout)
{
    var deadline = started.AddSeconds(timeout);
    var cancellationReserve = TimeSpan.FromSeconds(Math.Min(15, Math.Max(1, timeout / 40.0)));
    return (deadline - cancellationReserve, deadline);
}

static bool ApplyOptimizerCleanupOutcome(JsonObject report, List<string> errors)
{
    if (errors.Count == 0) return true;
    report["optimizer_status"] = "BLOCKED";
    report["optimizer_cleanup_errors"] = new JsonArray(errors.Select(error => (JsonNode?)JsonValue.Create(error)).ToArray());
    return false;
}

static bool FiniteJsonNumber(JsonNode? value)
{
    if (value is not JsonValue json || value.GetValueKind() != System.Text.Json.JsonValueKind.Number) return false;
    if (json.TryGetValue<double>(out var number)) return double.IsFinite(number);
    if (json.TryGetValue<decimal>(out var decimalNumber)) return true;
    if (json.TryGetValue<long>(out var longNumber)) return true;
    if (json.TryGetValue<int>(out var intNumber)) return true;
    return false;
}
static bool FiniteBetween(JsonNode? value, double min, double max)
    => FiniteJsonNumber(value) && Number(value) >= min && Number(value) <= max;

async Task<JsonObject> GenerateDataset(NativeProject project, JsonObject config, JsonObject report)
{
    var name = "catalog-qna-" + Guid.NewGuid().ToString("N")[..10];
    var body = DatasetRequest(name, config["deployment"]!.ToString());
    var job = await project.Send(HttpMethod.Post, "evaluation_data_generation_jobs", body);
    report["generation_job_id"] = job["id"]?.DeepClone();
    await Save(report);
    var completed = await Poll(project, job, "evaluation_data_generation_jobs", WaitSeconds, report, "generation_job");
    var outputs = completed["result"]?["outputs"]?.AsArray() ?? new JsonArray();
    var datasets = outputs.Where(o => !string.IsNullOrEmpty(o?["name"]?.ToString()) && !string.IsNullOrEmpty(o?["version"]?.ToString())).ToArray();
    if (datasets.Length != 1) throw new InvalidDataException($"Native generation did not produce one immutable dataset version: {outputs}");
    var datasetName = datasets[0]!["name"]!.ToString();
    var version = datasets[0]!["version"]!.ToString();
    var saved = await project.Get($"datasets/{Uri.EscapeDataString(datasetName)}/versions/{Uri.EscapeDataString(version)}");
    report["generated_dataset_before_tag_update"] = saved.DeepClone();
    if (string.IsNullOrEmpty(saved["id"]?.ToString()) || string.IsNullOrEmpty(saved["version"]?.ToString()))
        throw new InvalidDataException("Generated cloud dataset lacks a service id or immutable version.");
    if (!HasRequiredTags(saved))
    {
        await project.Send(HttpMethod.Put, $"datasets/{Uri.EscapeDataString(datasetName)}/versions/{Uri.EscapeDataString(version)}",
            DatasetWithRequiredTags(saved));
        report["dataset_tag_update_submitted"] = true;
    }
    var readback = await project.Get($"datasets/{Uri.EscapeDataString(datasetName)}/versions/{Uri.EscapeDataString(version)}");
    VerifyDatasetReadback(saved, readback);
    return readback;
}

static JsonObject DatasetRequest(string name, string model) => new() {
        ["name"] = name,
        ["sources"] = new JsonArray(new JsonObject { ["type"] = "prompt", ["description"] = "Synthetic pricing-only training source",
            ["prompt"] = "Synthetic workshop catalog: Atlas costs USD 25. Boreal costs USD 40." }),
        ["generation_configuration"] = new JsonObject { ["type"] = "simple_qna", ["max_samples"] = 2,
            ["model_options"] = new JsonObject { ["model"] = model } },
        ["output_configuration"] = new JsonObject { ["name"] = name,
            ["description"] = "Exactly two synthetic pricing training samples; refund holdout excluded.",
            ["tags"] = new JsonObject { ["lab"] = "44", ["split"] = "training",
                ["SecurityControl"] = "Ignore", ["SecurityExemption"] = "StorageAccountKeyAccess" } }
    };
static bool HasRequiredTags(JsonObject dataset)
    => dataset["tags"]?["SecurityControl"]?.ToString() == "Ignore"
       && dataset["tags"]?["SecurityExemption"]?.ToString() == "StorageAccountKeyAccess";

static JsonObject DatasetWithRequiredTags(JsonObject dataset)
{
    var updated = dataset.DeepClone().AsObject();
    var tags = updated["tags"]?.AsObject() ?? new JsonObject();
    tags["SecurityControl"] = "Ignore";
    tags["SecurityExemption"] = "StorageAccountKeyAccess";
    updated["tags"] = tags;
    return updated;
}

static void VerifyDatasetReadback(JsonObject original, JsonObject readback)
{
    if (!HasRequiredTags(readback))
        throw new InvalidDataException("Generated cloud dataset did not retain mandatory security tags after native metadata update.");
    foreach (var key in new[] { "id", "name", "version", "type", "data_uri", "dataUri" })
        if (original[key]?.ToString() != readback[key]?.ToString())
            throw new InvalidDataException($"Dataset metadata update unexpectedly changed immutable {key}.");
}

async Task EvaluateTraces(JsonObject config, JsonObject report)
{
    var rows = new JsonArray();
    foreach (var sample in training)
    {
        var call = await OpenAI(config["endpoint"]!.ToString(), "responses", new JsonObject {
            ["model"] = config["deployment"]!.DeepClone(), ["input"] = sample!["query"]!.DeepClone(),
            ["instructions"] = "Always use lookup_price exactly once to answer pricing questions. Answer only from its result.",
            ["tools"] = ToolDefinition(), ["max_output_tokens"] = 256 });
        var calls = (call["output"]?.AsArray() ?? new JsonArray()).Where(item => item?["type"]?.ToString() == "function_call").ToArray();
        if (calls.Length != 1 || calls[0]?["name"]?.ToString() != "lookup_price")
            throw new InvalidDataException("Real agent did not issue exactly one read-only catalog tool call.");
        var arguments = JsonNode.Parse(calls[0]!["arguments"]!.ToString())!.AsObject();
        var output = LookupPrice(arguments);
        var completed = await OpenAI(config["endpoint"]!.ToString(), "responses", new JsonObject {
            ["previous_response_id"] = call["id"]!.DeepClone(),
            ["input"] = new JsonArray(new JsonObject { ["type"] = "function_call_output",
                ["call_id"] = calls[0]!["call_id"]!.DeepClone(), ["output"] = output.ToJsonString() }),
            ["model"] = config["deployment"]!.DeepClone(), ["tools"] = ToolDefinition(), ["max_output_tokens"] = 256 });
        if (completed["status"]?.ToString() != "completed" || string.IsNullOrEmpty(ResponseText(completed)))
            throw new InvalidDataException("Real tool-assisted response did not complete.");
        rows.Add(new JsonObject { ["query"] = sample!["query"]!.DeepClone(), ["ground_truth"] = sample["ground_truth"]!.DeepClone(),
            ["context"] = Policy, ["response"] = ResponseText(completed), ["tool_definitions"] = ToolDefinition(),
            ["tool_calls"] = new JsonArray(new JsonObject { ["type"] = "tool_call",
                ["tool_call_id"] = calls[0]!["call_id"]!.DeepClone(), ["name"] = "lookup_price", ["arguments"] = arguments.DeepClone() }),
            ["response_id"] = completed["id"]?.DeepClone(), ["tool_response_id"] = call["id"]?.DeepClone() });
    }
    report["actual_agent_traces"] = rows.DeepClone();
    report["tool_trace_source"] = "Actual deployed-model Responses API tool calls, not an agent-reference endpoint.";
    await Save(report);
    var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
        ["query"] = new JsonObject { ["type"] = "string" }, ["response"] = new JsonObject { ["type"] = "string" },
        ["context"] = new JsonObject { ["type"] = "string" }, ["ground_truth"] = new JsonObject { ["type"] = "string" },
        ["tool_definitions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
        ["tool_calls"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } }
    }, ["required"] = new JsonArray("query", "response", "context", "tool_definitions", "tool_calls") };
    var evaluation = await OpenAI(config["endpoint"]!.ToString(), "evals", new JsonObject {
        ["name"] = "44 native evaluator breadth", ["data_source_config"] = new JsonObject {
            ["type"] = "custom", ["item_schema"] = schema, ["include_sample_schema"] = true },
        ["testing_criteria"] = new JsonArray(
            Criterion("groundedness", config["deployment"]!.ToString(), new JsonObject { ["query"] = "{{item.query}}",
                ["response"] = "{{item.response}}", ["context"] = "{{item.context}}", ["tool_definitions"] = "{{item.tool_definitions}}" }),
            Criterion("relevance", config["deployment"]!.ToString(), new JsonObject { ["query"] = "{{item.query}}", ["response"] = "{{item.response}}" }),
            Criterion("tool_call_accuracy", config["deployment"]!.ToString(), new JsonObject { ["query"] = "{{item.query}}",
                ["response"] = "{{item.response}}", ["tool_definitions"] = "{{item.tool_definitions}}", ["tool_calls"] = "{{item.tool_calls}}" })
        ) });
    var evalId = evaluation["id"]?.ToString() ?? throw new InvalidDataException("Native evaluator ID missing.");
    var run = await OpenAI(config["endpoint"]!.ToString(), $"evals/{Uri.EscapeDataString(evalId)}/runs", new JsonObject {
        ["name"] = "two-real-tool-traces", ["data_source"] = new JsonObject { ["type"] = "jsonl",
            ["source"] = new JsonObject { ["type"] = "file_content",
                ["content"] = new JsonArray(rows.Select(row => new JsonObject { ["item"] = row!.DeepClone() }).ToArray()) } } });
    var completedRun = await PollOpenAI(config["endpoint"]!.ToString(), $"evals/{evalId}/runs", run, WaitSeconds);
    var items = await OpenAI(config["endpoint"]!.ToString(), $"evals/{evalId}/runs/{completedRun["id"]}/output_items", null, HttpMethod.Get);
    report["evaluation"] = evaluation;
    report["evaluation_run"] = completedRun;
    report["evaluation_output_items"] = items["data"]?.DeepClone() ?? items["value"]?.DeepClone();
    VerifyEvaluatorResults((items["data"]?.AsArray() ?? items["value"]?.AsArray() ?? new JsonArray()), rows.Count);
}

JsonObject OptimizationRequest(string agentName, JsonNode? agentVersion, JsonObject config, JsonObject? dataset)
{
    var trainingSet = new JsonObject { ["type"] = "target_completion", ["source"] = new JsonObject { ["type"] = "inline",
        ["test_cases"] = training.DeepClone() } };
    if (dataset is not null)
        trainingSet["source"] = new JsonObject { ["type"] = "dataset_reference",
            ["name"] = dataset["name"]?.DeepClone(), ["version"] = dataset["version"]?.ToString() };
    return new JsonObject {
        ["display_name"] = "44 one-candidate bounded native optimization",
        ["target"] = new JsonObject { ["type"] = "foundry_agent", ["name"] = agentName, ["version"] = agentVersion?.DeepClone() },
        ["optimization_model"] = new JsonObject { ["model"] = config["optimizationDeployment"]!.DeepClone() },
        ["optimization_configuration"] = new JsonObject { ["type"] = "agent_optimization", ["goal"] = "improve_quality",
            ["evaluation_configuration"] = new JsonObject { ["training_set"] = trainingSet,
                ["validation_set"] = new JsonObject { ["type"] = "target_completion", ["source"] = new JsonObject {
                    ["type"] = "inline", ["test_cases"] = heldout.DeepClone() } },
                ["evaluators"] = new JsonArray(new JsonObject { ["name"] = "builtin.relevance" }),
                ["evaluation_model"] = new JsonObject { ["model"] = config["deployment"]!.DeepClone() },
                ["max_concurrent_agent_runs"] = 1 },
            ["candidate_search_configuration"] = new JsonObject { ["max_candidates"] = 1 },
            ["agent_optimization_space"] = new JsonObject { ["target_attributes"] = new JsonArray("instructions") } }
    };
}

async Task StartOptimizer(NativeProject project, JsonObject config, JsonObject report)
{
    var agentName = "optimizer-agent-" + Guid.NewGuid().ToString("N")[..10];
    var agent = await project.Send(HttpMethod.Post, $"agents/{Uri.EscapeDataString(agentName)}/versions",
        new JsonObject { ["definition"] = new JsonObject { ["kind"] = "prompt", ["model"] = config["deployment"]!.DeepClone(), ["instructions"] = Baseline } });
    report["optimizer_baseline_agent"] = agent.DeepClone();
    string? jobId = null;
    Exception? operationError = null;
    try
    {
    var body = OptimizationRequest(agentName, agent["version"], config, report["generated_dataset"]?.AsObject());
    report["optimizer_request"] = body.DeepClone();
    await Save(report);
    var response = await project.Send(HttpMethod.Post, "agent_optimization_jobs", body);
    jobId = response["id"]?.ToString() ?? throw new InvalidDataException("Native optimizer response has no job id.");
    report["optimizer_job_id"] = jobId;
    await Save(report);
    var job = await Poll(project, response, "agent_optimization_jobs", WaitSeconds, report, "optimization_job");
    var candidates = await project.Get($"agent_optimization_jobs/{Uri.EscapeDataString(jobId)}/candidates?limit=5");
    report["optimizer_candidates"] = candidates["data"]?.DeepClone() ?? candidates["value"]?.DeepClone() ?? new JsonArray();
    report["optimization_job"] = job.DeepClone();
    var candidateList = (report["optimizer_candidates"]?.AsArray() ?? new JsonArray())
        .Where(item => item is JsonObject).Select(item => item!.AsObject()).ToList();
    var summary = job["result"]?["candidate_summary"]?.AsObject()
        ?? throw new InvalidOperationException("Native optimizer candidate summary is missing.");
    var validation = await NativeHeldoutEvaluation(config, report, candidateList, summary);
    report["heldout_gate"] = HeldoutGate(candidateList, summary, validation);
    report["optimizer_status"] = "PASS";
    }
    catch (Exception error)
    {
        operationError = error;
        throw;
    }
    finally
    {
        var cleanupErrors = new List<string>();
        if (jobId is not null)
        {
            try
            {
                var current = await project.Get($"agent_optimization_jobs/{Uri.EscapeDataString(jobId)}");
                if (current["status"]?.ToString() is "queued" or "in_progress")
                {
                    await project.Send(HttpMethod.Post, $"agent_optimization_jobs/{Uri.EscapeDataString(jobId)}/cancel", new JsonObject());
                    report["optimizer_cancel_requested"] = true;
                }
            }
            catch (Exception error) { cleanupErrors.Add($"job cancellation check: {error.Message}"); }
        }
        try
        {
            await project.Send(HttpMethod.Delete, $"agents/{Uri.EscapeDataString(agentName)}");
            report["optimizer_agent_deleted"] = true;
        }
        catch (Exception error) { cleanupErrors.Add($"temporary agent deletion: {error.Message}"); }
        var cleanupSucceeded = ApplyOptimizerCleanupOutcome(report, cleanupErrors);
        await Save(report);
        if (!cleanupSucceeded && operationError is null)
            throw new InvalidOperationException($"Optimizer cleanup failed: {string.Join("; ", cleanupErrors)}");
    }
}

async Task<JsonObject> NativeHeldoutEvaluation(JsonObject config, JsonObject report, List<JsonObject> candidates, JsonObject summary)
{
    var baselineId = summary["baseline_id"]?.ToString();
    var baseline = candidates.FirstOrDefault(candidate => candidate["candidate_id"]?.ToString() == baselineId);
    var alternatives = candidates.Where(candidate => candidate["candidate_id"]?.ToString() != baselineId
        && candidate["status"]?.ToString() == "completed").ToList();
    if (alternatives.Count == 0)
        throw new InvalidOperationException($"Native optimizer produced no completed candidate: {summary}; candidates={new JsonArray(candidates.Select(candidate => (JsonNode?)candidate.DeepClone()).ToArray())}");
    var candidate = alternatives.OrderByDescending(item => Number(item["evaluation"]?["score"])).First();
    var mutation = (candidate["output"]?["mutations"]?.AsArray() ?? new JsonArray())
        .FirstOrDefault(item => item?["type"]?.ToString() == "instructions" && !string.IsNullOrWhiteSpace(item?["value"]?.ToString()));
    if (mutation is null) throw new InvalidOperationException("Native candidate has no generated instruction mutation to evaluate.");
    var baselineInstructions = report["optimizer_baseline_agent"]?["definition"]?["instructions"]?.ToString()
        ?? report["optimizer_baseline_agent"]?["instructions"]?.ToString()
        ?? throw new InvalidDataException("Native baseline agent instructions are missing.");
    var rows = new JsonArray();
    foreach (var (target, instructions) in new[] {
        (baseline!, baselineInstructions), (candidate, mutation["value"]!.ToString()) })
    {
        var response = await OpenAI(config["endpoint"]!.ToString(), "responses", new JsonObject {
            ["model"] = config["deployment"]!.DeepClone(), ["instructions"] = instructions,
            ["input"] = heldout[0]!["query"]!.DeepClone(), ["max_output_tokens"] = 256 });
        if (response["status"]?.ToString() != "completed" || string.IsNullOrWhiteSpace(ResponseText(response)))
            throw new InvalidOperationException("Actual native baseline/candidate held-out response did not complete.");
        rows.Add(new JsonObject { ["query"] = heldout[0]!["query"]!.DeepClone(),
            ["ground_truth"] = heldout[0]!["ground_truth"]!.DeepClone(),
            ["candidate_id"] = target["candidate_id"]!.DeepClone(), ["response"] = ResponseText(response),
            ["response_id"] = response["id"]!.DeepClone() });
    }
    report["explicit_holdout_responses"] = rows.DeepClone();
    await Save(report);
    var evaluation = await OpenAI(config["endpoint"]!.ToString(), "evals", new JsonObject {
        ["name"] = "44 explicit native baseline/candidate held-out gate",
        ["data_source_config"] = new JsonObject { ["type"] = "custom", ["include_sample_schema"] = true,
            ["item_schema"] = new JsonObject { ["type"] = "object",
                ["properties"] = new JsonObject {
                    ["query"] = new JsonObject { ["type"] = "string" },
                    ["ground_truth"] = new JsonObject { ["type"] = "string" },
                    ["response"] = new JsonObject { ["type"] = "string" },
                    ["response_id"] = new JsonObject { ["type"] = "string" },
                    ["candidate_id"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("query", "response", "response_id", "candidate_id") } },
        ["testing_criteria"] = new JsonArray(Criterion("relevance", config["deployment"]!.ToString(),
            new JsonObject { ["query"] = "{{item.query}}", ["response"] = "{{item.response}}" })) });
    report["explicit_holdout_eval"] = evaluation.DeepClone();
    var items = new JsonArray();
    var evalId = evaluation["id"]!.ToString();
    var payload = new JsonObject { ["name"] = "same-disjoint-refund-question-both-configurations",
        ["data_source"] = new JsonObject { ["type"] = "jsonl", ["source"] = new JsonObject {
            ["type"] = "file_content", ["content"] = new JsonArray(rows.Select(row => new JsonObject { ["item"] = row!.DeepClone() }).ToArray()) } } };
    var run = await OpenAI(config["endpoint"]!.ToString(), $"evals/{Uri.EscapeDataString(evalId)}/runs", payload);
    report["explicit_holdout_run"] = run.DeepClone();
    await Save(report);
    run = await PollOpenAI(config["endpoint"]!.ToString(), $"evals/{Uri.EscapeDataString(evalId)}/runs", run, WaitSeconds);
    report["explicit_holdout_run"] = run.DeepClone();
    var evaluationId = evalId;
    var runId = run["id"]!.ToString();
    string? after = null;
    do
    {
        var route = $"evals/{Uri.EscapeDataString(evaluationId)}/runs/{Uri.EscapeDataString(runId)}/output_items?limit=100";
        if (after is not null) route += "&after=" + Uri.EscapeDataString(after);
        var page = await OpenAI(config["endpoint"]!.ToString(), route, null, HttpMethod.Get);
        foreach (var item in page["data"]?.AsArray() ?? new JsonArray()) items.Add(item?.DeepClone());
        after = page["has_more"]?.GetValue<bool>() == true ? page["last_id"]?.ToString() : null;
    } while (after is not null);
    report["explicit_holdout_output_items"] = items.DeepClone();
    var validation = new JsonObject { ["eval_id"] = evaluationId, ["eval_run_id"] = runId,
        ["query"] = heldout[0]!["query"]!.DeepClone() };
    if (items.Count != 2) throw new InvalidDataException("Native held-out evaluation must score exactly one baseline and one candidate response.");
    foreach (var item in items.Select(node => node!.AsObject()))
    {
        var source = item["datasource_item"]?.AsObject() ?? throw new InvalidDataException("Native held-out output lacks its source item.");
        if (source["query"]?.ToString() != heldout[0]!["query"]!.ToString())
            throw new InvalidDataException("Native gate scored training data instead of the actual refund holdout.");
        var key = source["candidate_id"]?.ToString() == baselineId ? "baseline" : "candidate";
        var expected = key == "baseline" ? baseline! : candidate;
        var expectedRow = rows.Select(node => node!.AsObject()).Single(row => row["candidate_id"]?.ToString() == expected["candidate_id"]?.ToString());
        if (source["candidate_id"]?.ToString() != expected["candidate_id"]?.ToString()
            || source["response_id"]?.ToString() != expectedRow["response_id"]?.ToString())
            throw new InvalidDataException("Native held-out output is not linked to the correct returned configuration's response.");
        var result = (item["results"]?.AsArray() ?? new JsonArray())
            .FirstOrDefault(node => node?["name"]?.ToString() == "relevance");
        var score = Number(result?["score"]);
        if (result?["error"] is not null         || !FiniteJsonNumber(result?["score"]) || !double.IsFinite(score) || score is < 1 or > 5)
            throw new InvalidDataException("Native held-out relevance evaluator did not return a valid 1–5 score.");
        validation[key] = new JsonObject { ["candidate_id"] = expected["candidate_id"]!.DeepClone(),
            ["response_id"] = source["response_id"]!.DeepClone(), ["raw_score"] = score, ["score"] = score / 5 };
    }
    if (validation["baseline"] is null || validation["candidate"] is null)
        throw new InvalidDataException("Native held-out output lacks a distinct baseline and candidate.");
    report["native_holdout_validation"] = validation.DeepClone();
    await Save(report);
    return validation;
}

async Task<JsonObject> Poll(NativeProject project, JsonObject initial, string collection, int timeout, JsonObject report, string key)
{
    var jobId = initial["id"]?.ToString() ?? throw new InvalidDataException($"Native {key} response has no job id.");
    var (pollingDeadline, deadline) = StageDeadlines(DateTimeOffset.UtcNow, timeout);
    var current = initial;
    while (DateTimeOffset.UtcNow < pollingDeadline)
    {
        report[key] = current.DeepClone();
        await Save(report);
        if (current["status"]?.ToString() == "succeeded") return current;
        if (current["status"]?.ToString() is "failed" or "cancelled")
            throw new InvalidOperationException($"Native {key} {current["status"]}: {current["error"]}");
        var delay = pollingDeadline - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds)));
        var remaining = pollingDeadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) break;
        using var requestDeadline = new CancellationTokenSource(remaining);
        try { current = await project.Get($"{collection}/{Uri.EscapeDataString(jobId)}", requestDeadline.Token); }
        catch (OperationCanceledException) when (DateTimeOffset.UtcNow >= pollingDeadline) { break; }
    }
    var cancelRemaining = deadline - DateTimeOffset.UtcNow;
    if (cancelRemaining > TimeSpan.Zero)
    {
        using var cancellationDeadline = new CancellationTokenSource(cancelRemaining);
        await project.Send(HttpMethod.Post, $"{collection}/{Uri.EscapeDataString(jobId)}/cancel",
            new JsonObject(), cancellationDeadline.Token);
        report[key + "_cancel_requested"] = true;
        await Save(report);
    }
    throw new TimeoutException($"Native {key} exceeded its {timeout}s deadline; cancellation was {(report[key + "_cancel_requested"]?.GetValue<bool>() == true ? "requested" : "not confirmed")}, never resubmitted.");
}

async Task<JsonObject> PollOpenAI(string endpoint, string route, JsonObject initial, int timeout)
{
    var id = initial["id"]?.ToString() ?? throw new InvalidDataException("Native evaluator run has no ID.");
    var (pollingDeadline, deadline) = StageDeadlines(DateTimeOffset.UtcNow, timeout);
    while (DateTimeOffset.UtcNow < pollingDeadline)
    {
        if (initial["status"]?.ToString() == "completed") return initial;
        if (initial["status"]?.ToString() is "failed" or "canceled")
            throw new InvalidOperationException($"Native evaluator run failed: {initial["error"]}");
        var delay = pollingDeadline - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds)));
        var remaining = pollingDeadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) break;
        using var requestDeadline = new CancellationTokenSource(remaining);
        try { initial = await OpenAI(endpoint, $"{route}/{id}", null, HttpMethod.Get, requestDeadline.Token); }
        catch (OperationCanceledException) when (DateTimeOffset.UtcNow >= pollingDeadline) { break; }
    }
    var cancelRemaining = deadline - DateTimeOffset.UtcNow;
    var cancellationConfirmed = false;
    if (cancelRemaining > TimeSpan.Zero)
    {
        using var cancellationDeadline = new CancellationTokenSource(cancelRemaining);
        await OpenAI(endpoint, $"{route}/{id}/cancel", new JsonObject(), HttpMethod.Post, cancellationDeadline.Token);
        cancellationConfirmed = true;
    }
    throw new TimeoutException($"Cloud evaluator run exceeded its {timeout}s deadline; cancellation was {(cancellationConfirmed ? "requested" : "not confirmed")}.");
}

static JsonObject ToolDefinition() => new() { ["type"] = "function", ["name"] = "lookup_price",
    ["description"] = "Read the synthetic catalog price for one workshop. Has no side effects.",
    ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
        ["workshop"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Atlas", "Boreal") } },
        ["required"] = new JsonArray("workshop"), ["additionalProperties"] = false }, ["strict"] = true };

static JsonObject Criterion(string name, string model, JsonObject mapping) => new() {
    ["type"] = "azure_ai_evaluator", ["name"] = name, ["evaluator_name"] = "builtin." + name,
    ["initialization_parameters"] = name == "tool_call_accuracy"
        ? new JsonObject { ["deployment_name"] = model }
        : new JsonObject { ["model"] = model },
    ["data_mapping"] = mapping };

static string ResponseText(JsonObject response) => string.Concat((response["output"]?.AsArray() ?? new JsonArray())
    .SelectMany(item => item?["content"]?.AsArray() ?? new JsonArray())
    .Where(item => item?["type"]?.ToString() == "output_text").Select(item => item?["text"]?.ToString() ?? ""));

async Task<JsonObject> OpenAI(string endpoint, string route, JsonObject? payload, HttpMethod? method = null,
    CancellationToken cancellationToken = default)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    using var request = new HttpRequestMessage(method ?? HttpMethod.Post, endpoint.TrimEnd('/') + "/openai/v1/" + route);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
        await AzCli.Token("https://ai.azure.com/.default", cancellationToken));
    if (payload is not null) request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    using var response = await http.SendAsync(request, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Native OpenAI API {method ?? HttpMethod.Post} {route}: HTTP {(int)response.StatusCode}, {ErrorCode(body)}");
    return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
}

static string ErrorCode(string body)
{
    try { return JsonNode.Parse(body)?["error"]?["code"]?.ToString() ?? "service error"; }
    catch { return "service error"; }
}

static async Task Save(JsonObject report)
{
    var state = Path.Combine(Environment.CurrentDirectory, ".azure");
    Directory.CreateDirectory(state);
    var path = Path.Combine(state, "live-report.json");
    var temp = Path.Combine(state, "live-report.pending");
    await File.WriteAllTextAsync(temp, report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    File.Move(temp, path, true);
}

void OfflineTests()
{
    if (training.Select(r => r!["query"]!.ToString()).Intersect(heldout.Select(r => r!["query"]!.ToString())).Any())
        throw new InvalidDataException("Refund holdout leaked into training.");
    var generated = DatasetRequest("catalog-qna-test", "baseline-model");
    if (generated["generation_configuration"]?["max_samples"]?.GetValue<int>() != 2
        || generated["sources"]?.AsArray().Count != 1
        || generated["sources"]?[0]?["type"]?.ToString() != "prompt"
        || generated["sources"]?[0]?["prompt"]?.ToString().Contains("refund", StringComparison.OrdinalIgnoreCase) == true
        || generated["output_configuration"]?["tags"]?["SecurityControl"]?.ToString() != "Ignore"
        || generated["output_configuration"]?["tags"]?["SecurityExemption"]?.ToString() != "StorageAccountKeyAccess")
        throw new InvalidDataException("Generated training dataset must be exactly two pricing-only samples with required tags.");
    var optimizerConfig = new JsonObject { ["deployment"] = "baseline-model", ["optimizationDeployment"] = "optimizer-model" };
    var optimizerRequest = OptimizationRequest("optimizer-agent", JsonValue.Create("3"), optimizerConfig,
        new JsonObject { ["name"] = "generated", ["version"] = "7" });
    var optimization = optimizerRequest["optimization_configuration"]!;
    if (optimizerRequest["target"]?["version"]?.ToString() != "3"
        || optimization["evaluation_configuration"]?["training_set"]?["source"]?["type"]?.ToString() != "dataset_reference"
        || optimization["evaluation_configuration"]?["training_set"]?["source"]?["version"]?.ToString() != "7"
        || optimization["evaluation_configuration"]?["validation_set"]?["source"]?["test_cases"]?.AsArray().Count != 1
        || optimization["evaluation_configuration"]?["max_concurrent_agent_runs"]?.GetValue<int>() != 1
        || optimization["candidate_search_configuration"]?["max_candidates"]?.GetValue<int>() != 1
        || optimization["agent_optimization_space"]?["target_attributes"]?.AsArray().Single()?.ToString() != "instructions")
        throw new InvalidDataException("Native optimizer must pin versions, disjoint holdout, supported model and bounded instruction search.");
    var toolCallCriterion = Criterion("tool_call_accuracy", "judge-model", new JsonObject());
    if (toolCallCriterion["initialization_parameters"]?["deployment_name"]?.ToString() != "judge-model"
        || toolCallCriterion["initialization_parameters"]?["model"] is not null)
        throw new InvalidDataException("Native tool-call accuracy requires its deployment_name parameter.");
    var cleanupOutcome = new JsonObject { ["optimizer_status"] = "PASS" };
    if (ApplyOptimizerCleanupOutcome(cleanupOutcome, ["simulated temporary agent deletion failure"])
        || cleanupOutcome["optimizer_status"]?.ToString() != "BLOCKED"
        || cleanupOutcome["optimizer_cleanup_errors"]?.AsArray().Count != 1)
        throw new InvalidDataException("Optimizer cleanup failures must block the stage and remain in evidence.");
    var started = DateTimeOffset.UtcNow;
    var (pollingDeadline, stageDeadline) = StageDeadlines(started, WaitSeconds);
    if (pollingDeadline >= stageDeadline || stageDeadline - pollingDeadline > TimeSpan.FromSeconds(15)
        || pollingDeadline - started < TimeSpan.FromSeconds(585))
        throw new InvalidDataException("Long-running evaluation deadlines must reserve bounded cancellation time.");
    var originalDataset = JsonNode.Parse("""{"id":"native-id","name":"generated","version":"1","type":"uri_file","data_uri":"azureai://source","tags":{"lab":"44","securityControl":"Ignore","custom":"kept"}}""")!.AsObject();
    var taggedDataset = DatasetWithRequiredTags(originalDataset);
    if (taggedDataset["tags"]?["lab"]?.ToString() != "44" || taggedDataset["tags"]?["custom"]?.ToString() != "kept"
        || !HasRequiredTags(taggedDataset) || HasRequiredTags(originalDataset))
        throw new InvalidDataException("Metadata tag repair must preserve existing tags and exact required tag casing.");
    VerifyDatasetReadback(originalDataset, taggedDataset);
    ExpectFailure(() => VerifyDatasetReadback(originalDataset,
        new JsonObject { ["id"] = "native-id", ["name"] = "generated", ["version"] = "1", ["type"] = "uri_file",
            ["data_uri"] = "azureai://different", ["tags"] = DatasetWithRequiredTags(originalDataset)["tags"]!.DeepClone() }),
        "immutable data_uri");
    ExpectFailure(() => VerifyDatasetReadback(originalDataset, originalDataset), "mandatory security tags");
    if (!Baseline.Contains("reply only 'Please consult the catalog.'", StringComparison.Ordinal)
        || !Baseline.Contains(Policy, StringComparison.Ordinal)) throw new InvalidDataException("Baseline must preserve real catalog facts and instruction headroom.");
    if (LookupPrice(new JsonObject { ["workshop"] = "Atlas" })["price"]?.ToString() != "25")
        throw new InvalidDataException("Read-only synthetic price tool contract failed.");
    ExpectFailure(() => LookupPrice(new JsonObject { ["workshop"] = "unknown" }), "Unknown synthetic workshop");
    var candidates = new List<JsonObject> {
        new() { ["candidate_id"] = "baseline", ["status"] = "completed",
            ["evaluation"] = new JsonObject { ["score"] = .5, ["eval_id"] = "eval", ["eval_run_id"] = "base-run" } },
        new() { ["candidate_id"] = "new", ["status"] = "completed",
            ["evaluation"] = new JsonObject { ["score"] = .9, ["eval_id"] = "eval", ["eval_run_id"] = "new-run" },
            ["output"] = new JsonObject { ["mutations"] = new JsonArray(new JsonObject { ["type"] = "instructions", ["value"] = "Service-generated prompt." }) } }
    };
    var summary = new JsonObject { ["baseline_id"] = "baseline" };
    var validation = new JsonObject { ["eval_id"] = "heldout", ["eval_run_id"] = "heldout-run",
        ["query"] = "What is the refund deadline?", ["baseline"] = new JsonObject { ["candidate_id"] = "baseline",
            ["response_id"] = "base-response", ["score"] = .5 },
        ["candidate"] = new JsonObject { ["candidate_id"] = "new", ["response_id"] = "new-response", ["score"] = .9 } };
    if (HeldoutGate(candidates, summary, validation)["decision"]?.ToString() != "APPROVE")
        throw new InvalidDataException("Candidate improvement gate failed.");
    validation["candidate"]!["score"] = .5;
    if (HeldoutGate(candidates, summary, validation)["decision"]?.ToString() != "REJECT")
        throw new InvalidDataException("Tie must be rejected.");
    ExpectFailure(() => HeldoutGate(candidates, summary, new JsonObject()), "separate actual native held-out");
    var rejected = validation.DeepClone().AsObject();
    rejected["candidate"]!["score"] = .7;
    if (HeldoutGate(candidates, summary, rejected)["decision"]?.ToString() != "REJECT")
        throw new InvalidDataException("Held-out candidate below the required threshold must be rejected.");
    var invalidCandidate = candidates.Select(item => item.DeepClone().AsObject()).ToList();
    invalidCandidate[1]["evaluation"]!["score"] = double.NaN;
    ExpectFailure(() => HeldoutGate(invalidCandidate, summary, validation), "bounded numeric score");
    invalidCandidate = candidates.Select(item => item.DeepClone().AsObject()).ToList();
    invalidCandidate[1]["output"]!["mutations"] = new JsonArray();
    ExpectFailure(() => HeldoutGate(invalidCandidate, summary, validation), "no native generated instruction mutation");
    invalidCandidate = candidates.Select(item => item.DeepClone().AsObject()).ToList();
    invalidCandidate[1]["evaluation"]!["eval_run_id"] = null;
    ExpectFailure(() => HeldoutGate(invalidCandidate, summary, validation), "linked cloud evaluation evidence");
    var poor = new JsonArray(new JsonObject { ["results"] = new JsonArray(new JsonObject { ["name"] = "groundedness", ["score"] = 4 }) });
    ExpectFailure(() => VerifyEvaluatorResults(poor, 1), "missing scores");
    var stringScore = new JsonArray(new JsonObject { ["results"] = new JsonArray(
        new JsonObject { ["name"] = "groundedness", ["score"] = "5" },
        new JsonObject { ["name"] = "relevance", ["score"] = 4 },
        new JsonObject { ["name"] = "tool_call_accuracy", ["score"] = 4 }) });
    ExpectFailure(() => VerifyEvaluatorResults(stringScore, 1), "missing scores");
    var invalidNumber = new JsonArray(new JsonObject { ["results"] = new JsonArray(
        new JsonObject { ["name"] = "groundedness", ["score"] = double.NaN },
        new JsonObject { ["name"] = "relevance", ["score"] = 4 },
        new JsonObject { ["name"] = "tool_call_accuracy", ["score"] = 4 }) });
    ExpectFailure(() => VerifyEvaluatorResults(invalidNumber, 1), "missing scores");
    VerifyEvaluatorResults(new JsonArray(new JsonObject { ["results"] = new JsonArray(
        new JsonObject { ["name"] = "groundedness", ["score"] = 4 },
        new JsonObject { ["name"] = "relevance", ["score"] = 4 },
        new JsonObject { ["name"] = "tool_call_accuracy", ["score"] = 4 }) }), 1);
    Console.WriteLine("PASS: disjoint dataset, grounded read-only tool, three native evaluator scores and separate holdout gate.");
}

static void ExpectFailure(Action action, string message)
{
    try { action(); }
    catch (Exception error) when (error.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new InvalidDataException($"Expected failure containing '{message}'.");
}

sealed class NativeProject(string endpoint, string token)
{
    private readonly Uri _endpoint = new(endpoint.TrimEnd('/') + "/");
    public Task<JsonObject> Get(string path, CancellationToken cancellationToken = default)
        => Send(HttpMethod.Get, path, cancellationToken: cancellationToken);
    public async Task<JsonObject> Send(HttpMethod method, string path, JsonObject? payload = null,
        CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var builder = new UriBuilder(new Uri(_endpoint, path.TrimStart('/')));
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        query["api-version"] = "v1";
        builder.Query = query.ToString();
        using var request = new HttpRequestMessage(method, builder.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload is not null) request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Native project API {method} {path}: HTTP {(int)response.StatusCode}, {ErrorCode(body)}");
        return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
    }
    private static string ErrorCode(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["code"]?.ToString() ?? "service error"; }
        catch { return "service error"; }
    }
}

static class AzCli
{
    public static async Task<string> Token(string scope, CancellationToken cancellationToken = default)
        => (await Run(cancellationToken, "account", "get-access-token", "--scope", scope, "--query", "accessToken", "-o", "tsv")).Trim();
    public static async Task<string> Run(CancellationToken cancellationToken, params string[] args)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Azure CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            throw;
        }
        var output = await stdout;
        var errorOutput = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"Azure CLI failed ({process.ExitCode}): {errorOutput.Trim()}");
        return output;
    }
}
