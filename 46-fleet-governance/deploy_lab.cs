#:property TargetFramework=net10.0
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var lab = Environment.CurrentDirectory;
if (!File.Exists(Path.Combine(lab, "infra", "main.bicep")))
    throw new DirectoryNotFoundException("Run this file from 46-fleet-governance.");
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Deploy and verify only this recorded, owned synthetic two-project fleet.\nUsage: dotnet run --file deploy_lab.cs -- [--subscription ID] [--principal-id ID] [--prefix PREFIX] [--location LOCATION] [--infra-only]");
    return;
}
var state = Path.Combine(lab, ".azure");
var statePath = Path.Combine(state, "lab-deployment.json");
Dictionary<string, string> options;
try { options = Parse(args); }
catch (ArgumentException error)
{
    Console.Error.WriteLine($"ERROR: {error.Message}");
    Environment.ExitCode = 2;
    return;
}
if (options.GetValueOrDefault("--prefix") is { } prefix && !Regex.IsMatch(prefix, "^[a-zA-Z0-9_-]{1,50}$"))
{
    Console.Error.WriteLine("ERROR: --prefix must contain 1-50 letters, numbers, underscores, or hyphens.");
    Environment.ExitCode = 2;
    return;
}
var accountArgs = new List<string> { "account", "show", "-o", "json" };
if (options.TryGetValue("--subscription", out var requestedSubscription))
    accountArgs.AddRange(["--subscription", requestedSubscription]);
var account = JsonNode.Parse(await RunAz(accountArgs, capture: true))!.AsObject();
if (account["user"]?["type"]?.ToString() != "user")
    throw new ArgumentException("Use az login as a user; the template assigns User RBAC.");
var subscription = account["id"]?.ToString() ?? throw new InvalidDataException("Azure CLI did not return a subscription ID.");
var scope = new[] { "--subscription", subscription };
var previous = File.Exists(statePath) ? JsonNode.Parse(await File.ReadAllTextAsync(statePath))!.AsObject() : null;
var name = Regex.Replace(Path.GetFileName(lab), "^\\d+-", "") + "-demo";
var group = previous is not null && !options.ContainsKey("--prefix")
    ? previous["resourceGroup"]?.ToString() ?? throw new InvalidDataException("Recorded lab resource group is missing.")
    : $"{options.GetValueOrDefault("--prefix", "rg-foundry")}-{name}";
if (!Regex.IsMatch(group, "^[a-zA-Z0-9_-]{1,90}$") || !group.EndsWith("-" + name, StringComparison.Ordinal))
    throw new ArgumentException("Recorded group is not a valid group for this lab.");
if (previous is not null && (previous["subscription"]?.ToString() != subscription || previous["resourceGroup"]?.ToString() != group))
    throw new ArgumentException("Recorded lab targets a different subscription/group; do not overwrite its state.");
var exists = (await RunAz(["group", "exists", "--name", group, .. scope], capture: true)).Trim();
if (exists == "true")
{
    var tags = JsonNode.Parse(await RunAz(["group", "show", "--name", group, .. scope, "--query", "tags", "-o", "json"], capture: true))?.AsObject();
    if (previous is null || tags?["managedBy"]?.ToString() != "foundry-lab-script" || tags["lab"]?.ToString() != Path.GetFileName(lab))
        throw new ArgumentException("Refusing to reuse a group not recorded and owned by this lab.");
}
else if (exists != "false")
    throw new InvalidDataException($"Unexpected group existence response: {exists}");
var principal = options.GetValueOrDefault("--principal-id");
if (string.IsNullOrWhiteSpace(principal))
    principal = (await RunAz(["ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"], capture: true)).Trim();
if (string.IsNullOrWhiteSpace(principal))
    throw new ArgumentException("Provide --principal-id or sign in as an identifiable user.");
Directory.CreateDirectory(state);
if (!options.ContainsKey("--infra-only"))
    await RunProcess("dotnet", ["run", "--file", "test_demo.cs"], lab);
var template = Path.Combine(lab, "infra", "main.bicep");
var compiled = JsonNode.Parse(await RunAz(["bicep", "build", "--file", template, "--stdout"], capture: true))!.AsObject();
var providers = (compiled["resources"]?.AsArray() ?? new JsonArray())
    .Select(resource => resource?["type"]?.ToString() ?? "")
    .Where(type => type.Length > 0 && !type.StartsWith("Microsoft.Authorization/", StringComparison.Ordinal))
    .Select(type => type.Split('/')[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
foreach (var provider in providers)
    await RunAz(["provider", "register", "--namespace", provider, "--wait", .. scope]);
var tagsToApply = new[] { "managedBy=foundry-lab-script", $"lab={Path.GetFileName(lab)}",
    "SecurityControl=Ignore", "SecurityExemption=StorageAccountKeyAccess" };
await RunAz(["group", "create", "--name", group, "--location", options.GetValueOrDefault("--location", "eastus2"),
    "--tags", .. tagsToApply, .. scope, "-o", "none"]);
var result = new JsonObject { ["subscription"] = subscription, ["resourceGroup"] = group, ["outputs"] = new JsonObject() };
await SaveDeployment(result);
var deployment = JsonNode.Parse(await RunAz(["deployment", "group", "create", "--resource-group", group,
    "--name", name, .. scope, "--template-file", template, "--parameters",
    $"principalId={principal}", $"location={options.GetValueOrDefault("--location", "eastus2")}",
    "--query", "properties.outputs", "-o", "json"], capture: true))!.AsObject();
result["outputs"] = deployment.DeepClone();
await SaveDeployment(result);
await File.WriteAllTextAsync(Path.Combine(state, "deployment.json"),
    result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
if (!options.ContainsKey("--infra-only"))
    await RunLive();

async Task SaveDeployment(JsonObject document)
{
    await File.WriteAllTextAsync(statePath, document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
}

async Task RunLive()
{
    var log = Path.Combine(state, "demo.log");
    for (var attempt = 0; attempt < 11; attempt++)
    {
        Console.WriteLine($"\nRunning live demo: {Path.GetFileName(lab)} (attempt {attempt + 1}/11)");
        var (exitCode, output) = await RunProcess("dotnet", ["run", "--file", "demo.cs", "--", "--timeout", "600"], lab, capture: true, throwOnError: false);
        Console.Write(output);
        await File.AppendAllTextAsync(log, $"\n--- Attempt {attempt + 1} ---\n{output}");
        if (exitCode == 0) return;
        var dnsFailure = (output.Contains("HttpRequestException", StringComparison.Ordinal)
                || output.Contains("SocketException", StringComparison.Ordinal))
            && new[] { "Name or service not known", "Temporary failure in name resolution",
                "No such host is known", "nodename nor servname provided", "getaddrinfo failed" }
                .Any(message => output.Contains(message, StringComparison.OrdinalIgnoreCase));
        var propagating = output.Contains("HTTP 403", StringComparison.Ordinal)
                || (output.Contains("HTTP 404", StringComparison.Ordinal)
                    && output.Contains("DeploymentNotFound", StringComparison.Ordinal));
        if ((!dnsFailure && !propagating) || attempt == 10)
            throw new InvalidOperationException(dnsFailure
                ? $"DNS resolution failed for live demo {Path.GetFileName(lab)} after {attempt + 1} attempts; see {log}."
                : $"Live demo {Path.GetFileName(lab)} failed; see {log}.");
        Console.WriteLine(dnsFailure
            ? "DNS lookup failed; endpoint or local DNS may not be ready. Retrying in 30 seconds."
            : "Azure access/deployment is not ready. Retrying in 30 seconds.");
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}

static Dictionary<string, string> Parse(string[] values)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index++)
    {
        var key = values[index];
        if (key == "--infra-only") { parsed[key] = ""; continue; }
        if (key is not ("--subscription" or "--principal-id" or "--prefix" or "--location"))
            throw new ArgumentException($"Unknown option: {key}");
        if (index + 1 >= values.Length || values[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{key} requires a value.");
        parsed[key] = values[++index];
    }
    return parsed;
}

static async Task<string> RunAz(IEnumerable<string> arguments, bool capture = false)
{
    var (_, output) = await RunProcess("az", arguments, Environment.CurrentDirectory, capture);
    return output;
}

static async Task<(int ExitCode, string Output)> RunProcess(string executable, IEnumerable<string> arguments,
    string workingDirectory, bool capture = false, bool throwOnError = true)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    Console.WriteLine($"> {executable} {string.Join(' ', start.ArgumentList)}");
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Unable to start {executable}.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await stdout + await stderr;
    if (!capture && output.Length > 0) Console.Write(output);
    if (throwOnError && process.ExitCode != 0)
        throw new InvalidOperationException($"{executable} exited with code {process.ExitCode}: {output}");
    return (process.ExitCode, output);
}
