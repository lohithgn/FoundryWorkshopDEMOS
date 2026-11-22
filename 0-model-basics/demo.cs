#:property PublishAot=false
#:package Azure.Identity@1.17.0

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

const string SamplingPrompt = "Invent one short, playful name for a beginner AI workshop. Return only the name.";
const string ComparisonPrompt = "Tasks A, B, C, D take 4, 3, 5, 2 minutes respectively. B and C must wait for A; D must wait for both B and C. Two workers are available, each task needs one worker, and tasks cannot be interrupted. B and C can run at the same time after A completes. What is the minimum total duration? Return the minimum duration in minutes.";
const int ChatMaxRetries = 8;

try
{
    return await RunAsync(args);
}
catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine($"ERROR: {error.GetType().Name}: {error.Message}");
    return 2;
}
catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or Azure.RequestFailedException)
{
    Console.Error.WriteLine($"ERROR: {error.GetType().Name}: {error.Message}");
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    var options = Parse(args);
    if (options.Help)
    {
        PrintUsage();
        return 0;
    }

    Concepts();
    if (options.Section == "concepts")
        return 0;
    if (string.IsNullOrWhiteSpace(options.Endpoint))
        throw new ArgumentException("Set AZURE_OPENAI_ENDPOINT or pass --endpoint.");

    var endpoint = new Uri(options.Endpoint.TrimEnd('/') + "/");
    if (endpoint.Scheme != Uri.UriSchemeHttps)
        throw new ArgumentException("--endpoint must be an absolute HTTPS URL.");
    var required = new List<string>();
    if (options.Section is "sampling" or "compare" or "all") required.Add(options.Deployment);
    if (options.Section is "compare" or "all") required.Add(options.ReasoningDeployment);
    if (options.Section is "batch" or "all") required.Add(options.BatchDeployment);
    if (required.Any(string.IsNullOrWhiteSpace))
        throw new ArgumentException("Selected model deployment names must be nonempty.");

    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ExcludeInteractiveBrowserCredential = true
    });
    var token = await credential.GetTokenAsync(
        new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]));
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    client.BaseAddress = new Uri(endpoint, "openai/v1/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    if (options.Section is "sampling" or "compare" or "all")
    {
        Console.WriteLine($"Chat inference allows up to {ChatMaxRetries} SDK retries for transient errors; batch submission has no automatic retries.");
        var pacer = new RequestPacer(options.RequestIntervalSeconds);
        if (options.RequestIntervalSeconds > 0)
            Console.WriteLine($"Online request starts are paced by at least {options.RequestIntervalSeconds:g}s.");
        if (options.Section is "sampling" or "all")
            await SamplingAsync(client, options.Deployment, options.Samples, pacer);
        if (options.Section is "compare" or "all")
            await CompareAsync(client, options.Deployment, options.ReasoningDeployment, options.ReasoningEffort, pacer);
    }
    if (options.Section is "batch" or "all")
        await RunBatchAsync(client, endpoint, options.BatchDeployment, options.StateDirectory, options.BatchId, options.WaitSeconds);
    return 0;
}

static (string Section, string? Endpoint, string Deployment, string ReasoningDeployment, string BatchDeployment, int Samples, string ReasoningEffort, double RequestIntervalSeconds, string? BatchId, int WaitSeconds, string StateDirectory, bool Help) Parse(string[] args)
{
    string? endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
    var result = ("all", endpoint, Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "basics-chat",
        Environment.GetEnvironmentVariable("AZURE_OPENAI_REASONING_DEPLOYMENT") ?? "basics-reasoning",
        Environment.GetEnvironmentVariable("AZURE_OPENAI_BATCH_DEPLOYMENT") ?? "basics-batch",
        3, "medium", 0d, (string?)null, 120,
        Path.Combine(AppContext.BaseDirectory, ".azure"), false);
    var stateDir = Path.Combine(Environment.CurrentDirectory, ".azure");
    result.Item11 = stateDir;
    for (var i = 0; i < args.Length; i++)
    {
        string Value()
        {
            if (++i >= args.Length) throw new ArgumentException($"Missing value for {args[i - 1]}.");
            return args[i];
        }
        switch (args[i])
        {
            case "--help": case "-h": result.Item12 = true; break;
            case "--section": result.Item1 = Value(); break;
            case "--endpoint": result.Item2 = Value(); break;
            case "--deployment": result.Item3 = Value(); break;
            case "--reasoning-deployment": result.Item4 = Value(); break;
            case "--batch-deployment": result.Item5 = Value(); break;
            case "--samples": result.Item6 = int.Parse(Value(), CultureInfo.InvariantCulture); break;
            case "--reasoning-effort": result.Item7 = Value(); break;
            case "--request-interval-seconds": result.Item8 = double.Parse(Value(), CultureInfo.InvariantCulture); break;
            case "--batch-id": result.Item9 = Value(); break;
            case "--wait-seconds": result.Item10 = int.Parse(Value(), CultureInfo.InvariantCulture); break;
            case "--state-dir": result.Item11 = Value(); break;
            default: throw new ArgumentException($"Unknown argument: {args[i]}");
        }
    }
    if (result.Item1 is not ("concepts" or "sampling" or "compare" or "batch" or "all"))
        throw new ArgumentException("--section must be concepts, sampling, compare, batch, or all.");
    if (result.Item6 is < 1 or > 5) throw new ArgumentException("--samples must be between 1 and 5.");
    if (result.Item7 is not ("low" or "medium" or "high")) throw new ArgumentException("--reasoning-effort must be low, medium, or high.");
    if (result.Item10 < 0) throw new ArgumentException("--wait-seconds must be nonnegative.");
    if (!double.IsFinite(result.Item8) || result.Item8 < 0) throw new ArgumentException("--request-interval-seconds must be finite and nonnegative.");
    if (result.Item9 is not null && string.IsNullOrWhiteSpace(result.Item9)) throw new ArgumentException("--batch-id must be nonempty.");
    return result;
}

static void PrintUsage() =>
    Console.WriteLine("Usage: dotnet run --file demo.cs -- [--section concepts|sampling|compare|batch|all] [--endpoint URL] [--deployment NAME] [--reasoning-deployment NAME] [--batch-deployment NAME] [--samples 1..5] [--reasoning-effort low|medium|high] [--request-interval-seconds N] [--batch-id ID] [--wait-seconds N] [--state-dir PATH]");

static void Concepts()
{
    double[] probabilities = [0.5, 0.3, 0.15, 0.05];
    Console.WriteLine("LOCAL TOY EXAMPLE, not probabilities obtained from a model.");
    Console.WriteLine("Next-token candidates A/B/C/D: [0.5, 0.3, 0.15, 0.05]");
    foreach (var temperature in new[] { 0d, 0.2, 1d, 2d })
        Console.WriteLine($"temperature={temperature}: [{string.Join(", ", TemperatureDistribution(probabilities, temperature).Select(x => x.ToString("0.####", CultureInfo.InvariantCulture)))}]");
    foreach (var topP in new[] { 0.5, 0.8, 1d })
        Console.WriteLine($"top_p={topP}: keep [{string.Join(", ", Nucleus(probabilities, topP).Select(i => (char)('A' + i)))}]");
    Console.WriteLine("Temperature reshapes probabilities; top_p keeps a cumulative-probability nucleus,");
    Console.WriteLine("then sampling renormalizes the retained probabilities. Change one knob at a time.");
    Console.WriteLine("Low temperature is not a correctness or reproducibility guarantee.");
    Console.WriteLine("Chat/reasoning are model choices; batch is asynchronous request scheduling.");
    Console.WriteLine("gpt-5-mini uses reasoning_effort, not temperature/top_p; reasoning tokens are billed.");
}

static double[] TemperatureDistribution(IReadOnlyList<double> probabilities, double temperature)
{
    if (probabilities.Count == 0 || probabilities.Any(p => !double.IsFinite(p) || p <= 0))
        throw new ArgumentException("Toy probabilities must be finite and positive.");
    if (Math.Abs(probabilities.Sum() - 1) > 1e-9)
        throw new ArgumentException("Toy probabilities must sum to one.");
    if (!double.IsFinite(temperature) || temperature < 0)
        throw new ArgumentException("Temperature must be finite and nonnegative.");
    if (temperature == 0)
    {
        var winner = Enumerable.Range(0, probabilities.Count).MaxBy(i => probabilities[i]);
        return Enumerable.Range(0, probabilities.Count).Select(i => i == winner ? 1d : 0d).ToArray();
    }
    var logits = probabilities.Select(p => Math.Log(p) / temperature).ToArray();
    var maximum = logits.Max();
    var weights = logits.Select(value => Math.Exp(value - maximum)).ToArray();
    return weights.Select(weight => weight / weights.Sum()).ToArray();
}

static int[] Nucleus(IReadOnlyList<double> probabilities, double topP)
{
    _ = TemperatureDistribution(probabilities, 1);
    if (!double.IsFinite(topP) || topP is <= 0 or > 1)
        throw new ArgumentException("Toy top_p must be in (0, 1].");
    var selected = new List<int>();
    var cumulative = 0d;
    foreach (var index in Enumerable.Range(0, probabilities.Count).OrderByDescending(i => probabilities[i]))
    {
        selected.Add(index);
        cumulative += probabilities[index];
        if (cumulative >= topP) break;
    }
    return selected.ToArray();
}

static JsonObject ChatRequest(string deployment, string prompt, double temperature = 1, double topP = 1) =>
    new()
    {
        ["model"] = deployment,
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
        ["max_tokens"] = 256,
        ["temperature"] = temperature,
        ["top_p"] = topP
    };

static JsonObject ReasoningRequest(string deployment, string effort) =>
    new()
    {
        ["model"] = deployment,
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = ComparisonPrompt }),
        ["max_completion_tokens"] = 4096,
        ["reasoning_effort"] = effort,
        ["response_format"] = ComparisonFormat()
    };

static JsonObject ComparisonFormat() => new()
{
    ["type"] = "json_schema",
    ["json_schema"] = new JsonObject
    {
        ["name"] = "minimum_duration",
        ["strict"] = true,
        ["schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["minutes"] = new JsonObject { ["type"] = "integer" } },
            ["required"] = new JsonArray("minutes"),
            ["additionalProperties"] = false
        }
    }
};

static JsonObject ComparisonRequest(string deployment)
{
    var request = ChatRequest(deployment, ComparisonPrompt, 0);
    request["response_format"] = ComparisonFormat();
    return request;
}

static async Task<JsonObject> SendJsonAsync(HttpClient client, string path, JsonNode body, bool retry)
{
    for (var attempt = 0; ; attempt++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        if (retry && attempt < ChatMaxRetries && ShouldRetry(response.StatusCode))
        {
            await Task.Delay(RetryDelay(response, attempt));
            continue;
        }
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {content}", null, response.StatusCode);
        return JsonNode.Parse(content)?.AsObject() ?? throw new JsonException("The service returned invalid JSON.");
    }
}

static bool ShouldRetry(HttpStatusCode status) =>
    status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
{
    if (response.Headers.RetryAfter?.Delta is { } delta && delta <= TimeSpan.FromSeconds(65) && delta >= TimeSpan.Zero)
        return delta;
    if (response.Headers.TryGetValues("retry-after-ms", out var values) &&
        double.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var milliseconds) && milliseconds is >= 0 and <= 65000)
        return TimeSpan.FromMilliseconds(milliseconds);
    return TimeSpan.FromSeconds(Math.Min(1 << attempt, 30));
}

static async Task<string> ShowCompletionAsync(JsonObject response, Stopwatch elapsed)
{
    var choices = response["choices"]?.AsArray();
    if (choices is null || choices.Count == 0) throw new InvalidOperationException("Service returned no choices.");
    var choice = choices[0]?.AsObject() ?? new JsonObject();
    var message = choice["message"]?.AsObject() ?? new JsonObject();
    var content = message["content"]?.GetValue<string>();
    var finish = choice["finish_reason"]?.ToString();
    if (finish != "stop" || string.IsNullOrWhiteSpace(content))
        throw new InvalidOperationException($"Incomplete/empty response: finish_reason={finish}, refusal={message["refusal"]}, usage={response["usage"]}");
    Console.WriteLine($"Model: {response["model"]} | elapsed: {elapsed.Elapsed.TotalSeconds:F3}s | finish: {finish}");
    Console.WriteLine($"Usage: {response["usage"]?.ToJsonString() ?? "Not returned by service"}");
    Console.WriteLine($"Answer: {content}");
    return content.Trim();
}

static async Task SamplingAsync(HttpClient client, string deployment, int samples, RequestPacer pacer)
{
    foreach (var (temperature, topP) in new[] { (0d, 1d), (0.7, 1d), (1.4, 1d), (1d, 0.2), (1d, 0.8), (1d, 1d) })
    {
        Console.WriteLine($"\nSame prompt: temperature={temperature}, top_p={topP}");
        var answers = new HashSet<string>(StringComparer.Ordinal);
        for (var sample = 0; sample < samples; sample++)
        {
            await pacer.WaitAsync();
            var elapsed = Stopwatch.StartNew();
            var response = await SendJsonAsync(client, "chat/completions", ChatRequest(deployment, SamplingPrompt, temperature, topP), retry: true);
            answers.Add(await ShowCompletionAsync(response, elapsed));
        }
        Console.WriteLine($"Distinct answers: {answers.Count}/{samples} (illustration, not a benchmark)");
    }
}

static async Task CompareAsync(HttpClient client, string chat, string reasoning, string effort, RequestPacer pacer)
{
    Console.WriteLine($"\nSame scheduling problem: {ComparisonPrompt}");
    foreach (var (label, request) in new[]
    {
        ("Typical chat (GPT-4.1-mini)", ComparisonRequest(chat)),
        ($"Reasoning (gpt-5-mini, effort={effort})", ReasoningRequest(reasoning, effort))
    })
    {
        Console.WriteLine($"\n{label}");
        await pacer.WaitAsync();
        var elapsed = Stopwatch.StartNew();
        var response = await SendJsonAsync(client, "chat/completions", request, retry: true);
        var answer = await ShowCompletionAsync(response, elapsed);
        var minutes = ComparisonMinutes(answer);
        Console.WriteLine($"Matches reference answer 11: {minutes == 11}");
    }
    Console.WriteLine("Reference: A 0-4; B 4-7 and C 4-9; D 9-11. Both models may be correct.");
    Console.WriteLine("One problem does not establish quality, latency or cost superiority.");
}

static int ComparisonMinutes(string answer)
{
    try
    {
        using var json = JsonDocument.Parse(answer);
        if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 1 ||
            !json.RootElement.TryGetProperty("minutes", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var minutes) || minutes <= 0)
            throw new InvalidOperationException("Comparison answer must contain one positive integer minutes field.");
        return minutes;
    }
    catch (JsonException error)
    {
        throw new InvalidOperationException("Comparison answer is not valid JSON.", error);
    }
}

static JsonArray BatchRows(string deployment)
{
    var prompts = GetBatchPrompts();
    var rows = new JsonArray();
    for (var index = 0; index < prompts.Length; index++)
        rows.Add(new JsonObject
        {
            ["custom_id"] = $"task-{index}",
            ["method"] = "POST",
            ["url"] = "/v1/chat/completions",
            ["body"] = ChatRequest(deployment, prompts[index], 0)
        });
    return rows;
}

static string[] GetBatchPrompts() =>
[
    "Explain the temperature parameter in language-model text generation in one sentence for a beginner.",
    "Explain the top_p parameter in language-model text generation in one sentence for a beginner.",
    "Explain when asynchronous batch processing is useful in one sentence."
];

static JsonArray ValidateBatchResults(string text)
{
    var expected = Enumerable.Range(0, GetBatchPrompts().Length).Select(i => $"task-{i}").ToHashSet(StringComparer.Ordinal);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var rows = new List<JsonObject>();
    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var row = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidOperationException("Invalid batch JSONL row.");
        var id = row["custom_id"]?.GetValue<string>();
        if (id is null || !expected.Contains(id) || !seen.Add(id))
            throw new InvalidOperationException($"Unexpected or duplicate batch custom_id: {id ?? "(missing)"}");
        var response = row["response"]?.AsObject();
        if (row["error"] is not null && row["error"]!.ToJsonString() != "null" ||
            response?["status_code"]?.GetValue<int>() != 200)
            throw new InvalidOperationException($"Batch request failed: {row}");
        var choice = response?["body"]?["choices"]?.AsArray()?.FirstOrDefault()?.AsObject();
        var content = choice?["message"]?["content"]?.GetValue<string>();
        if (choice is null) throw new InvalidOperationException($"Missing choices for {id}");
        if (choice["finish_reason"]?.ToString() != "stop" || string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException($"Incomplete/empty batch answer for {id}: {choice}");
        rows.Add(row);
    }
    if (!seen.SetEquals(expected))
        throw new InvalidOperationException($"Missing batch results: {string.Join(", ", expected.Except(seen).Order())}");
    var ordered = new JsonArray();
    foreach (var row in rows.OrderBy(r => r["custom_id"]!.ToString(), StringComparer.Ordinal))
        ordered.Add(row);
    return ordered;
}

static async Task RunBatchAsync(HttpClient client, Uri endpoint, string deployment, string stateDirectory, string? batchId, int waitSeconds)
{
    Directory.CreateDirectory(stateDirectory);
    var statePath = Path.Combine(stateDirectory, "batch-state.json");
    if (batchId is null && File.Exists(statePath))
    {
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
        var root = saved.RootElement;
        if (root.GetProperty("endpoint").GetString() != endpoint.ToString().TrimEnd('/') ||
            root.GetProperty("deployment").GetString() != deployment)
            throw new ArgumentException("Saved batch belongs to a different endpoint/deployment; use a different --state-dir.");
        batchId = root.GetProperty("batch_id").GetString();
    }
    if (batchId is null)
    {
        var jsonl = string.Join("", BatchRows(deployment).Select(row => row!.ToJsonString() + "\n"));
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("batch"), "purpose");
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(jsonl));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");
        form.Add(file, "file", "basics.jsonl");
        using var upload = await client.PostAsync("files", form);
        var uploadText = await upload.Content.ReadAsStringAsync();
        if (!upload.IsSuccessStatusCode) throw new HttpRequestException($"Batch input upload failed: {uploadText}", null, upload.StatusCode);
        var inputFileId = JsonNode.Parse(uploadText)?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Upload response omitted file ID.");
        Console.WriteLine($"Uploaded input file: {inputFileId}");
        var jobBody = new JsonObject { ["input_file_id"] = inputFileId, ["endpoint"] = "/chat/completions", ["completion_window"] = "24h" };
        var job = await SendJsonAsync(client, "batches", jobBody, retry: false);
        batchId = job["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Batch response omitted ID.");
        await File.WriteAllTextAsync(statePath, new JsonObject
        {
            ["endpoint"] = endpoint.ToString().TrimEnd('/'), ["deployment"] = deployment,
            ["batch_id"] = batchId, ["input_file_id"] = inputFileId
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
    Console.WriteLine($"Batch ID: {batchId} | resume with --section batch; no new submission.");
    var deadline = Stopwatch.StartNew();
    while (true)
    {
        using var statusResponse = await client.GetAsync($"batches/{Uri.EscapeDataString(batchId)}");
        var statusText = await statusResponse.Content.ReadAsStringAsync();
        if (!statusResponse.IsSuccessStatusCode) throw new HttpRequestException($"Batch retrieval failed: {statusText}", null, statusResponse.StatusCode);
        var job = JsonNode.Parse(statusText)?.AsObject() ?? throw new JsonException("Invalid batch status response.");
        var status = job["status"]?.ToString();
        Console.WriteLine($"Batch status: {status} | request counts: {job["request_counts"]?.ToJsonString() ?? "null"}");
        if (status == "completed")
        {
            var errorFile = job["error_file_id"]?.ToString();
            if (!string.IsNullOrEmpty(errorFile) && errorFile != "null")
            {
                var errors = await GetFileContentAsync(client, errorFile);
                var errorPath = Path.Combine(stateDirectory, "batch-errors.jsonl");
                await File.WriteAllTextAsync(errorPath, errors);
                throw new InvalidOperationException($"Batch contains failed requests; see {errorPath}");
            }
            var outputFile = job["output_file_id"]?.ToString();
            if (string.IsNullOrEmpty(outputFile) || outputFile == "null") throw new InvalidOperationException("Completed batch has no output file.");
            var output = await GetFileContentAsync(client, outputFile);
            await File.WriteAllTextAsync(Path.Combine(stateDirectory, "batch-output.jsonl"), output);
            foreach (var row in ValidateBatchResults(output))
            {
                var body = row!["response"]!["body"]!;
                Console.WriteLine($"{row["custom_id"]} | model: {body["model"]} | usage: {body["usage"]?.ToJsonString() ?? "Not returned by service"}");
                Console.WriteLine($"Answer: {body["choices"]![0]!["message"]!["content"]}");
            }
            Console.WriteLine("PASS: all three native batch requests completed and validated.");
            return;
        }
        if (status is "failed" or "expired" or "cancelled")
            throw new InvalidOperationException($"Batch {batchId} ended as {status}: {job["errors"]}");
        var remaining = waitSeconds - deadline.Elapsed.TotalSeconds;
        if (remaining <= 0)
            throw new InvalidOperationException($"Batch {batchId} is still {status}; NOT a completed live test. Rerun --section batch later to resume. The service target window is 24 hours.");
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, remaining)));
    }
}

static async Task<string> GetFileContentAsync(HttpClient client, string id)
{
    using var response = await client.GetAsync($"files/{Uri.EscapeDataString(id)}/content");
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Batch file download failed: {body}", null, response.StatusCode);
    return body;
}

sealed class RequestPacer(double intervalSeconds)
{
    private readonly double _intervalSeconds = double.IsFinite(intervalSeconds) && intervalSeconds >= 0
        ? intervalSeconds : throw new ArgumentException("Request interval must be finite and nonnegative.");
    private long _nextTicks;
    public async Task WaitAsync()
    {
        var now = Stopwatch.GetTimestamp();
        var remaining = _nextTicks - now;
        if (remaining > 0)
            await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
        _nextTicks = Stopwatch.GetTimestamp() + (long)(_intervalSeconds * Stopwatch.Frequency);
    }
}
