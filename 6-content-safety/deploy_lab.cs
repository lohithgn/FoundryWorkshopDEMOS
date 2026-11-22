#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

try { return await MainAsync(args); }
catch (Exception error)
{
    Console.Error.WriteLine($"ERROR: {error.Message}");
    var statePath = Path.Combine(Environment.CurrentDirectory, ".azure", "lab-deployment.json");
    if (File.Exists(statePath))
        Console.Error.WriteLine($"Resources are retained; inspect {statePath} and run cleanup.cs.");
    return 1;
}

static async Task<int> MainAsync(string[] arguments)
{
    var subscriptionOption = "";
    var location = "eastus2";
    var prefix = "rg-foundry-student";
    var principalId = "";
    for (var i = 0; i < arguments.Length; i++)
    {
        string Value()
        {
            if (++i >= arguments.Length) throw new ArgumentException($"Missing value for {arguments[i - 1]}.");
            return arguments[i];
        }
        switch (arguments[i])
        {
            case "--help": case "-h": Usage(); return 0;
            case "--subscription": subscriptionOption = Value(); break;
            case "--location": location = Value(); break;
            case "--prefix": prefix = Value(); break;
            case "--principal-id": principalId = Value(); break;
            default: throw new ArgumentException($"Unknown argument: {arguments[i]}");
        }
    }
    if (!Regex.IsMatch(prefix, "^[a-zA-Z0-9_-]{1,50}$"))
        throw new ArgumentException("--prefix must contain 1-50 letters, numbers, underscores, or hyphens.");

    var lab = Environment.CurrentDirectory;
    var labName = Path.GetFileName(lab.TrimEnd(Path.DirectorySeparatorChar));
    var requiredInputs = labName == "4-agent-tools"
        ? new[] { "infra/main.bicep", "demo.cs", "test_demo.cs", "openapi.json" }
        : new[] { "infra/main.bicep", "demo.cs", "test_demo.cs" };
    foreach (var required in requiredInputs)
        if (!File.Exists(Path.Combine(lab, required))) throw new ArgumentException($"Missing lab input: {required}");
    var statePath = Path.Combine(lab, ".azure", "lab-deployment.json");
    var accountArgs = new List<string> { "account", "show", "-o", "json" };
    if (subscriptionOption.Length > 0) accountArgs.AddRange(["--subscription", subscriptionOption]);
    using var account = JsonDocument.Parse(Run("az", accountArgs, lab, capture: true));
    if (account.RootElement.GetProperty("user").GetProperty("type").GetString() != "user")
        throw new ArgumentException("Run az login as a user; the template assigns a User role.");
    var subscription = account.RootElement.GetProperty("id").GetString()
        ?? throw new InvalidOperationException("Azure CLI did not return a subscription ID.");
    var name = Regex.Replace(labName, "^\\d+-", "") + "-demo";
    var group = $"{prefix}-{name}";
    var scope = new[] { "--subscription", subscription };

    if (File.Exists(statePath))
    {
        using var prior = JsonDocument.Parse(File.ReadAllText(statePath));
        if ((prior.RootElement.GetProperty("subscription").GetString(), prior.RootElement.GetProperty("resourceGroup").GetString()) != (subscription, group) &&
            (!prior.RootElement.TryGetProperty("cleanupStatus", out var cleanup) || cleanup.GetString() != "deleted"))
            throw new ArgumentException("Recorded lab targets a different group/subscription; clean it up first.");
    }
    var exists = Run("az", ["group", "exists", "--name", group, .. scope], lab, capture: true).Trim();
    if (exists == "true")
    {
        var tagsText = Run("az", ["group", "show", "--name", group, .. scope, "--query", "tags", "-o", "json"], lab, capture: true);
        var tags = JsonNode.Parse(tagsText)?.AsObject();
        if (!File.Exists(statePath) || tags?["lab"]?.ToString() != labName || tags?["managedBy"]?.ToString() != "foundry-lab-script")
            throw new ArgumentException("Refusing to reuse an existing group not owned by this lab script.");
    }
    else if (exists != "false")
        throw new InvalidOperationException($"Unexpected group existence response: {exists}");
    if (string.IsNullOrWhiteSpace(principalId))
        principalId = Run("az", ["ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"], lab, capture: true).Trim();
    if (string.IsNullOrWhiteSpace(principalId))
        throw new ArgumentException("Cannot resolve user object ID; provide --principal-id.");

    Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
    var state = new JsonObject
    {
        ["lab"] = labName, ["subscription"] = subscription, ["resourceGroup"] = group,
        ["status"] = "running", ["stage"] = "local-tests", ["outputs"] = new JsonObject()
    };
    WriteState();
    try
    {
        Console.WriteLine($"Billable Azure deployment: {labName} -> {group} ({subscription})");
        var tests = RunResult("dotnet", ["run", "--file", "test_demo.cs"], lab);
        var testLog = Path.Combine(Path.GetDirectoryName(statePath)!, "lab-tests.log");
        await File.WriteAllTextAsync(testLog, tests.Output);
        Console.Write(tests.Output);
        if (tests.ExitCode != 0) throw new InvalidOperationException($"Offline .NET lab tests failed; see {testLog}.");

        state["stage"] = "deployment";
        WriteState();
        var template = Path.Combine(lab, "infra/main.bicep");
        using var compiled = JsonDocument.Parse(Run("az", ["bicep", "build", "--file", template, "--stdout"], lab, capture: true));
        Run("az", ["bicep", "lint", "--file", template], lab);
        var providers = compiled.RootElement.GetProperty("resources").EnumerateArray()
            .Select(resource => resource.GetProperty("type").GetString()!.Split('/')[0])
            .Where(provider => !provider.Equals("Microsoft.Authorization", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var provider in providers)
            Run("az", ["provider", "register", "--namespace", provider, "--wait", .. scope], lab);
        var tags = new[] { "managedBy=foundry-lab-script", $"lab={labName}", "SecurityControl=Ignore", "SecurityExemption=StorageAccountKeyAccess" };
        Run("az", ["group", "create", "--name", group, "--location", location, "--tags", .. tags, .. scope, "-o", "none"], lab);
        var outputText = Run("az", [
            "deployment", "group", "create", "--resource-group", group, "--name", name,
            .. scope, "--template-file", template, "--parameters",
            $"principalId={principalId}", $"location={location}", "--query", "properties.outputs", "-o", "json"
        ], lab, capture: true);
        state["outputs"] = JsonNode.Parse(outputText);
        state["stage"] = "live-demo";
        WriteState();
        await RunLiveAsync(lab, statePath, CreateEnvironment(state["outputs"]!.AsObject()));
        state["status"] = "passed";
        state["stage"] = "complete";
        Console.WriteLine($"PASS: {labName}. Resources retained. Results: {statePath}");
        return 0;
    }
    catch (Exception error)
    {
        state["status"] = "failed";
        state["error"] = error.Message;
        throw;
    }
    finally { WriteState(); }

    void WriteState() => File.WriteAllText(statePath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
}

static Dictionary<string, string> CreateEnvironment(JsonObject outputs)
{
    var values = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
        .Where(entry => entry.Key is string && entry.Value is string)
        .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);
    values.Remove("AZURE_OPENAI_DEPLOYMENT");
    AddRequired("endpoint", "AZURE_OPENAI_ENDPOINT");
    foreach (var (output, variable) in new[]
    {
        ("deployment", "AZURE_OPENAI_DEPLOYMENT"), ("reasoningDeployment", "AZURE_OPENAI_REASONING_DEPLOYMENT"),
        ("batchDeployment", "AZURE_OPENAI_BATCH_DEPLOYMENT"), ("embeddingDeployment", "AZURE_OPENAI_EMBEDDING_DEPLOYMENT"),
        ("projectEndpoint", "FOUNDRY_PROJECT_ENDPOINT"), ("toolEndpoint", "WORKSHOP_TOOL_ENDPOINT"),
        ("searchEndpoint", "AZURE_SEARCH_ENDPOINT"), ("agentIdentityId", "ENTRA_AGENT_ID"),
        ("contentSafetyEndpoint", "CONTENT_SAFETY_ENDPOINT"), ("deniedEndpoint", "WORKSHOP_DENIED_ENDPOINT"),
        ("alternateDeployment", "AZURE_OPENAI_ALTERNATE_DEPLOYMENT"), ("routerDeployment", "AZURE_OPENAI_ROUTER_DEPLOYMENT"),
        ("applicationInsightsConnectionString", "APPLICATIONINSIGHTS_CONNECTION_STRING"),
        ("projectResourceId", "FOUNDRY_PROJECT_RESOURCE_ID"), ("tenantId", "AZURE_TENANT_ID"),
        ("searchResourceId", "AZURE_SEARCH_RESOURCE_ID"), ("storageEndpoint", "AZURE_STORAGE_BLOB_ENDPOINT"),
        ("storageResourceId", "AZURE_STORAGE_RESOURCE_ID")
    }) AddOptional(output, variable);
    return values;

    void AddRequired(string output, string variable)
    {
        var value = outputs[output]?["value"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Bicep must return a nonempty '{output}' output.");
        values[variable] = value;
    }
    void AddOptional(string output, string variable)
    {
        if (!outputs.ContainsKey(output)) return;
        var value = outputs[output]?["value"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Bicep output '{output}' must be a nonempty string.");
        values[variable] = value;
    }
}

static async Task RunLiveAsync(string lab, string statePath, Dictionary<string, string> environment)
{
    var log = Path.Combine(Path.GetDirectoryName(statePath)!, "demo.log");
    for (var attempt = 0; attempt < 11; attempt++)
    {
        Console.WriteLine($"\nRunning live demo: {Path.GetFileName(lab)} (attempt {attempt + 1}/11)");
        var result = RunResult("dotnet", ["run", "--file", "demo.cs"], lab, environment);
        Console.Write(result.Output);
        await File.AppendAllTextAsync(log, $"\n--- Attempt {attempt + 1} ---\n{result.Output}");
        if (result.ExitCode == 0) return;
        var dns = (result.Output.Contains("HttpRequestException", StringComparison.Ordinal) ||
                   result.Output.Contains("SocketException", StringComparison.Ordinal)) &&
            new[] { "No such host is known", "Name or service not known", "Temporary failure in name resolution", "nodename nor servname provided" }
                .Any(result.Output.Contains);
        var propagation = result.Output.Contains("403", StringComparison.Ordinal) &&
                (result.Output.Contains("Forbidden", StringComparison.Ordinal) || result.Output.Contains("PermissionDenied", StringComparison.Ordinal)) ||
            result.Output.Contains("404", StringComparison.Ordinal) && result.Output.Contains("DeploymentNotFound", StringComparison.Ordinal);
        if ((!dns && !propagation) || attempt == 10)
            throw new InvalidOperationException(dns
                ? $"DNS resolution failed for live demo {Path.GetFileName(lab)} after {attempt + 1} attempts; see {log}."
                : $"Live demo {Path.GetFileName(lab)} failed; see {log}.");
        Console.WriteLine(dns ? "DNS lookup failed; endpoint or local DNS may not be ready. Retrying in 30 seconds."
            : "Azure access/deployment is not ready. Retrying in 30 seconds.");
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}

static string Run(string file, IEnumerable<string> arguments, string directory, bool capture = false)
{
    var result = RunResult(file, arguments, directory);
    if (result.ExitCode != 0)
        throw new InvalidOperationException($"{file} command failed ({result.ExitCode}): {result.Output}");
    if (capture) return result.StandardOutput;
    Console.Write(result.StandardOutput);
    Console.Error.Write(result.StandardError);
    return result.StandardOutput + result.StandardError;
}

static (int ExitCode, string Output, string StandardOutput, string StandardError) RunResult(
    string file, IEnumerable<string> arguments, string directory, Dictionary<string, string>? environment = null)
{
    var values = arguments.ToArray();
    Console.WriteLine($"\n> {file} {string.Join(" ", values)}");
    var start = new ProcessStartInfo(file) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var value in values) start.ArgumentList.Add(value);
    if (environment is not null)
    {
        start.Environment.Clear();
        foreach (var (key, value) in environment) start.Environment[key] = value;
    }
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    var standardOutput = stdout.GetAwaiter().GetResult();
    var standardError = stderr.GetAwaiter().GetResult();
    return (process.ExitCode, standardOutput + standardError, standardOutput, standardError);
}

static void Usage() => Console.WriteLine("Usage: dotnet run --file deploy_lab.cs -- [--subscription ID] [--location eastus2] [--prefix rg-foundry-student] [--principal-id ID]");
