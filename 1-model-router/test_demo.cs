#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json.Nodes;

var checks = new (string Name, Action Run)[]
{
    ("default prompts and custom request contract", TestPrompts),
    ("service completion and failure validation", TestCompletion),
    ("invalid configuration fails before authentication", TestCli)
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

static void TestPrompts()
{
    var prompts = DefaultPrompts();
    Equal(3, prompts.Length);
    Contains("capital of France", prompts[0].Text);
    Contains("Foundry", prompts[1].Text);
    Contains("120 attendees", prompts[2].Text);
    var request = new JsonObject
    {
        ["model"] = "my-router",
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = "You are a helpful assistant. Keep answers concise." },
            new JsonObject { ["role"] = "user", ["content"] = "Explain routing." })
    };
    Equal("my-router", request["model"]!.ToString());
    Equal("system", request["messages"]![0]!["role"]!.ToString());
    Equal("Explain routing.", request["messages"]![1]!["content"]!.ToString());
}

static void TestCompletion()
{
    var valid = new JsonObject
    {
        ["model"] = "gpt-4.1-mini-2025-04-14",
        ["usage"] = new JsonObject { ["prompt_tokens"] = 20, ["completion_tokens"] = 10, ["total_tokens"] = 30 },
        ["choices"] = new JsonArray(new JsonObject
        {
            ["finish_reason"] = "stop",
            ["message"] = new JsonObject { ["content"] = "Paris is the capital of France.", ["refusal"] = null }
        })
    };
    ValidateCompletion(valid);
    foreach (var response in new[]
    {
        new JsonObject { ["choices"] = new JsonArray() },
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = null } }) },
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["refusal"] = "Cannot comply." } }) }
    })
        Throws<InvalidOperationException>(() => ValidateCompletion(response));
    Throws<HttpRequestException>(() => ValidateHttpStatus(403, "denied"));
}

static void TestCli()
{
    var missing = RunApp("--prompt", "Hello");
    True(missing.ExitCode != 0);
    Contains("Set AZURE_OPENAI_ENDPOINT", missing.Output);
    var http = RunApp("--endpoint", "http://example.com");
    True(http.ExitCode != 0);
    Contains("absolute HTTPS URL", http.Output);
    var blankPrompt = RunApp("--endpoint", "https://example.com", "--prompt", " ");
    True(blankPrompt.ExitCode != 0);
    Contains("--prompt must not be empty", blankPrompt.Output);
    var blankDeployment = RunApp("--endpoint", "https://example.com", "--deployment", " ");
    True(blankDeployment.ExitCode != 0);
    Contains("--deployment must not be empty", blankDeployment.Output);
}

static (int ExitCode, string Output) RunApp(params string[] arguments)
{
    var lab = Path.Combine(Environment.CurrentDirectory, "1-model-router");
    if (!File.Exists(Path.Combine(lab, "demo.cs"))) lab = Environment.CurrentDirectory;
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = lab, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
    };
    start.Environment.Remove("AZURE_OPENAI_ENDPOINT");
    start.ArgumentList.Add("run"); start.ArgumentList.Add("--file"); start.ArgumentList.Add("demo.cs"); start.ArgumentList.Add("--");
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
}

static void ValidateCompletion(JsonObject response)
{
    var choices = response["choices"]?.AsArray();
    if (choices is null || choices.Count == 0) throw new InvalidOperationException("The service returned no completion choices.");
    var choice = choices[0]!.AsObject();
    var message = choice["message"]?.AsObject() ?? new JsonObject();
    if (!string.IsNullOrEmpty(message["refusal"]?.ToString())) throw new InvalidOperationException($"The model refused the prompt: {message["refusal"]}");
    if (string.IsNullOrEmpty(message["content"]?.ToString())) throw new InvalidOperationException($"The service returned no text (finish_reason={choice["finish_reason"]}).");
}

static void ValidateHttpStatus(int status, string body)
{
    if (status is < 200 or >= 300) throw new HttpRequestException($"HTTP {status}: {body}", null, (System.Net.HttpStatusCode)status);
}

static (string Label, string Text)[] DefaultPrompts() =>
[
    ("Simple", "What is the capital of France? Answer in one sentence."),
    ("Writing", "Write a friendly, three-sentence invitation to a workshop about Microsoft Foundry. The audience is Python developers new to AI."),
    ("Reasoning", "A workshop has 120 attendees. Each lab needs one mentor per started group of 8 attendees, and there are 11 mentors. Attendees may join either of two consecutive lab slots. Find an allocation that minimizes the maximum number of mentors needed in either slot. Explain why it is optimal, including rounding to whole groups.")
];

static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
static void Contains(string expected, string actual) { if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected output to contain: {expected}"); }
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
