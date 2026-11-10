using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class AgentEvaluationDemo
{
    public const string Instructions = "You are a concise AI workshop tutor.";
    public static readonly string[] Questions =
    [
        "Microsoft Foundry is an Azure platform for building and operating AI applications and agents. What can developers use it for? Explain in two short sentences.",
        "How is model inference different from supervised model training using labeled data?"
    ];

    public sealed record Options(string ProjectEndpoint, string Deployment);
    public sealed record AgentVersion(string Name, string Version);
    public sealed record ResponseRow(string Query, string Response, string ResponseId);

    public static Options Parse(string[] args, IReadOnlyDictionary<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(item => (string)item.Key, item => item.Value?.ToString());
        var endpoint = environment.GetValueOrDefault("FOUNDRY_PROJECT_ENDPOINT");
        var deployment = environment.GetValueOrDefault("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--help")
                throw new HelpRequestedException();
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}.");
            var value = args[++index];
            switch (option)
            {
                case "--project-endpoint": endpoint = value; break;
                case "--deployment": deployment = value; break;
                default: throw new ArgumentException($"Unknown option: {option}.");
            }
        }
        if (string.IsNullOrWhiteSpace(endpoint) || !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Set FOUNDRY_PROJECT_ENDPOINT to the Bicep projectEndpoint HTTPS URL.");
        if (string.IsNullOrWhiteSpace(deployment))
            throw new ArgumentException("Deployment must not be empty.");
        return new Options(endpoint.TrimEnd('/'), deployment);
    }

    public static void ValidateResponses(IReadOnlyList<ResponseRow> responses)
    {
        if (responses.Count == 0 || responses.Any(row =>
                string.IsNullOrWhiteSpace(row.Query) || string.IsNullOrWhiteSpace(row.Response) ||
                string.IsNullOrWhiteSpace(row.ResponseId)))
            throw new ArgumentException("Nonempty questions, actual answers, and response IDs are required.");
        if (responses.Select(row => row.ResponseId).Distinct(StringComparer.Ordinal).Count() != responses.Count)
            throw new ArgumentException("Response IDs must be unique.");
    }

    public static string BuildEvaluationPayload(string deployment, IReadOnlyList<ResponseRow> responses)
    {
        ValidateResponses(responses);
        const string template = """{"name":"Workshop agent coherence","data_source_config":{"type":"custom","item_schema":{"type":"object","properties":{"query":{"type":"string"},"response":{"type":"string"},"response_id":{"type":"string"}},"required":["query","response","response_id"]}},"testing_criteria":[{"type":"azure_ai_evaluator","name":"coherence","evaluator_name":"builtin.coherence","initialization_parameters":{"model":"__DEPLOYMENT__"},"data_mapping":{"query":"{{item.query}}","response":"{{item.response}}"}}]}""";
        return template.Replace("\"__DEPLOYMENT__\"", Quote(deployment), StringComparison.Ordinal);
    }

    public static string BuildRunPayload(IReadOnlyList<ResponseRow> responses)
    {
        ValidateResponses(responses);
        var rows = string.Join(",", responses.Select(row =>
            $"{{\"item\":{{\"query\":{Quote(row.Query)},\"response\":{Quote(row.Response)},\"response_id\":{Quote(row.ResponseId)}}}}}"));
        return $"{{\"name\":\"two-question-smoke-test\",\"data_source\":{{\"type\":\"jsonl\",\"source\":{{\"type\":\"file_content\",\"content\":[{rows}]}}}}}}";
    }

    private static string Quote(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

    public static async Task<JsonDocument> SendAsync(
        HttpClient http, HttpMethod method, string uri, string token, string? body = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Foundry returned {(int)response.StatusCode}: {content}");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content);
    }

    public static async Task<AgentVersion> CreateAgentAsync(
        HttpClient http, string projectEndpoint, string deployment, string token,
        string? agentName = null, CancellationToken cancellationToken = default)
    {
        agentName ??= $"eval-demo-{Guid.NewGuid():N}"[..17];
        var body = $"{{\"definition\":{{\"kind\":\"prompt\",\"model\":{Quote(deployment)},\"instructions\":{Quote(Instructions)}}}}}";
        using var result = await SendAsync(http, HttpMethod.Post,
            $"{projectEndpoint.TrimEnd('/')}/agents/{Uri.EscapeDataString(agentName)}/versions?api-version=v1",
            token, body, cancellationToken);
        return new AgentVersion(
            result.RootElement.GetProperty("name").GetString() ?? agentName,
            result.RootElement.GetProperty("version").ValueKind == JsonValueKind.String
                ? result.RootElement.GetProperty("version").GetString()!
                : result.RootElement.GetProperty("version").GetRawText());
    }

    public static async Task<ResponseRow> GetAgentAnswerAsync(
        HttpClient http, string projectEndpoint, AgentVersion agent, string question, string token,
        CancellationToken cancellationToken = default)
    {
        var uri = $"{projectEndpoint.TrimEnd('/')}/agents/{Uri.EscapeDataString(agent.Name)}/endpoint/protocols/openai/responses";
        var body = $"{{\"input\":{Quote(question)},\"store\":true}}";
        using var result = await SendAsync(http, HttpMethod.Post, uri, token, body, cancellationToken);
        var root = result.RootElement;
        var status = root.TryGetProperty("status", out var state) ? state.GetString() : null;
        var text = root.TryGetProperty("output_text", out var output) ? output.GetString() : null;
        if (status != "completed" || string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"Agent response failed: {status}; {root.GetRawText()}");
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Agent response has no stored response ID.");
        return new ResponseRow(question, text, id);
    }

    public static async Task<JsonDocument> EvaluateAsync(
        HttpClient http, string projectEndpoint, IReadOnlyList<ResponseRow> responses, string deployment,
        string token, TimeSpan? timeout = null, Func<TimeSpan, Task>? delay = null,
        CancellationToken cancellationToken = default)
    {
        ValidateResponses(responses);
        delay ??= Task.Delay;
        var prefix = projectEndpoint.TrimEnd('/') + "/openai/v1";
        using var evaluation = await SendAsync(http, HttpMethod.Post, $"{prefix}/evals", token,
            BuildEvaluationPayload(deployment, responses), cancellationToken);
        var evaluationId = evaluation.RootElement.GetProperty("id").GetString()!;
        using var createdRun = await SendAsync(http, HttpMethod.Post, $"{prefix}/evals/{Uri.EscapeDataString(evaluationId)}/runs",
            token, BuildRunPayload(responses), cancellationToken);
        var runId = createdRun.RootElement.GetProperty("id").GetString()!;
        Console.WriteLine($"Evaluation: {evaluationId}; run: {runId}");
        var deadline = Stopwatch.StartNew();
        var maxTime = timeout ?? TimeSpan.FromMinutes(10);
        JsonDocument run = createdRun;
        while (IsRunning(run.RootElement))
        {
            if (deadline.Elapsed >= maxTime)
                throw new TimeoutException($"Evaluation still running: {evaluationId}/{runId}");
            await delay(TimeSpan.FromSeconds(5));
            run = await SendAsync(http, HttpMethod.Get, $"{prefix}/evals/{Uri.EscapeDataString(evaluationId)}/runs/{Uri.EscapeDataString(runId)}",
                token, cancellationToken: cancellationToken);
        }
        ValidateRun(run.RootElement, responses);
        Console.WriteLine(run.RootElement.ToString());
        using var itemsDocument = await SendAsync(http, HttpMethod.Get,
            $"{prefix}/evals/{Uri.EscapeDataString(evaluationId)}/runs/{Uri.EscapeDataString(runId)}/output_items",
            token, cancellationToken: cancellationToken);
        ValidateOutputItems(itemsDocument.RootElement, responses,
            GetCount(run.RootElement.GetProperty("result_counts"), "passed"));
        return JsonDocument.Parse(run.RootElement.GetRawText());
    }

    private static bool IsRunning(JsonElement run) =>
        run.TryGetProperty("status", out var status) && status.GetString() is "queued" or "in_progress";

    public static void ValidateRun(JsonElement run, IReadOnlyList<ResponseRow> responses)
    {
        if (!run.TryGetProperty("status", out var status) || status.GetString() != "completed")
            throw new InvalidOperationException($"Evaluation failed: {run.GetRawText()}");
        var counts = run.GetProperty("result_counts");
        var total = GetCount(counts, "total");
        var passed = GetCount(counts, "passed");
        var failed = GetCount(counts, "failed");
        var errored = GetCount(counts, "errored");
        if (total != responses.Count || errored != 0 || passed + failed != total)
            throw new InvalidOperationException($"Incomplete evaluation results: {counts}");
    }

    private static int GetCount(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : 0;

    public static void ValidateOutputItems(JsonElement output, IReadOnlyList<ResponseRow> responses, int expectedPassed)
    {
        var items = output.TryGetProperty("data", out var data) ? data :
            output.TryGetProperty("value", out var value) ? value : default;
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != responses.Count)
            throw new InvalidOperationException("Evaluation returned missing or errored output items.");
        var expected = responses.ToDictionary(row => row.ResponseId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var passed = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("status", out var itemStatus) || itemStatus.GetString() != "completed")
                throw new InvalidOperationException("Evaluation returned missing or errored output items.");
            var source = item.GetProperty("datasource_item");
            var responseId = source.GetProperty("response_id").GetString();
            if (responseId is null || !expected.TryGetValue(responseId, out var row) || !seen.Add(responseId) ||
                source.GetProperty("query").GetString() != row.Query ||
                source.GetProperty("response").GetString() != row.Response)
                throw new InvalidOperationException("Missing, mismatched, or errored coherence result.");
            var results = item.GetProperty("results");
            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != 1)
                throw new InvalidOperationException("Missing, mismatched, or errored coherence result.");
            var result = results[0];
            if (result.GetProperty("name").GetString() != "coherence" ||
                result.GetProperty("status").GetString() != "completed" ||
                !result.TryGetProperty("passed", out var resultPassed) ||
                resultPassed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("Missing, mismatched, or errored coherence result.");
            if (resultPassed.GetBoolean()) passed++;
        }
        if (passed != expectedPassed)
            throw new InvalidOperationException("Per-item coherence results disagree with aggregate counts.");
    }

    public static async Task<int> Main(string[] args)
    {
        AgentVersion? agent = null;
        Options? options = null;
        HttpClient? http = null;
        var exitCode = 0;
        try
        {
            try { options = Parse(args); }
            catch (HelpRequestedException)
            {
                Console.WriteLine("Usage: dotnet run --file demo.cs -- [--project-endpoint HTTPS_URL] [--deployment NAME]");
                return 0;
            }
            var token = await AgentEvaluationAuth.GetTokenAsync("https://ai.azure.com/.default");
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            agent = await CreateAgentAsync(http, options.ProjectEndpoint, options.Deployment, token);
            Console.WriteLine($"Temporary agent: {agent.Name}/{agent.Version}");
            var responses = new List<ResponseRow>();
            foreach (var question in Questions)
            {
                var response = await GetAgentAnswerAsync(http, options.ProjectEndpoint, agent, question, token);
                Console.WriteLine($"{question}\n{response.Response}\nStored response: {response.ResponseId}");
                responses.Add(response);
            }
            var evalToken = await AgentEvaluationAuth.GetTokenAsync("https://ai.azure.com/.default");
            using var run = await EvaluateAsync(http, options.ProjectEndpoint, responses, options.Deployment, evalToken);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}");
            exitCode = 1;
        }
        finally
        {
            if (agent is not null && http is not null)
            {
                try
                {
                    var token = await AgentEvaluationAuth.GetTokenAsync("https://ai.azure.com/.default");
                    using var deleted = await SendAsync(http, HttpMethod.Delete,
                        $"{options!.ProjectEndpoint}/agents/{Uri.EscapeDataString(agent.Name)}/versions/{Uri.EscapeDataString(agent.Version)}?api-version=v1",
                        token);
                    Console.WriteLine($"Temporary agent version deleted: {agent.Name}/{agent.Version}");
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"ERROR: temporary agent cleanup failed: {error.Message}");
                    exitCode = 1;
                }
            }
            http?.Dispose();
        }
        return exitCode;
    }

    private sealed class HelpRequestedException : Exception { }
}

public static class AgentEvaluationAuth
{
    public static async Task<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "account", "get-access-token", "--scope", scope, "--query", "accessToken", "-o", "tsv" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Azure CLI.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"Azure CLI token acquisition failed: {error.Trim()}");
        return output.Trim();
    }
}
