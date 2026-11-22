#:property PublishAot=false

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

var checks = new (string Name, Action Run)[]
{
    ("temperature distribution and greedy limit", TestTemperature),
    ("nucleus cumulative boundary", TestNucleus),
    ("invalid toy parameters", TestInvalidParameters),
    ("request parameter contracts", TestRequests),
    ("structured comparison validation", TestComparison),
    ("batch JSONL mapping and strict results", TestBatch),
    ("native batch state and infrastructure contract", TestBatchAndInfrastructure),
    ("concepts are offline and configuration fails", TestCli)
};
var failures = new List<string>();
foreach (var (name, run) in checks)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failures.Add($"{name}: {error.Message}");
        Console.Error.WriteLine($"FAIL {name}: {error}");
    }
}
if (failures.Count != 0)
{
    Console.Error.WriteLine($"{failures.Count} offline regression check(s) failed.");
    return 1;
}
Console.WriteLine($"PASS all {checks.Length} offline regression checks.");
return 0;

static void TestTemperature()
{
    var probabilities = new[] { 0.5, 0.3, 0.15, 0.05 };
    EqualSequence(new[] { 1d, 0, 0, 0 }, TemperatureDistribution(probabilities, 0));
    NearSequence(probabilities, TemperatureDistribution(probabilities, 1));
    True(TemperatureDistribution(probabilities, 0.2)[0] > 0.9);
    True(TemperatureDistribution(probabilities, 2)[0] < 0.5);
    NearScalar(1, TemperatureDistribution(probabilities, 2).Sum());
}

static void TestNucleus()
{
    EqualSequence(new[] { 1 }, Nucleus([0.05, 0.5, 0.3, 0.15], 0.5));
    EqualSequence(new[] { 1, 2 }, Nucleus([0.05, 0.5, 0.3, 0.15], 0.8));
    EqualSequence(new[] { 1, 2, 3, 0 }, Nucleus([0.05, 0.5, 0.3, 0.15], 1));
}

static void TestInvalidParameters()
{
    foreach (var probabilities in new[] { Array.Empty<double>(), [0d, 1], [-0.5, 1.5], [double.NaN], [0.3, 0.4] })
        Throws<ArgumentException>(() => TemperatureDistribution(probabilities, 1));
    foreach (var temperature in new[] { -1d, double.NaN, double.PositiveInfinity })
        Throws<ArgumentException>(() => TemperatureDistribution([0.5, 0.5], temperature));
    foreach (var topP in new[] { 0d, -1, 1.1, double.NaN })
        Throws<ArgumentException>(() => Nucleus([0.5, 0.5], topP));
}

static void TestRequests()
{
    var chat = ChatRequest("chat-alias", "sample", 0.7, 0.8);
    Equal("chat-alias", chat["model"]!.ToString());
    Equal("sample", chat["messages"]![0]!["content"]!.ToString());
    Equal("0.7", chat["temperature"]!.ToString());
    Equal("0.8", chat["top_p"]!.ToString());
    var reasoning = ReasoningRequest("reasoning-alias", "high");
    Equal("reasoning-alias", reasoning["model"]!.ToString());
    Equal("high", reasoning["reasoning_effort"]!.ToString());
    Equal("4096", reasoning["max_completion_tokens"]!.ToString());
    True(reasoning["temperature"] is null && reasoning["top_p"] is null && reasoning["max_tokens"] is null);
    var schema = reasoning["response_format"]!["json_schema"]!["schema"]!;
    Equal("minutes", schema["required"]![0]!.ToString());
    Equal("false", schema["additionalProperties"]!.ToString());
    Equal("/v1/chat/completions", BatchRows("batch-alias")[0]!["url"]!.ToString());
}

static void TestComparison()
{
    Equal(11, ComparisonMinutes("""{"minutes":11}"""));
    foreach (var answer in new[] { "{", "11", "{}", """{"minutes":"11"}""", """{"minutes":true}""", """{"minutes":-1}""", """{"minutes":11,"extra":1}""" })
        Throws<InvalidOperationException>(() => ComparisonMinutes(answer));
}

static void TestBatch()
{
    var rows = OutputRows();
    var shuffled = string.Join("\n", rows.Reverse().Select(row => row.ToJsonString()));
    var ordered = ValidateBatchResults(shuffled);
    Equal("task-0", ordered[0]!["custom_id"]!.ToString());
    Equal(3, ordered.Count);
    foreach (var rowsToReject in new[]
    {
        Array.Empty<JsonObject>(),
        rows.Take(2).ToArray(),
        [rows[0]!, rows[0]!, rows[2]!]
    })
        Throws<InvalidOperationException>(() => ValidateBatchResults(string.Join("\n", rowsToReject.Select(row => row.ToJsonString()))));

    foreach (var badRow in new[]
    {
        new JsonObject { ["custom_id"] = "foreign" },
        new JsonObject { ["custom_id"] = "task-0", ["error"] = new JsonObject { ["code"] = "BadRequest" } },
        new JsonObject { ["custom_id"] = "task-0", ["response"] = new JsonObject { ["status_code"] = 429 } },
        Row("task-0", 200, null, "stop"),
        Row("task-0", 200, "partial", "length"),
        Row("task-0", 200, "", "stop")
    })
    {
        var fixture = OutputRows();
        fixture[0] = badRow;
        Throws<InvalidOperationException>(() => ValidateBatchResults(string.Join("\n", fixture.Select(row => row.ToJsonString()))));
    }
}

static void TestBatchAndInfrastructure()
{
    var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var statePath = Path.Combine(directory, "batch-state.json");
        var state = new JsonObject
        {
            ["endpoint"] = "https://example.org", ["deployment"] = "batch-alias",
            ["batch_id"] = "batch-123", ["input_file_id"] = "input-123"
        };
        File.WriteAllText(statePath, state.ToJsonString());
        using var persisted = JsonDocument.Parse(File.ReadAllText(statePath));
        Equal("batch-123", persisted.RootElement.GetProperty("batch_id").GetString());
        Equal("input-123", persisted.RootElement.GetProperty("input_file_id").GetString());
        Equal(false, ShouldSubmitNewBatch(statePath, "https://example.org", "batch-alias", null));
        Throws<InvalidOperationException>(() => ShouldSubmitNewBatch(statePath, "https://other.org", "batch-alias", null));
        Equal(false, ShouldSubmitNewBatch(statePath, "https://other.org", "other-alias", "explicit-id"));
        Throws<InvalidOperationException>(() => RequireCompleted("in_progress", 0));
        RequireCompleted("completed", 0);
    }
    finally { Directory.Delete(directory, recursive: true); }

    if (!CanRun("az")) return;
    var lab = LabDirectory();
    var compiled = Run("az", ["bicep", "build", "--file", "infra/main.bicep", "--stdout"], lab);
    using var template = JsonDocument.Parse(compiled.Output);
    var reasoning = template.RootElement.GetProperty("resources").EnumerateArray()
        .Single(resource => resource.GetProperty("type").GetString() == "Microsoft.CognitiveServices/accounts/deployments" &&
            resource.GetProperty("name").GetString()!.Contains("basics-reasoning", StringComparison.Ordinal));
    Equal("gpt-5-mini", reasoning.GetProperty("properties").GetProperty("model").GetProperty("name").GetString());
    Equal("2025-08-07", reasoning.GetProperty("properties").GetProperty("model").GetProperty("version").GetString());
    Equal("GlobalStandard", reasoning.GetProperty("sku").GetProperty("name").GetString());
}

static void TestCli()
{
    var concepts = RunApp("--section", "concepts");
    Equal(0, concepts.ExitCode);
    Contains("LOCAL TOY EXAMPLE", concepts.Output);
    Contains("batch is asynchronous", concepts.Output);
    var invalid = RunApp("--section", "batch", "--endpoint", "https://example.org", "--wait-seconds", "-1");
    True(invalid.ExitCode != 0);
    Contains("--wait-seconds must be nonnegative", invalid.Output);
    var missing = RunApp("--section", "sampling");
    True(missing.ExitCode != 0);
    Contains("Set AZURE_OPENAI_ENDPOINT", missing.Output);
}

static bool ShouldSubmitNewBatch(string statePath, string endpoint, string deployment, string? explicitBatchId)
{
    if (explicitBatchId is not null) return false;
    if (!File.Exists(statePath)) return true;
    using var state = JsonDocument.Parse(File.ReadAllText(statePath));
    if (state.RootElement.GetProperty("endpoint").GetString() != endpoint ||
        state.RootElement.GetProperty("deployment").GetString() != deployment)
        throw new InvalidOperationException("Saved batch belongs to a different endpoint/deployment; use a different --state-dir.");
    return false;
}

static void RequireCompleted(string status, int remainingSeconds)
{
    if (status != "completed" && remainingSeconds <= 0)
        throw new InvalidOperationException($"Batch is still {status}; NOT a completed live test.");
}

static string LabDirectory()
{
    var lab = Path.Combine(Environment.CurrentDirectory, "0-model-basics");
    return File.Exists(Path.Combine(lab, "demo.cs")) ? lab : Environment.CurrentDirectory;
}

static bool CanRun(string file) { try { using var p = Process.Start(new ProcessStartInfo(file, "--version") { RedirectStandardOutput = true, RedirectStandardError = true }); p?.WaitForExit(); return p?.ExitCode == 0; } catch { return false; } }
static (int ExitCode, string Output) Run(string file, string[] arguments, string directory)
{
    var start = new ProcessStartInfo(file) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit(); return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
}

static (int ExitCode, string Output) RunApp(params string[] arguments)
{
    var labDirectory = LabDirectory();
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = labDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    start.Environment.Remove("AZURE_OPENAI_ENDPOINT");
    start.Environment.Remove("AZURE_OPENAI_DEPLOYMENT");
    start.Environment.Remove("AZURE_OPENAI_REASONING_DEPLOYMENT");
    start.Environment.Remove("AZURE_OPENAI_BATCH_DEPLOYMENT");
    start.ArgumentList.Add("run");
    start.ArgumentList.Add("--file");
    start.ArgumentList.Add("demo.cs");
    start.ArgumentList.Add("--");
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult());
}

static double[] TemperatureDistribution(IReadOnlyList<double> probabilities, double temperature)
{
    if (probabilities.Count == 0 || probabilities.Any(p => !double.IsFinite(p) || p <= 0))
        throw new ArgumentException("Toy probabilities must be finite and positive.");
    if (Math.Abs(probabilities.Sum() - 1) > 1e-9) throw new ArgumentException("Toy probabilities must sum to one.");
    if (!double.IsFinite(temperature) || temperature < 0) throw new ArgumentException("Temperature must be finite and nonnegative.");
    if (temperature == 0)
    {
        var winner = Enumerable.Range(0, probabilities.Count).MaxBy(i => probabilities[i]);
        return Enumerable.Range(0, probabilities.Count).Select(i => i == winner ? 1d : 0d).ToArray();
    }
    var logits = probabilities.Select(p => Math.Log(p) / temperature).ToArray();
    var maximum = logits.Max();
    var weights = logits.Select(value => Math.Exp(value - maximum)).ToArray();
    var total = weights.Sum();
    return weights.Select(weight => weight / total).ToArray();
}

static int[] Nucleus(IReadOnlyList<double> probabilities, double topP)
{
    _ = TemperatureDistribution(probabilities, 1);
    if (!double.IsFinite(topP) || topP is <= 0 or > 1) throw new ArgumentException("Toy top_p must be in (0, 1].");
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

static JsonObject ChatRequest(string model, string prompt, double temperature, double topP) => new()
{
    ["model"] = model, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
    ["max_tokens"] = 256, ["temperature"] = temperature, ["top_p"] = topP
};

static JsonObject ReasoningRequest(string model, string effort) => new()
{
    ["model"] = model, ["reasoning_effort"] = effort, ["max_completion_tokens"] = 4096,
    ["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject
    {
        ["strict"] = true, ["schema"] = new JsonObject
        {
            ["type"] = "object", ["properties"] = new JsonObject { ["minutes"] = new JsonObject { ["type"] = "integer" } },
            ["required"] = new JsonArray("minutes"), ["additionalProperties"] = false
        }
    } }
};

static JsonArray BatchRows(string model)
{
    var prompts = new[]
    {
        "Explain the temperature parameter in language-model text generation in one sentence for a beginner.",
        "Explain the top_p parameter in language-model text generation in one sentence for a beginner.",
        "Explain when asynchronous batch processing is useful in one sentence."
    };
    var rows = new JsonArray();
    for (var i = 0; i < prompts.Length; i++)
        rows.Add(new JsonObject { ["custom_id"] = $"task-{i}", ["method"] = "POST", ["url"] = "/v1/chat/completions",
            ["body"] = ChatRequest(model, prompts[i], 0, 1) });
    return rows;
}

static JsonArray ValidateBatchResults(string text)
{
    var expected = Enumerable.Range(0, 3).Select(i => $"task-{i}").ToHashSet(StringComparer.Ordinal);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var rows = new List<JsonObject>();
    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var row = JsonNode.Parse(line)!.AsObject();
        var id = row["custom_id"]?.GetValue<string>();
        if (id is null || !expected.Contains(id) || !seen.Add(id)) throw new InvalidOperationException("Unexpected or duplicate batch custom_id.");
        var response = row["response"]?.AsObject();
        if (row["error"] is not null && row["error"]!.ToJsonString() != "null" || response?["status_code"]?.GetValue<int>() != 200)
            throw new InvalidOperationException("Batch request failed.");
        var choice = response?["body"]?["choices"]?.AsArray()?.FirstOrDefault()?.AsObject();
        if (choice is null) throw new InvalidOperationException($"Missing choices for {id}.");
        if (choice["finish_reason"]?.ToString() != "stop" || string.IsNullOrWhiteSpace(choice["message"]?["content"]?.GetValue<string>()))
            throw new InvalidOperationException($"Incomplete/empty batch answer for {id}.");
        rows.Add(row);
    }
    if (!seen.SetEquals(expected)) throw new InvalidOperationException("Missing batch results.");
    var ordered = new JsonArray();
    foreach (var row in rows.OrderBy(r => r["custom_id"]!.ToString(), StringComparer.Ordinal)) ordered.Add(row);
    return ordered;
}

static int ComparisonMinutes(string answer)
{
    try
    {
        using var document = JsonDocument.Parse(answer);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("minutes", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var minutes) || minutes <= 0)
            throw new InvalidOperationException("Comparison answer must contain one positive integer minutes field.");
        return minutes;
    }
    catch (JsonException error)
    {
        throw new InvalidOperationException("Comparison answer is not valid JSON.", error);
    }
}

static JsonObject[] OutputRows() => Enumerable.Range(0, 3).Select(i => Row($"task-{i}", 200, $"Fixture {i}", "stop")).ToArray();

static JsonObject Row(string id, int status, string? content, string finish) => new()
{
    ["custom_id"] = id, ["error"] = null,
    ["response"] = new JsonObject { ["status_code"] = status, ["body"] = new JsonObject
    {
        ["model"] = "fixture-model", ["choices"] = content is null ? new JsonArray() : new JsonArray(new JsonObject
        {
            ["finish_reason"] = finish, ["message"] = new JsonObject { ["content"] = content }
        })
    } }
};

static void NearSequence(IEnumerable<double> expected, IEnumerable<double> actual)
{
    var left = expected.ToArray();
    var right = actual.ToArray();
    Equal(left.Length, right.Length);
    for (var i = 0; i < left.Length; i++) NearScalar(left[i], right[i]);
}
static void NearScalar(double expected, double actual) { if (Math.Abs(expected - actual) > 1e-9) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
static void EqualSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]."); }
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void Contains(string expected, string actual) { if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected output to contain: {expected}"); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
