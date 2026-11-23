#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    private const string ApiVersion = "2025-05-01";
    private const string Evaluator = "builtin.coherence";
    private const string UsageText = "Usage: dotnet run --file demo.cs -- [--project-endpoint HTTPS_URL] [--deployment NAME] [--judge-deployment NAME] [--prompt baseline|regression] [--correctness-threshold 0..1] [--coherence-threshold 0..1] [--timeout SECONDS] [--report PATH]\n       dotnet run --file demo.cs -- --help\n       dotnet run --file demo.cs -- --self-test";

    private sealed record DatasetItem(string Id, string Query, string Expected);
    private sealed record ResponseItem(string Id, string Query, string Expected, string Response, string ResponseId, bool Correct);
    private sealed record RunCounts(int Total, int Passed, int Failed, int Errored);

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--self-test"))
            return await SelfTest();
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(UsageText);
            return 0;
        }

        var parsed = ParseArguments(args);
        if (parsed.Error is not null)
            return UsageError(parsed.Error);
        var (projectEndpoint, deployment, judgeDeployment, promptName, correctnessThreshold,
            coherenceThreshold, timeout, reportPath, _) = parsed;
        if (!IsHttps(projectEndpoint))
            return UsageError("Set FOUNDRY_PROJECT_ENDPOINT");
        if (!double.IsFinite(correctnessThreshold) || correctnessThreshold is < 0 or > 1
            || !double.IsFinite(coherenceThreshold) || coherenceThreshold is < 0 or > 1)
            return UsageError("Thresholds must be between 0 and 1");
        if (!double.IsFinite(timeout) || timeout <= 0)
            return UsageError("Timeout must be positive");
        judgeDeployment = string.IsNullOrEmpty(judgeDeployment) ? deployment : judgeDeployment;

        byte[] datasetBytes;
        byte[] promptBytes;
        List<DatasetItem> dataset;
        try
        {
            datasetBytes = await File.ReadAllBytesAsync(Path.Combine(Environment.CurrentDirectory, "dataset.json"));
            promptBytes = await File.ReadAllBytesAsync(Path.Combine(Environment.CurrentDirectory, "prompts", $"{promptName}.txt"));
            dataset = ParseDataset(datasetBytes);
            if (dataset.Count == 0 || dataset.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != dataset.Count)
                return UsageError("Dataset must be nonempty and have unique IDs");
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
        var prompt = Encoding.UTF8.GetString(promptBytes);
        var report = CreateReport(datasetBytes, promptBytes, promptName, prompt, projectEndpoint!,
            deployment, judgeDeployment!, correctnessThreshold, coherenceThreshold);
        var exitCode = 0;
        try
        {
            var token = await AzureCliToken();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            await Execute(client, token, projectEndpoint!, deployment, judgeDeployment!, prompt, dataset,
                report, correctnessThreshold, coherenceThreshold, timeout);
        }
        catch (Exception error)
        {
            RecordFailure(report, error);
            Console.Error.WriteLine($"FAIL: {ErrorType(error)}: {error.Message}");
            exitCode = 1;
        }
        finally
        {
            report["finished_at"] = DateTimeOffset.UtcNow.ToString("O");
            try
            {
                await WriteReport(report, reportPath);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL: {ErrorType(error)}: {error.Message}");
                exitCode = 1;
            }
        }
        return exitCode;
    }

    private static (string? ProjectEndpoint, string Deployment, string? JudgeDeployment, string Prompt,
        double Correctness, double Coherence, double Timeout, string Report, string? Error)
        ParseArguments(string[] args)
    {
            args = ExpandEquals(args);
        var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
        string? judgeDeployment = null;
        var prompt = "baseline";
        var correctness = 1.0;
        var coherence = 1.0;
        var timeout = 600.0;
        var report = Path.Combine(Environment.CurrentDirectory, "evaluation-report.json");
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (index + 1 >= args.Length)
                return (endpoint, deployment, judgeDeployment, prompt, correctness, coherence, timeout, report, $"missing value for {argument}");
            var value = args[++index];
            switch (argument)
            {
                case "--project-endpoint": endpoint = value; break;
                case "--deployment": deployment = value; break;
                case "--judge-deployment": judgeDeployment = value; break;
                case "--prompt":
                    if (value is not ("baseline" or "regression"))
                        return (endpoint, deployment, judgeDeployment, prompt, correctness, coherence, timeout, report, "prompt must be baseline or regression");
                    prompt = value;
                    break;
                case "--correctness-threshold":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out correctness))
                        return (endpoint, deployment, judgeDeployment, prompt, correctness, coherence, timeout, report, "invalid correctness threshold");
                    break;
                case "--coherence-threshold":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out coherence))
                        return (endpoint, deployment, judgeDeployment, prompt, correctness, coherence, timeout, report, "invalid coherence threshold");
                    break;
                case "--timeout":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out timeout))
                        return (endpoint, deployment, judgeDeployment, prompt, correctness, coherence, timeout, report, "invalid timeout");
                    break;
                case "--report": report = value; break;
                default: return (endpoint, deployment, judgeDeployment, prompt, correctness, coherence, timeout, report, $"unrecognized argument: {argument}");
            }
        }
        return (endpoint, deployment, judgeDeployment, prompt, correctness, coherence, timeout, report, null);
    }

    private static string[] ExpandEquals(string[] args) =>
        args.SelectMany(argument =>
        {
            var separator = argument.IndexOf('=');
            return separator > 1 && argument.StartsWith("--", StringComparison.Ordinal)
                ? new[] { argument[..separator], argument[(separator + 1)..] }
                : new[] { argument };
        }).ToArray();

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"error: {message}\n{UsageText}");
        return 2;
    }

    private static bool IsHttps(string? value) =>
        value is not null && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    private static List<DatasetItem> ParseDataset(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Dataset must be a JSON array.");
        return document.RootElement.EnumerateArray().Select(row => new DatasetItem(
            row.GetProperty("id").GetString() ?? "",
            row.GetProperty("query").GetString() ?? "",
            row.GetProperty("expected").GetString() ?? "")).ToList();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Dictionary<string, object?> CreateReport(
        byte[] datasetBytes, byte[] promptBytes, string promptName, string prompt, string projectEndpoint,
        string deployment, string judgeDeployment, double correctnessThreshold, double coherenceThreshold) =>
        new()
        {
            ["started_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["commit"] = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
            ["dataset_sha256"] = Sha256(datasetBytes),
            ["dataset"] = DatasetJson(datasetBytes),
            ["prompt_variant"] = promptName,
            ["prompt_sha256"] = Sha256(promptBytes),
            ["prompt"] = prompt,
            ["project_endpoint"] = projectEndpoint,
            ["deployment"] = deployment,
            ["judge_deployment"] = judgeDeployment,
            ["evaluator"] = new { name = Evaluator, initialization_parameters = new { model = judgeDeployment } },
            ["thresholds"] = new { correctness = correctnessThreshold, coherence = coherenceThreshold },
            ["passed"] = false
        };

    private static JsonElement DatasetJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
    private static string ErrorType(Exception error) => error is HttpRequestException ? "RuntimeError" :
        error is TaskCanceledException or TimeoutException ? "TimeoutError" :
        error is JsonException ? "ValueError" : error.GetType().Name;

    private static async Task<string> AzureCliToken()
    {
        var start = new ProcessStartInfo("az")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in AzureCliArguments())
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Azure CLI.");
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Azure CLI could not obtain a Foundry token.");
        using var json = JsonDocument.Parse(await output);
        return json.RootElement.GetProperty("accessToken").GetString()
            ?? throw new JsonException("Azure CLI returned no access token.");
    }

    private static string[] AzureCliArguments() =>
        ["account", "get-access-token", "--scope", "https://ai.azure.com/.default", "-o", "json"];

    private static void RecordFailure(Dictionary<string, object?> report, Exception error)
    {
        report["passed"] = false;
        report["error"] = new { type = ErrorType(error), message = error.Message };
    }

    private static async Task WriteReport(Dictionary<string, object?> report, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Evaluation report: {path}");
    }

    private static async Task Execute(
        HttpClient client, string token, string endpoint, string deployment, string judgeDeployment,
        string prompt, List<DatasetItem> dataset, Dictionary<string, object?> report,
        double correctnessThreshold, double coherenceThreshold, double timeout)
    {
        var root = endpoint.TrimEnd('/');
        report["model"] = await RequestJson(client, token, HttpMethod.Get,
            $"{root}/deployments/{Uri.EscapeDataString(deployment)}?api-version={ApiVersion}");
        report["judge_model"] = await RequestJson(client, token, HttpMethod.Get,
            $"{root}/deployments/{Uri.EscapeDataString(judgeDeployment)}?api-version={ApiVersion}");

        var agentName = "ci-support-" + Guid.NewGuid().ToString("N")[..8];
        using var agentResponse = await SendJson(client, token, HttpMethod.Post,
            $"{root}/agents/{Uri.EscapeDataString(agentName)}/versions?api-version={ApiVersion}",
            new { definition = new { kind = "prompt", model = deployment, instructions = prompt } });
        using var agentJson = JsonDocument.Parse(await agentResponse.Content.ReadAsStringAsync());
        var version = agentJson.RootElement.GetProperty("version").GetString()
            ?? throw new JsonException("Foundry returned no agent version.");
        report["agent"] = new { name = agentName, version };
        try
        {
            var items = new List<ResponseItem>();
            foreach (var row in dataset)
            {
                using var response = await SendJson(client, token, HttpMethod.Post,
                    $"{root}/agents/{Uri.EscapeDataString(agentName)}/endpoint/protocols/openai/responses",
                    new { input = row.Query, store = true }, timeoutSeconds: 120);
                using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var status = responseJson.RootElement.GetProperty("status").GetString();
                var outputText = ExtractOutputText(responseJson.RootElement);
                if (status != "completed" || string.IsNullOrWhiteSpace(outputText))
                    throw new InvalidOperationException($"Agent response failed: {status}; {ReadError(responseJson.RootElement)}");
                var id = responseJson.RootElement.GetProperty("id").GetString()
                    ?? throw new JsonException("Foundry response has no ID.");
                items.Add(new ResponseItem(row.Id, row.Query, row.Expected, outputText, id, ExactAnswer(outputText, row.Expected)));
            }
            report["items"] = items.Select(item => new
            {
                id = item.Id, query = item.Query, expected = item.Expected,
                response_id = item.ResponseId, response = item.Response, correct = item.Correct
            }).ToArray();
            report["coherence_rate"] = await CloudEvaluate(client, token, root, items, judgeDeployment, report, timeout);
            ReleaseGate(items, dataset, report, correctnessThreshold, coherenceThreshold);
        }
        finally
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete,
                $"{root}/agents/{Uri.EscapeDataString(agentName)}/versions/{Uri.EscapeDataString(version)}?api-version={ApiVersion}");
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(delete);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                Console.Error.WriteLine("Warning: temporary prompt-agent version cleanup failed.");
        }
    }

    private static bool ExactAnswer(string text, string expected)
    {
        try
        {
            using var value = JsonDocument.Parse(text);
            return value.RootElement.ValueKind == JsonValueKind.Object
                && value.RootElement.TryGetProperty("answer", out var answer)
                && answer.ValueKind == JsonValueKind.String && answer.GetString() == expected;
        }
        catch (JsonException) { return false; }
    }

    private static async Task<double> CloudEvaluate(
        HttpClient client, string token, string root, List<ResponseItem> items, string deployment,
        Dictionary<string, object?> report, double timeout)
    {
        if (items.Count == 0 || items.Any(item => string.IsNullOrWhiteSpace(item.Query)
            || string.IsNullOrWhiteSpace(item.Response) || string.IsNullOrWhiteSpace(item.ResponseId)))
            throw new ArgumentException("Nonempty queries, actual responses and response IDs are required");
        var content = items.Select(item => new { item = new { query = item.Query, response = item.Response, response_id = item.ResponseId } }).ToArray();
        var source = new { type = "jsonl", source = new { type = "file_content", content } };
        report["evaluation_source"] = source;
        using var evalResponse = await SendJson(client, token, HttpMethod.Post, $"{root}/openai/v1/evals",
            new
            {
                name = "lab23-coherence",
                data_source_config = new
                {
                    type = "custom",
                    item_schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string" },
                            response = new { type = "string" },
                            response_id = new { type = "string" }
                        },
                        required = new[] { "query", "response", "response_id" }
                    }
                },
                testing_criteria = new[]
                {
                    new
                    {
                        type = "azure_ai_evaluator",
                        name = "coherence",
                        evaluator_name = Evaluator,
                        initialization_parameters = new { model = deployment },
                        data_mapping = new { query = "{{item.query}}", response = "{{item.response}}" }
                    }
                }
            });
        using var evalJson = JsonDocument.Parse(await evalResponse.Content.ReadAsStringAsync());
        var evalId = evalJson.RootElement.GetProperty("id").GetString()
            ?? throw new JsonException("Foundry evaluation response has no ID.");
        report["evaluation_id"] = evalId;
        using var runResponse = await SendJson(client, token, HttpMethod.Post,
            $"{root}/openai/v1/evals/{Uri.EscapeDataString(evalId)}/runs",
            new { name = "support-regression-gate", data_source = source });
        using var runJson = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
        var runId = runJson.RootElement.GetProperty("id").GetString()
            ?? throw new JsonException("Foundry evaluation run has no ID.");
        report["run_id"] = runId;
        var current = runJson.RootElement.Clone();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
        while (IsInProgress(current))
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"Evaluation timeout: {evalId}/{runId}");
            await Task.Delay(TimeSpan.FromSeconds(5));
            var poll = await RequestJson(client, token, HttpMethod.Get,
                $"{root}/openai/v1/evals/{Uri.EscapeDataString(evalId)}/runs/{Uri.EscapeDataString(runId)}");
            current = poll;
        }
        report["cloud_run"] = current;
        if (GetString(current, "status") != "completed")
            throw new InvalidOperationException($"Evaluation infrastructure status: {GetString(current, "status")}; error: {ReadError(current)}");
        var counts = ReadCounts(current);
        if (counts.Total != items.Count || counts.Errored != 0 || counts.Passed + counts.Failed != counts.Total)
            throw new InvalidOperationException($"Incomplete/errored evaluation: total={counts.Total}, passed={counts.Passed}, failed={counts.Failed}, errored={counts.Errored}");

        var outputItems = new List<JsonElement>();
        string? nextUrl = $"{root}/openai/v1/evals/{Uri.EscapeDataString(evalId)}/runs/{Uri.EscapeDataString(runId)}/output_items?limit=100";
        while (nextUrl is not null)
        {
            var page = await RequestJson(client, token, HttpMethod.Get, nextUrl);
            if (!page.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Missing or malformed evaluation output items.");
            outputItems.AddRange(rows.EnumerateArray().Select(row => row.Clone()));
            nextUrl = page.TryGetProperty("has_more", out var hasMore) && hasMore.GetBoolean()
                ? page.TryGetProperty("last_id", out var last) ? $"{root}/openai/v1/evals/{Uri.EscapeDataString(evalId)}/runs/{Uri.EscapeDataString(runId)}/output_items?limit=100&after={Uri.EscapeDataString(last.GetString()!)}" : null
                : null;
            if (nextUrl is null && page.TryGetProperty("has_more", out hasMore) && hasMore.GetBoolean())
                throw new InvalidOperationException("Evaluation output pagination omitted its cursor.");
        }
        report["cloud_items"] = outputItems;
        if (outputItems.Count != items.Count || outputItems.Any(item => GetString(item, "status") != "completed"))
            throw new InvalidOperationException("Missing or errored evaluation output items");
        var expected = items.ToDictionary(item => item.ResponseId, item => item, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var passed = 0;
        foreach (var item in outputItems)
        {
            var sourceItem = item.TryGetProperty("datasource_item", out var sourceNode) ? sourceNode : default;
            var id = sourceItem.ValueKind == JsonValueKind.Object ? GetString(sourceItem, "response_id") : null;
            if (id is null || !expected.TryGetValue(id, out var expectedItem) || !seen.Add(id)
                || !SameInput(sourceItem, expectedItem)
                || !item.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() != 1)
                throw new InvalidOperationException("Incomplete, mismatched or errored coherence result");
            var result = results[0];
            if (GetString(result, "name") != "coherence" || GetString(result, "status") != "completed"
                || !result.TryGetProperty("passed", out var passNode)
                || passNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("Incomplete, mismatched or errored coherence result");
            if (passNode.GetBoolean())
                passed++;
        }
        if (seen.Count != items.Count || passed != counts.Passed)
            throw new InvalidOperationException("Per-item coherence results disagree with aggregate counts");
        return (double)counts.Passed / counts.Total;
    }

    private static bool SameInput(JsonElement source, ResponseItem item) =>
        source.ValueKind == JsonValueKind.Object
        && source.EnumerateObject().Count() == 3
        && GetString(source, "query") == item.Query
        && GetString(source, "response") == item.Response
        && GetString(source, "response_id") == item.ResponseId;

    private static bool IsInProgress(JsonElement run)
    {
        var status = GetString(run, "status");
        return status is "queued" or "in_progress";
    }

    private static RunCounts ReadCounts(JsonElement run)
    {
        var counts = run.GetProperty("result_counts");
        return new RunCounts(counts.GetProperty("total").GetInt32(), counts.GetProperty("passed").GetInt32(),
            counts.GetProperty("failed").GetInt32(), counts.GetProperty("errored").GetInt32());
    }

    private static string? GetString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var node)
        && node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private static string? ExtractOutputText(JsonElement response)
    {
        var shortcut = GetString(response, "output_text");
        if (shortcut is not null)
            return shortcut;
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return null;
        var pieces = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
                if (GetString(part, "type") == "output_text" && GetString(part, "text") is { } text)
                    pieces.Add(text);
        }
        return pieces.Count == 0 ? null : string.Concat(pieces);
    }

    private static string ReadError(JsonElement value) =>
        value.TryGetProperty("error", out var error) ? error.ToString() : "";

    private static async Task<JsonElement> RequestJson(HttpClient client, string token, HttpMethod method, string uri)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        await EnsureSuccess(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendJson(
        HttpClient client, string token, HttpMethod method, string uri, object body, double? timeoutSeconds = null)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var timeout = timeoutSeconds.HasValue
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value))
            : null;
        var response = await client.SendAsync(request, timeout?.Token ?? CancellationToken.None);
        await EnsureSuccess(response);
        return response;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync();
        var message = body;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var text))
                message = text.GetString() ?? body;
        }
        catch (JsonException) { }
        throw new HttpRequestException($"Foundry REST request failed ({(int)response.StatusCode}): {message}",
            null, response.StatusCode);
    }

    private static void ReleaseGate(
        List<ResponseItem> items, List<DatasetItem> dataset, Dictionary<string, object?> report,
        double correctnessThreshold, double coherenceThreshold)
    {
        if (items.Count == 0)
            throw new InvalidOperationException("Empty dataset cannot pass");
        var accuracy = (double)items.Count(item => item.Correct) / items.Count;
        report["correctness_rate"] = accuracy;
        var coherence = Convert.ToDouble(report["coherence_rate"], System.Globalization.CultureInfo.InvariantCulture);
        var passed = accuracy >= correctnessThreshold && coherence >= coherenceThreshold;
        report["passed"] = passed;
        foreach (var item in items)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                id = item.Id, query = item.Query, expected = item.Expected, response_id = item.ResponseId,
                response = item.Response, correct = item.Correct
            }));
        }
        if (!passed)
            throw new InvalidOperationException($"Release blocked: correctness={accuracy.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}, coherence={coherence.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static async Task<int> SelfTest()
    {
        var datasetBytes = File.ReadAllBytes(Path.Combine(Environment.CurrentDirectory, "dataset.json"));
        var dataset = ParseDataset(datasetBytes);
        Check(dataset.Count == 3 && dataset[0].Expected == "14", "dataset loading");
        var promptBytes = File.ReadAllBytes(Path.Combine(Environment.CurrentDirectory, "prompts", "baseline.txt"));
        var reportFixture = CreateReport(datasetBytes, promptBytes, "baseline", Encoding.UTF8.GetString(promptBytes),
            "https://project.example", "model", "judge", 1, 1);
        Check((string)reportFixture["dataset_sha256"]! == Sha256(datasetBytes)
              && (string)reportFixture["prompt_sha256"]! == Sha256(promptBytes)
              && reportFixture["evaluator"] is not null
              && reportFixture["thresholds"] is not null,
            "reproducibility report metadata");
        Check(AzureCliArguments().SequenceEqual(
            ["account", "get-access-token", "--scope", "https://ai.azure.com/.default", "-o", "json"]),
            "Foundry authentication scope");
        var artifactPath = Path.Combine(Environment.CurrentDirectory, $".offline-evaluation-report-{Guid.NewGuid():N}.json");
        var previousOutput = Console.Out;
        try
        {
            RecordFailure(reportFixture, new InvalidOperationException("evaluation unavailable"));
            reportFixture["finished_at"] = DateTimeOffset.UtcNow.ToString("O");
            using var output = new StringWriter();
            Console.SetOut(output);
            await WriteReport(reportFixture, artifactPath);
            Check(output.ToString().Contains($"Evaluation report: {artifactPath}", StringComparison.Ordinal),
                "report path output");
            using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(artifactPath));
            Check(!artifact.RootElement.GetProperty("passed").GetBoolean()
                  && artifact.RootElement.GetProperty("error").GetProperty("message").GetString() == "evaluation unavailable"
                  && artifact.RootElement.GetProperty("error").GetProperty("type").GetString() == nameof(InvalidOperationException)
                  && artifact.RootElement.GetProperty("dataset_sha256").GetString() == Sha256(datasetBytes)
                  && artifact.RootElement.GetProperty("prompt_sha256").GetString() == Sha256(promptBytes)
                  && artifact.RootElement.GetProperty("evaluator").GetProperty("name").GetString() == Evaluator
                  && artifact.RootElement.GetProperty("evaluator").GetProperty("initialization_parameters")
                      .GetProperty("model").GetString() == "judge"
                  && artifact.RootElement.GetProperty("thresholds").GetProperty("correctness").GetDouble() == 1
                  && artifact.RootElement.TryGetProperty("finished_at", out _),
                "failed report artifact contract");
        }
        finally
        {
            Console.SetOut(previousOutput);
            if (File.Exists(artifactPath))
                File.Delete(artifactPath);
        }
        var workflow = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "workflow.yml"));
        foreach (var contract in new[]
                 {
                     "actions/setup-dotnet@v4", "dotnet-version: '10.0.x'",
                     "paths:", "23-evaluation-cicd/**", "workflow_dispatch:",
                     "environment: foundry-evaluation", "id-token: write",
                     "head.repo.full_name == github.repository",
                     "--correctness-threshold 1 --coherence-threshold 1",
                     "if: always()", "evaluation-report.json"
                 })
            Check(workflow.Contains(contract, StringComparison.Ordinal), $"workflow security/report contract: {contract}");
        Check(!workflow.Contains("setup-python", StringComparison.Ordinal)
              && !workflow.Contains("pip install", StringComparison.Ordinal)
              && !workflow.Contains("python demo.py", StringComparison.Ordinal),
            "workflow uses the .NET runner");
        Check(ExactAnswer("{\"answer\":\"14\"}", "14"), "exact string answer");
        Check(!ExactAnswer("not JSON", "14") && !ExactAnswer("{\"answer\":14}", "14")
              && !ExactAnswer("```json\n{\"answer\":\"14\"}\n```", "14"), "strict malformed answer rejection");
        var baseline = new List<ResponseItem>
        {
            new(dataset[0].Id, dataset[0].Query, dataset[0].Expected, "{\"answer\":\"14\"}", "r1", true),
            new(dataset[1].Id, dataset[1].Query, dataset[1].Expected, "{\"answer\":\"3\"}", "r2", true),
            new(dataset[2].Id, dataset[2].Query, dataset[2].Expected, "{\"answer\":\"help@example.invalid\"}", "r3", true)
        };
        var report = new Dictionary<string, object?> { ["coherence_rate"] = 1.0 };
        ReleaseGate(baseline, dataset, report, 1, 1);
        Check((bool)report["passed"]!, "release gate passes exact results");
        var regression = baseline.Select((item, index) => item with
        {
            Correct = index == 0,
            Response = index == 0 ? item.Response : "{\"answer\":\"wrong\"}"
        }).ToList();
        var blocked = new Dictionary<string, object?> { ["coherence_rate"] = 1.0 };
        Throws<InvalidOperationException>(() => ReleaseGate(regression, dataset, blocked, 1, 1));
        Check(!(bool)blocked["passed"]!, "regression fails despite coherence");
        Throws<InvalidOperationException>(() => ReleaseGate([], dataset, new Dictionary<string, object?> { ["coherence_rate"] = 1 }, 1, 1));
        foreach (var name in new[] { "baseline", "regression" })
        {
            var prompt = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "prompts", $"{name}.txt"));
            Check(prompt.Contains("Return raw JSON", StringComparison.Ordinal)
                  && prompt.Contains("do not use Markdown code fences", StringComparison.Ordinal),
                $"{name} prompt response contract");
        }

        var apiItems = dataset.Select((row, index) => new ResponseItem(row.Id, row.Query, row.Expected,
            $"{{\"answer\":\"{row.Expected}\"}}", $"r{index}", true)).ToList();
        var capturedRequests = new List<(string Url, string Body)>();
        var outputItems = apiItems.Select(item => new
        {
            status = "completed",
            datasource_item = new { query = item.Query, response = item.Response, response_id = item.ResponseId },
            results = new[] { new { name = "coherence", status = "completed", passed = true } }
        }).ToArray();
        var replies = new Queue<HttpResponseMessage>(
        [
            JsonResponse("{\"id\":\"eval-1\"}"),
            JsonResponse("{\"id\":\"run-1\",\"status\":\"completed\",\"result_counts\":{\"total\":3,\"passed\":3,\"failed\":0,\"errored\":0}}"),
            JsonResponse(JsonSerializer.Serialize(new { data = outputItems, has_more = false }))
        ]);
        using (var evalClient = new HttpClient(new StubHandler(async (request, _) =>
               {
                   Check(request.Headers.Authorization?.Scheme == "Bearer"
                         && request.Headers.Authorization.Parameter == "test-token",
                       "evaluation API bearer auth");
                   var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                   capturedRequests.Add((request.RequestUri!.AbsoluteUri, body));
                   return replies.Dequeue();
               })))
        {
            var cloudReport = new Dictionary<string, object?>();
            var rate = await CloudEvaluate(evalClient, "test-token", "https://project.example", apiItems,
                "judge-model", cloudReport, 600);
            Check(rate == 1 && (string)cloudReport["evaluation_id"]! == "eval-1", "real evaluation API contract");
            Check(capturedRequests.Count == 3 && capturedRequests[0].Url.EndsWith("/openai/v1/evals", StringComparison.Ordinal),
                "evaluation REST paths");
            using var evalBody = JsonDocument.Parse(capturedRequests[0].Body);
            var criterion = evalBody.RootElement.GetProperty("testing_criteria")[0];
            Check(criterion.GetProperty("evaluator_name").GetString() == Evaluator
                  && criterion.GetProperty("initialization_parameters").GetProperty("model").GetString() == "judge-model"
                  && criterion.GetProperty("data_mapping").GetProperty("response").GetString() == "{{item.response}}",
                "coherence evaluator configuration");
            using var runBody = JsonDocument.Parse(capturedRequests[1].Body);
            Check(runBody.RootElement.GetProperty("data_source").GetProperty("source").GetProperty("content").GetArrayLength() == 3,
                "inline actual-response evaluation dataset");
            Check(((List<JsonElement>)cloudReport["cloud_items"]!).Count == 3, "per-item evaluation output");
        }
        var executionResponses = new List<ResponseItem>();
        var agentEndpointCalls = 0;
        using (var executeClient = new HttpClient(new StubHandler(async (request, cancellationToken) =>
               {
                   Check(request.Headers.Authorization?.Scheme == "Bearer"
                         && request.Headers.Authorization.Parameter == "test-token",
                       "Foundry endpoint bearer auth");
                   var path = request.RequestUri!.AbsolutePath;
                   if (path.Contains("/deployments/", StringComparison.Ordinal))
                       return JsonResponse("{\"modelName\":\"gpt-4.1-mini\",\"modelVersion\":\"2025-04-14\"}");
                   if (path.Contains("/agents/", StringComparison.Ordinal) && path.EndsWith("/versions", StringComparison.Ordinal))
                   {
                       using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                       Check(body.RootElement.GetProperty("definition").GetProperty("kind").GetString() == "prompt"
                             && body.RootElement.GetProperty("definition").GetProperty("instructions").GetString()
                             == "synthetic instructions",
                           "versioned prompt-agent definition request");
                       return JsonResponse("{\"version\":\"1\"}");
                   }
                   if (path.Contains("/endpoint/protocols/openai/responses", StringComparison.Ordinal))
                   {
                       agentEndpointCalls++;
                       using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                       Check(body.RootElement.GetProperty("store").GetBoolean(), "stored agent response");
                       Check(!body.RootElement.TryGetProperty("agent_reference", out _), "agent-bound response endpoint");
                       var query = body.RootElement.GetProperty("input").GetString()!;
                       var row = dataset.Single(item => item.Query == query);
                       var responseText = JsonSerializer.Serialize(new { answer = row.Expected });
                       var responseId = $"response-{row.Id}";
                       executionResponses.Add(new ResponseItem(row.Id, row.Query, row.Expected, responseText, responseId, true));
                       return JsonResponse(JsonSerializer.Serialize(new
                       {
                           id = responseId,
                           status = "completed",
                           output = new[]
                           {
                               new { type = "message", content = new[] { new { type = "output_text", text = responseText } } }
                           }
                       }));
                   }
                   if (path.EndsWith("/openai/v1/evals", StringComparison.Ordinal))
                       return JsonResponse("{\"id\":\"execute-eval\"}");
                   if (path.EndsWith("/runs", StringComparison.Ordinal))
                       return JsonResponse("{\"id\":\"execute-run\",\"status\":\"completed\",\"result_counts\":{\"total\":3,\"passed\":3,\"failed\":0,\"errored\":0}}");
                   if (path.EndsWith("/output_items", StringComparison.Ordinal))
                   {
                       var data = executionResponses.Select(item => new
                       {
                           status = "completed",
                           datasource_item = new { query = item.Query, response = item.Response, response_id = item.ResponseId },
                           results = new[] { new { name = "coherence", status = "completed", passed = true } }
                       });
                       return JsonResponse(JsonSerializer.Serialize(new { data, has_more = false }));
                   }
                   if (request.Method == HttpMethod.Delete)
                       return new HttpResponseMessage(HttpStatusCode.NoContent);
                   throw new InvalidOperationException($"Unexpected offline REST request: {request.Method} {path}");
               })))
        {
            var executionReport = new Dictionary<string, object?>();
            await Execute(executeClient, "test-token", "https://project.example", "model", "judge",
                "synthetic instructions", dataset, executionReport, 1, 1, 600);
            Check(agentEndpointCalls == 3 && (bool)executionReport["passed"]!, "agent endpoint and full release workflow");
            Check(executionReport.ContainsKey("model") && executionReport.ContainsKey("judge_model")
                  && executionReport.ContainsKey("agent"), "Foundry metadata report");
        }
        var timedOutReplies = new Queue<HttpResponseMessage>(
        [
            JsonResponse("{\"id\":\"eval-1\"}"),
            JsonResponse("{\"id\":\"run-1\",\"status\":\"queued\",\"result_counts\":{\"total\":3,\"passed\":0,\"failed\":0,\"errored\":0}}")
        ]);
        using (var timeoutClient = new HttpClient(new StubHandler((_, _) => Task.FromResult(timedOutReplies.Dequeue()))))
            await ThrowsAsync<TimeoutException>(() => CloudEvaluate(timeoutClient, "token", "https://project.example",
                apiItems, "judge", new Dictionary<string, object?>(), 0));
        var malformedReplies = new Queue<HttpResponseMessage>(
        [
            JsonResponse("{\"id\":\"eval-1\"}"),
            JsonResponse("{\"id\":\"run-1\",\"status\":\"failed\",\"result_counts\":{\"total\":3,\"passed\":0,\"failed\":0,\"errored\":0}}")
        ]);
        using (var failedClient = new HttpClient(new StubHandler((_, _) => Task.FromResult(malformedReplies.Dequeue()))))
            await ThrowsAsync<InvalidOperationException>(() => CloudEvaluate(failedClient, "token", "https://project.example",
                apiItems, "judge", new Dictionary<string, object?>(), 600));
        await ThrowsAsync<ArgumentException>(() => CloudEvaluate(new HttpClient(), "token", "https://project.example",
            [], "judge", new Dictionary<string, object?>(), 600));
        Check(UsageText.Contains("--report", StringComparison.Ordinal), "help contract");
        Console.WriteLine("PASS: dataset/answer validation, correctness gate and regression rejection.");
        return 0;
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Offline test expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"Offline test failed: {name}");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Offline test expected {typeof(T).Name}.");
    }
}
