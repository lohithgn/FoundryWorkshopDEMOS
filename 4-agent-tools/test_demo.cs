#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

var checks = new (string Name, Action Run)[]
{
    ("OpenAPI contract and anonymous toolbox registration", TestOpenApi),
    ("tool evidence is required and failures are visible", TestToolEvidence),
    ("attendee bounds fail before service calls", TestCli),
    ("hosted JavaScript function enforces price and input bounds", TestFunction),
    ("Bicep hosting remains keyless and dedicated", TestInfrastructure)
};
var failures = new List<string>();
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures.Add(name); Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
if (failures.Count > 0) { Console.Error.WriteLine($"Failed: {string.Join(", ", failures)}"); return 1; }
Console.WriteLine($"PASS all {checks.Length} offline regression checks (optional Node/Bicep checks skip when unavailable).");
return 0;

static void TestOpenApi()
{
    var spec = JsonNode.Parse(File.ReadAllText(Path.Combine(LabDirectory(), "openapi.json")))!.AsObject();
    var operation = spec["paths"]!["/workshop-price"]!["get"]!;
    Equal("getWorkshopPrice", operation["operationId"]!.ToString());
    Equal("999", operation["parameters"]![0]!["schema"]!["maximum"]!.ToString());
    True(operation["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["required"]!.AsArray()
        .Any(value => value!.ToString() == "source"));
    var toolbox = new JsonObject
    {
        ["description"] = "Read-only workshop pricing hosted in Azure Functions.",
        ["tools"] = new JsonArray(new JsonObject
        {
            ["type"] = "openapi",
            ["openapi"] = new JsonObject
            {
                ["name"] = "workshop", ["spec"] = spec.DeepClone(),
                ["auth"] = new JsonObject { ["type"] = "anonymous" }
            }
        })
    };
    Equal("openapi", toolbox["tools"]![0]!["type"]!.ToString());
    Equal("anonymous", toolbox["tools"]![0]!["openapi"]!["auth"]!["type"]!.ToString());
}

static void TestToolEvidence()
{
    ValidateToolResponse("""{"attendees":12,"total":300,"currency":"USD","source":"workshop-price-tool-v1"}""",
        "The workshop costs USD 300 for 12 attendees.");
    Throws<InvalidOperationException>(() => ValidateToolResponse(null, "answer"));
    Throws<InvalidOperationException>(() => ValidateToolResponse("""{"error":"failed"}""", "answer"));
    Throws<InvalidOperationException>(() => ValidateToolResponse("""{"attendees":12,"total":300,"currency":"USD"}""", "answer"));
    Throws<HttpRequestException>(() => CheckStatus(500, "fixture service failure"));
}

static void TestCli()
{
    var invalid = RunApp("--project-endpoint", "https://project", "--tool-endpoint", "https://tool", "--attendees", "1000");
    True(invalid.ExitCode != 0);
    Contains("--attendees must be between 1 and 999", invalid.Output);
    var missing = RunApp();
    True(missing.ExitCode != 0);
    Contains("FOUNDRY_PROJECT_ENDPOINT", missing.Output);
    var empty = RunApp("--project-endpoint", "https://project", "--tool-endpoint", "https://tool", "--deployment", " ");
    True(empty.ExitCode != 0);
    Contains("--deployment must not be empty", empty.Output);
}

static void TestFunction()
{
    if (!CanRun("node")) return;
    const string script = """
        const assert = require("node:assert/strict");
        const price = require("./tools/workshop-price.js");
        (async () => {
          for (const attendees of ["1", "12", "999"]) {
            const ctx = {};
            await price(ctx, {query: {attendees}});
            assert.equal(ctx.res.status, 200);
            assert.equal(ctx.res.body.total, Number(attendees) * 25);
            assert.equal(ctx.res.body.source, "workshop-price-tool-v1");
          }
          for (const attendees of [undefined, "", "0", "-1", "1000", "1.5", "abc", " 12", "01"]) {
            let logged = false;
            const ctx = {log: {warn: () => { logged = true; }}};
            await price(ctx, {query: {attendees}});
            assert.equal(ctx.res.status, 400);
            assert.ok(ctx.res.body.error);
            assert.ok(logged);
          }
        })().catch(error => { console.error(error); process.exitCode = 1; });
        """;
    var result = Run("node", ["-e", script], LabDirectory());
    Equal(0, result.ExitCode);
}

static void TestInfrastructure()
{
    if (!CanRun("az")) return;
    var result = Run("az", ["bicep", "build", "--file", "infra/main.bicep", "--stdout"], LabDirectory());
    Equal(0, result.ExitCode);
    using var document = JsonDocument.Parse(result.Output);
    var template = document.RootElement;
    var resources = template.GetProperty("resources").EnumerateArray().ToArray();
    var plan = resources.Single(item => item.GetProperty("type").GetString() == "Microsoft.Web/serverfarms");
    var app = resources.Single(item => item.GetProperty("type").GetString() == "Microsoft.Web/sites");
    var storage = resources.Single(item => item.GetProperty("type").GetString() == "Microsoft.Storage/storageAccounts");
    Equal("P0v3", plan.GetProperty("sku").GetProperty("name").GetString());
    True(app.GetProperty("identity").GetProperty("type").GetString() == "SystemAssigned");
    True(storage.GetProperty("properties").GetProperty("allowSharedKeyAccess").GetBoolean() == false);
    var settings = app.GetProperty("properties").GetProperty("siteConfig").GetProperty("appSettings").EnumerateArray()
        .ToDictionary(item => item.GetProperty("name").GetString()!, item => item.GetProperty("value").GetString());
    Equal("managedidentity", settings["AzureWebJobsStorage__credential"]);
    True(!settings.ContainsKey("AzureWebJobsStorage") && !result.Output.Contains("listKeys(", StringComparison.Ordinal));
}

static void ValidateToolResponse(string? toolResult, string answer)
{
    if (string.IsNullOrWhiteSpace(toolResult) || string.IsNullOrWhiteSpace(answer))
        throw new InvalidOperationException("Expected a real tool result and an agent answer.");
    using var result = JsonDocument.Parse(toolResult);
    if (result.RootElement.TryGetProperty("error", out _))
        throw new InvalidOperationException($"Tool call failed: {toolResult}");
    if (!toolResult.Contains("workshop-price-tool-v1", StringComparison.Ordinal))
        throw new InvalidOperationException("The tool did not return the deployed pricing function's source marker.");
}

static void CheckStatus(int status, string body)
{
    if (status is < 200 or >= 300) throw new HttpRequestException($"HTTP {status}: {body}", null, (System.Net.HttpStatusCode)status);
}

static string LabDirectory()
{
    var lab = Path.Combine(Environment.CurrentDirectory, "4-agent-tools");
    return File.Exists(Path.Combine(lab, "openapi.json")) ? lab : Environment.CurrentDirectory;
}

static (int ExitCode, string Output) RunApp(params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = LabDirectory(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.Environment.Remove("FOUNDRY_PROJECT_ENDPOINT"); start.Environment.Remove("WORKSHOP_TOOL_ENDPOINT");
    start.ArgumentList.Add("run"); start.ArgumentList.Add("--file"); start.ArgumentList.Add("demo.cs"); start.ArgumentList.Add("--");
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit(); return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
}

static (int ExitCode, string Output) Run(string file, string[] arguments, string directory)
{
    var start = new ProcessStartInfo(file) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit(); return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
}

static bool CanRun(string file) { try { using var p = Process.Start(new ProcessStartInfo(file, "--version") { RedirectStandardOutput = true, RedirectStandardError = true }); p?.WaitForExit(); return p?.ExitCode == 0; } catch { return false; } }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
static void Contains(string expected, string actual) { if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected output to contain: {expected}"); }
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
