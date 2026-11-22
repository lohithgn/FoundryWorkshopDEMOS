#:property PublishAot=false

using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

var checks = new (string Name, Func<Task> Run)[]
{
    ("severity threshold allows below and blocks at threshold", TestThreshold),
    ("requests full four-level category analysis", TestRequest),
    ("missing categories and invalid severities fail closed", TestInvalidResults),
    ("service errors propagate", TestServiceErrors),
    ("input validation fails before Azure authentication", TestCli)
};
var failures = new List<string>();
foreach (var (name, run) in checks)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures.Add(name); Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
if (failures.Count > 0) { Console.Error.WriteLine($"Failed: {string.Join(", ", failures)}"); return 1; }
Console.WriteLine($"PASS all {checks.Length} offline regression checks.");
return 0;

static Task TestThreshold()
{
    True(!Analyze(Result(0), "Hello", 2));
    True(!Analyze(Result(2), "Hello", 4));
    True(Analyze(Result(4), "Hello", 4));
    True(Analyze(Result(6), "Hello", 4));
    return Task.CompletedTask;
}

static async Task TestRequest()
{
    var handler = new FakeHandler(request =>
    {
        Equal("/contentsafety/text:analyze", request.RequestUri!.AbsolutePath);
        Equal("2024-09-01", request.RequestUri.Query.Contains("api-version=2024-09-01", StringComparison.Ordinal) ? "2024-09-01" : "");
        var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
        Equal("Hello", body["text"]!.ToString());
        Equal("FourSeverityLevels", body["outputType"]!.ToString());
        return Json(HttpStatusCode.OK, Result(0).ToJsonString());
    });
    using var client = CreateClient(handler);
    True(!await AnalyzeAsync(client, "Hello", 2));
    Equal(1, handler.Calls);
}

static Task TestInvalidResults()
{
    foreach (var result in new[]
    {
        new JsonObject { ["categoriesAnalysis"] = new JsonArray() },
        Result(null),
        Result(3),
        new JsonObject { ["categoriesAnalysis"] = new JsonArray(new JsonObject { ["category"] = "Hate", ["severity"] = 0 }) }
    })
        Throws<InvalidOperationException>(() => Analyze(result, "Hello", 2));
    return Task.CompletedTask;
}

static async Task TestServiceErrors()
{
    using var client = CreateClient(new FakeHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "service unavailable")));
    var error = await ThrowsAsync<HttpRequestException>(() => AnalyzeAsync(client, "Hello", 2));
    Contains("service unavailable", error.Message);
    Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
}

static Task TestCli()
{
    var missing = RunApp();
    True(missing.ExitCode != 0);
    Contains("AZURE_OPENAI_ENDPOINT", missing.Output);
    var invalid = RunApp("--endpoint", "http://example");
    True(invalid.ExitCode != 0);
    Contains("HTTPS URL", invalid.Output);
    var blank = RunApp("--endpoint", "https://example", "--text", " ");
    True(blank.ExitCode != 0);
    Contains("1-10000 characters", blank.Output);
    var longText = RunApp("--endpoint", "https://example", "--text", new string('a', 10001));
    True(longText.ExitCode != 0);
    Contains("1-10000 characters", longText.Output);
    var threshold = RunApp("--endpoint", "https://example", "--threshold", "3");
    True(threshold.ExitCode != 0);
    Contains("threshold must be 2, 4, or 6", threshold.Output);
    return Task.CompletedTask;
}

static bool Analyze(JsonObject result, string text, int threshold)
{
    var expected = new HashSet<string>(["Hate", "SelfHarm", "Sexual", "Violence"], StringComparer.Ordinal);
    var analyses = result["categoriesAnalysis"]?.AsArray();
    if (analyses is null || analyses.Count == 0 || !expected.SetEquals(analyses.Select(item => item?["category"]?.ToString() ?? "")))
        throw new InvalidOperationException("Content Safety did not return all four harm categories.");
    var blocked = false;
    foreach (var item in analyses)
    {
        var category = item!["category"]!.ToString();
        if (!int.TryParse(item["severity"]?.ToString(), out var severity) || severity is not (0 or 2 or 4 or 6))
            throw new InvalidOperationException($"Invalid severity for {category}: {item["severity"]}");
        blocked |= severity >= threshold;
    }
    return blocked;
}

static async Task<bool> AnalyzeAsync(HttpClient client, string text, int threshold)
{
    var request = new JsonObject { ["text"] = text, ["outputType"] = "FourSeverityLevels" };
    using var response = await client.PostAsync("contentsafety/text:analyze?api-version=2024-09-01",
        new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Content Safety request failed: HTTP {(int)response.StatusCode}: {body}", null, response.StatusCode);
    return Analyze(JsonNode.Parse(body)!.AsObject(), text, threshold);
}

static JsonObject Result(int? severity) => new()
{
    ["categoriesAnalysis"] = new JsonArray(new[] { "Hate", "SelfHarm", "Sexual", "Violence" }
        .Select(category => (JsonNode?)new JsonObject { ["category"] = category, ["severity"] = severity }).ToArray())
};

static HttpClient CreateClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://example/") };
static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
static (int ExitCode, string Output) RunApp(params string[] arguments)
{
    var lab = Path.Combine(Environment.CurrentDirectory, "6-content-safety");
    if (!File.Exists(Path.Combine(lab, "demo.cs"))) lab = Environment.CurrentDirectory;
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = lab, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.Environment.Remove("AZURE_OPENAI_ENDPOINT");
    start.ArgumentList.Add("run"); start.ArgumentList.Add("--file"); start.ArgumentList.Add("demo.cs"); start.ArgumentList.Add("--");
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit(); return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
}
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
static void Contains(string expected, string actual) { if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected output to contain: {expected}"); }
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(respond(request));
    }
}
