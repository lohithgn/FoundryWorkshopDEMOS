#:property PublishAot=false

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

var checks = new (string Name, Func<Task> Run)[]
{
    ("typed Decision API request and answer validation", TestTypedRequest),
    ("only exact subdomain errors are retried", TestRoutingRetry),
    ("throttling honors bounded retry headers", TestThrottleRetry),
    ("service errors are not accepted as answers", TestErrors),
    ("invalid settings fail before authentication", TestCli)
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

static async Task TestTypedRequest()
{
    var handler = new FakeHandler(request =>
    {
        Equal("/providers/microsoft/v1/systemone", request.RequestUri!.AbsolutePath);
        var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
        Equal("decision", body["model"]!.ToString());
        Equal("choice", body["questions"]!["team"]!["type"]!.ToString());
        Contains("billing", body["questions"]!["team"]!["criteria"]!.ToJsonString());
        return Json(HttpStatusCode.OK, """{"answers":{"team":{"type":"choice","choice":"billing"}}}""");
    });
    using var client = CreateClient(handler);
    Equal("billing", await ClassifyAsync(client, "Refund please", _ => Task.CompletedTask));
    Equal(1, handler.Calls);
    foreach (var payload in new[]
    {
        "{}",
        """{"answers":{"team":{"type":"choice","choice":"sales"}}}""",
        """{"answers":{"team":{"type":"score","choice":"billing"}}}"""
    })
    {
        using var invalid = CreateClient(new FakeHandler(_ => Json(HttpStatusCode.OK, payload)));
        await ThrowsAsync<InvalidOperationException>(() => ClassifyAsync(invalid, "Refund please", _ => Task.CompletedTask));
    }
}

static async Task TestRoutingRetry()
{
    var count = 0;
    var handler = new FakeHandler(_ => ++count == 1
        ? Json(HttpStatusCode.NotFound, """{"error":{"code":"ResourceNotFound","message":"Subdomain does not map to a resource."}}""", ("apim-request-id", "routing-request"))
        : Json(HttpStatusCode.OK, """{"answers":{"team":{"type":"choice","choice":"billing"}}}"""));
    var delays = new List<TimeSpan>();
    using var client = CreateClient(handler);
    Equal("billing", await ClassifyAsync(client, "Refund please", delay => { delays.Add(delay); return Task.CompletedTask; }));
    Equal(2, handler.Calls);
    EqualSequence(new[] { TimeSpan.FromSeconds(1) }, delays);

    foreach (var error in new[]
    {
        """{"error":{"code":"DeploymentNotFound","message":"Missing deployment"}}""",
        """{"error":{"code":"ResourceNotFound","message":"Wrong API path"}}""",
        """{"error":{"code":"ResourceNotFound","message":null}}""",
        """{"error":"not found"}""", "[]", "{}"
    })
    {
        var noRetry = new FakeHandler(_ => Json(HttpStatusCode.NotFound, error));
        using var otherClient = CreateClient(noRetry);
        await ThrowsAsync<HttpRequestException>(() => ClassifyAsync(otherClient, "Refund please", _ => Task.CompletedTask));
        Equal(1, noRetry.Calls);
    }
    var nonJson = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not found") });
    using var nonJsonClient = CreateClient(nonJson);
    await ThrowsAsync<HttpRequestException>(() => ClassifyAsync(nonJsonClient, "Refund please", _ => Task.CompletedTask));
    Equal(1, nonJson.Calls);
}

static async Task TestThrottleRetry()
{
    foreach (var (header, expected) in new[]
    {
        (("retry-after-ms", "2500"), TimeSpan.FromSeconds(2.5)),
        (("Retry-After", "3"), TimeSpan.FromSeconds(3))
    })
    {
        var count = 0;
        var handler = new FakeHandler(_ => ++count == 1
            ? Json(HttpStatusCode.TooManyRequests, "", header)
            : Json(HttpStatusCode.OK, """{"answers":{"team":{"type":"choice","choice":"billing"}}}"""));
        var delays = new List<TimeSpan>();
        using var client = CreateClient(handler);
        Equal("billing", await ClassifyAsync(client, "Refund please", delay => { delays.Add(delay); return Task.CompletedTask; }));
        EqualSequence(new[] { expected }, delays);
    }
    foreach (var (header, expectedCalls) in new[] { (("retry-after-ms", "1"), 3), (("none", ""), 1), (("Retry-After", "120"), 1) })
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.TooManyRequests, "", header));
        using var client = CreateClient(handler);
        await ThrowsAsync<HttpRequestException>(() => ClassifyAsync(client, "Refund please", _ => Task.CompletedTask));
        Equal(expectedCalls, handler.Calls);
    }
}

static async Task TestErrors()
{
    var handler = new FakeHandler(_ => Json(HttpStatusCode.Forbidden, """{"error":"denied"}""", ("apim-request-id", "fixture-id")));
    using var client = CreateClient(handler);
    var error = await ThrowsAsync<HttpRequestException>(() => ClassifyAsync(client, "Refund please", _ => Task.CompletedTask));
    Contains("fixture-id", error.Message);
    Contains("Deployment: decision", error.Message);
    Contains("denied", error.Message);
    Equal(HttpStatusCode.Forbidden, error.StatusCode);
}

static Task TestCli()
{
    foreach (var arguments in new[]
    {
        Array.Empty<string>(),
        new[] { "--endpoint", "http://example" },
        new[] { "--endpoint", "https://example", "--ticket", " " },
        new[] { "--endpoint", "https://example", "--deployment", " " }
    })
    {
        var result = RunApp(arguments);
        True(result.ExitCode != 0);
    }
    return Task.CompletedTask;
}

static async Task<string> ClassifyAsync(HttpClient client, string ticket, Func<TimeSpan, Task> delay)
{
    var request = new JsonObject
    {
        ["model"] = "decision", ["state"] = ticket,
        ["questions"] = new JsonObject { ["team"] = new JsonObject
        {
            ["type"] = "choice", ["instructions"] = "Which team should handle this support ticket?",
            ["criteria"] = new JsonObject { ["billing"] = "Charges, invoices, and refunds", ["technical"] = "Software errors, bugs, and outages", ["account"] = "Sign-in, password, and account access" }
        } }
    };
    HttpResponseMessage response = null!;
    for (var attempt = 0; attempt < 3; attempt++)
    {
        response = await client.PostAsync("providers/microsoft/v1/systemone", new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        if (response.StatusCode == HttpStatusCode.NotFound && ExactRoutingError(await response.Content.ReadAsStringAsync()))
        {
            if (attempt == 2) break;
            response.Dispose(); await delay(TimeSpan.FromSeconds(1 << attempt)); continue;
        }
        if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 2 || !GetDelay(response, out var retryDelay)) break;
        response.Dispose(); await delay(retryDelay);
    }
    using (response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}\nDeployment: decision; request ID: {RequestId(response)}; service response: {text}", null, response.StatusCode);
        var payload = JsonNode.Parse(text)!;
        var answer = payload["answers"]?["team"]!;
        var choice = answer?["choice"]?.ToString();
        if (answer?["type"]?.ToString() != "choice" || choice is not ("billing" or "technical" or "account"))
            throw new InvalidOperationException($"Decision API returned an invalid team answer: {payload}");
        return choice;
    }
}

static bool ExactRoutingError(string text)
{
    try
    {
        using var json = JsonDocument.Parse(text);
        var error = json.RootElement.GetProperty("error");
        return error.GetProperty("code").GetString() == "ResourceNotFound" &&
            error.GetProperty("message").GetString() == "Subdomain does not map to a resource.";
    }
    catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
}

static bool GetDelay(HttpResponseMessage response, out TimeSpan delay)
{
    delay = default;
    if (response.Headers.TryGetValues("retry-after-ms", out var values))
    {
        if (!double.TryParse(values.FirstOrDefault(), System.Globalization.CultureInfo.InvariantCulture, out var ms) || ms is < 0 or > 65000) return false;
        delay = TimeSpan.FromMilliseconds(ms); return true;
    }
    if (response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero && delta <= TimeSpan.FromSeconds(65))
    {
        delay = delta; return true;
    }
    return false;
}

static string RequestId(HttpResponseMessage response) => response.Headers.TryGetValues("apim-request-id", out var values) ? values.FirstOrDefault() ?? "(not supplied)" : "(not supplied)";
static HttpClient CreateClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://example/") };
static HttpResponseMessage Json(HttpStatusCode status, string body, params (string Key, string Value)[] headers)
{
    var response = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    foreach (var (key, value) in headers) response.Headers.TryAddWithoutValidation(key, value);
    return response;
}
static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T error) { return error; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
static (int ExitCode, string Output) RunApp(string[] arguments)
{
    var lab = Path.Combine(Environment.CurrentDirectory, "5-decision-model");
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
static void EqualSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]."); }
static void Contains(string expected, string actual) { if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected output to contain: {expected}"); }
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(respond(request));
    }
}
