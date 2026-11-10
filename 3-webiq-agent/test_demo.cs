#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json.Nodes;

var checks = new (string Name, Action Run)[]
{
    ("requires completed web search and structured citations", TestGrounding),
    ("no-tool answer rejects hidden search and plain URLs", TestNoTool),
    ("debug output and native agent request contract", TestDiagnostics),
    ("invalid settings fail before authentication", TestCli)
};
var failures = new List<string>();
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures.Add(name); Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
if (failures.Count > 0) { Console.Error.WriteLine($"Failed: {string.Join(", ", failures)}"); return 1; }
Console.WriteLine($"PASS all {checks.Length} offline regression checks.");
return 0;

static void TestGrounding()
{
    var valid = Response(true, true);
    ValidateResponse(valid, true);
    foreach (var response in new[] { Response(false, true), Response(true, false), Response(true, true, "incomplete"), Response(true, true, "completed", "") })
        Throws<InvalidOperationException>(() => ValidateResponse(response, true));
    foreach (var status in new[] { "failed", "in_progress", "incomplete" })
        Throws<InvalidOperationException>(() => ValidateResponse(Response(true, true, searchStatus: status), true));
}

static void TestNoTool()
{
    ValidateResponse(Response(false, false), false);
    foreach (var answer in new[] { "I cannot browse the web.", "See https://learn.microsoft.com/azure/foundry/" })
    {
        var result = Response(false, false, text: answer);
        ValidateResponse(result, false);
        Contains(answer, ResponseText(result));
    }
    Throws<InvalidOperationException>(() => ValidateResponse(Response(false, false, "incomplete"), false));
    Throws<InvalidOperationException>(() => ValidateResponse(Response(true, false), false));
    Throws<InvalidOperationException>(() => ValidateResponse(Response(false, true), false));
}

static void TestDiagnostics()
{
    var web = new JsonObject
    {
        ["definition"] = new JsonObject
        {
            ["kind"] = "prompt", ["model"] = "gpt-5-mini",
            ["instructions"] = "Search the public web before answering. Be concise and cite sources.",
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "web_search" })
        }
    };
    var none = (JsonObject)web.DeepClone();
    none["definition"]!["tools"] = new JsonArray();
    Equal("web_search", web["definition"]!["tools"]![0]!["type"]!.ToString());
    Equal(0, none["definition"]!["tools"]!.AsArray().Count);
    var requestA = new JsonObject { ["input"] = "same prompt", ["tool_choice"] = "required",
        ["agent_reference"] = new JsonObject { ["name"] = "agent", ["version"] = "1" } };
    var requestB = new JsonObject { ["input"] = "same prompt", ["tool_choice"] = "none",
        ["agent_reference"] = new JsonObject { ["name"] = "agent", ["version"] = "2" } };
    Equal(requestA["input"]!.ToString(), requestB["input"]!.ToString());
    True(requestA["previous_response_id"] is null && requestB["previous_response_id"] is null);
}

static void TestCli()
{
    var missing = RunApp();
    True(missing.ExitCode != 0);
    Contains("FOUNDRY_PROJECT_ENDPOINT", missing.Output);
    var invalid = RunApp("--project-endpoint", "http://example");
    True(invalid.ExitCode != 0);
    Contains("FOUNDRY_PROJECT_ENDPOINT", invalid.Output);
    var empty = RunApp("--project-endpoint", "https://example", "--prompt", " ");
    True(empty.ExitCode != 0);
    Contains("must not be empty", empty.Output);
}

static JsonObject Response(bool search, bool citation, string status = "completed", string text = "Foundry routes prompts to models.", string searchStatus = "completed")
{
    var output = new JsonArray();
    if (search) output.Add(new JsonObject { ["type"] = "web_search_call", ["status"] = searchStatus });
    output.Add(new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject
    {
        ["type"] = "output_text", ["text"] = text,
        ["annotations"] = citation ? new JsonArray(new JsonObject { ["type"] = "url_citation", ["title"] = "Microsoft Learn", ["url"] = "https://learn.microsoft.com/azure/foundry/" }) : new JsonArray()
    }) });
    return new JsonObject { ["status"] = status, ["output"] = output, ["error"] = null };
}

static void ValidateResponse(JsonObject response, bool expectSearch)
{
    var output = response["output"]!.AsArray();
    var searches = output.Where(item => item?["type"]?.ToString() == "web_search_call").ToArray();
    var citations = output.Where(item => item?["type"]?.ToString() == "message")
        .SelectMany(item => item?["content"]?.AsArray() ?? [])
        .SelectMany(content => content?["annotations"]?.AsArray() ?? [])
        .Where(annotation => annotation?["type"]?.ToString() == "url_citation").ToArray();
    var text = ResponseText(response);
    if (response["status"]?.ToString() != "completed" || string.IsNullOrWhiteSpace(text))
        throw new InvalidOperationException($"Agent did not complete with text: {response["status"]}");
    if (!expectSearch)
    {
        if (searches.Length > 0 || citations.Length > 0) throw new InvalidOperationException("Unexpected web-search evidence.");
        return;
    }
    if (searches.Length == 0 || citations.Length == 0) throw new InvalidOperationException("Grounding evidence missing.");
    if (searches.Any(search => search?["status"]?.ToString() != "completed"))
        throw new InvalidOperationException($"A web-search call failed: {string.Join(",", searches.Select(s => s?["status"]))}");
}

static string ResponseText(JsonObject response) =>
    string.Join("", response["output"]!.AsArray().Where(item => item?["type"]?.ToString() == "message")
        .SelectMany(item => item?["content"]?.AsArray() ?? [])
        .Where(item => item?["type"]?.ToString() == "output_text").Select(item => item?["text"]?.ToString()));

static (int ExitCode, string Output) RunApp(params string[] arguments)
{
    var lab = Path.Combine(Environment.CurrentDirectory, "3-webiq-agent");
    if (!File.Exists(Path.Combine(lab, "demo.cs"))) lab = Environment.CurrentDirectory;
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = lab, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.Environment.Remove("FOUNDRY_PROJECT_ENDPOINT");
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
