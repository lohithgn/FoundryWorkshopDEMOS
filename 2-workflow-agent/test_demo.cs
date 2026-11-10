#:property PublishAot=false
#:package Microsoft.Agents.AI.Workflows@1.28.0

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;

var checks = new (string Name, Func<Task> Run)[]
{
    ("native sequential workflow preserves stage order and outputs", TestWorkflow),
    ("empty agent output and service errors fail", TestFailures),
    ("configuration fails before Azure authentication", TestCli),
    ("infrastructure keeps project dependent on model deployment", TestInfrastructure)
};
var failures = new List<string>();
foreach (var (name, run) in checks)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures.Add(name); Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
if (failures.Count > 0) { Console.Error.WriteLine($"Failed: {string.Join(", ", failures)}"); return 1; }
Console.WriteLine($"PASS all {checks.Length} offline regression checks (infrastructure compile is skipped without Azure CLI/Bicep).");
return 0;

static async Task TestWorkflow()
{
    var steps = new List<string>();
    var writer = ((Func<string, ValueTask<string>>)(input =>
    {
        steps.Add("writer");
        Equal("Invite people", input);
        return ValueTask.FromResult("Draft invitation");
    })).BindAsExecutor("writer");
    var editor = ((Func<string, ValueTask<string>>)(draft =>
    {
        steps.Add("editor");
        Equal("Draft invitation", draft);
        return ValueTask.FromResult("Edited invitation");
    })).BindAsExecutor("editor");
    WorkflowBuilder builder = new(writer);
    builder.AddEdge(writer, editor).WithOutputFrom(writer, editor);
    var workflow = builder.Build();
    await using var run = await InProcessExecution.RunAsync(workflow, "Invite people");
    var outputs = run.NewEvents.OfType<WorkflowOutputEvent>().Select(e => e.Data as string).ToArray();
    EqualSequence(new[] { "writer", "editor" }, steps);
    EqualSequence(new[] { "Draft invitation", "Edited invitation" }, outputs!);
}

static Task TestFailures()
{
    Throws<InvalidOperationException>(() => ShowStage("Writer draft", ""));
    Throws<HttpRequestException>(() => CheckResponse(503, "service unavailable"));
    return Task.CompletedTask;
}

static Task TestCli()
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
    return Task.CompletedTask;
}

static Task TestInfrastructure()
{
    if (!CanRun("az")) return Task.CompletedTask;
    var lab = Path.Combine(Environment.CurrentDirectory, "2-workflow-agent");
    if (!File.Exists(Path.Combine(lab, "infra/main.bicep"))) lab = Environment.CurrentDirectory;
    var result = Run("az", ["bicep", "build", "--file", "infra/main.bicep", "--stdout"], lab);
    Equal(0, result.ExitCode);
    using var document = JsonDocument.Parse(result.Output);
    var resources = document.RootElement.GetProperty("resources").EnumerateArray().ToArray();
    var project = resources.Single(item => item.GetProperty("type").GetString() == "Microsoft.CognitiveServices/accounts/projects");
    True(project.GetProperty("dependsOn").EnumerateArray().Any(item => item.GetString()!.Contains("Microsoft.CognitiveServices/accounts/deployments", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static void ShowStage(string label, string? output)
{
    if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException($"{label} returned no agent text.");
}

static void CheckResponse(int status, string body)
{
    if (status is < 200 or >= 300) throw new HttpRequestException($"Foundry inference failed: HTTP {status}: {body}", null, (System.Net.HttpStatusCode)status);
}

static (int ExitCode, string Output) RunApp(params string[] arguments)
{
    var lab = Path.Combine(Environment.CurrentDirectory, "2-workflow-agent");
    if (!File.Exists(Path.Combine(lab, "demo.cs"))) lab = Environment.CurrentDirectory;
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = lab, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
    };
    start.Environment.Remove("FOUNDRY_PROJECT_ENDPOINT");
    start.ArgumentList.Add("run"); start.ArgumentList.Add("--file"); start.ArgumentList.Add("demo.cs"); start.ArgumentList.Add("--");
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
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

static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
static void EqualSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]."); }
static void Contains(string expected, string actual) { if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected output to contain: {expected}"); }
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
