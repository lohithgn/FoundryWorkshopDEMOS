#:property TargetFramework=net10.0
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var lab = Environment.CurrentDirectory;
if (!File.Exists(Path.Combine(lab, "infra", "main.bicep")))
    throw new DirectoryNotFoundException("Run this file from 45-governed-iq.");
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Deploy only this recorded, owned synthetic lab resource group.\nUsage: dotnet run --file deploy_lab.cs -- [--subscription ID] [--principal-id ID] [--prefix PREFIX] [--location REGION] [--infra-only]");
    return;
}
if (ValidateArguments(args) is { } argumentError)
{
    Console.Error.WriteLine($"ERROR: {argumentError}");
    Environment.ExitCode = 2;
    return;
}
var subscriptionArg = Option(args, "--subscription");
var principalArg = Option(args, "--principal-id");
var prefix = Option(args, "--prefix");
var location = Option(args, "--location") ?? "eastus2";
var infraOnly = args.Contains("--infra-only");
if (prefix is not null && !Regex.IsMatch(prefix, "^[a-zA-Z0-9_-]{1,50}$"))
{
    Console.Error.WriteLine("ERROR: --prefix must contain 1-50 letters, numbers, underscores, or hyphens.");
    Environment.ExitCode = 2;
    return;
}
var state = Path.Combine(lab, ".azure");
var recordPath = Path.Combine(state, "lab-deployment.json");
var accountArgs = new List<string> { "account", "show", "-o", "json" };
if (subscriptionArg is not null) accountArgs.AddRange(["--subscription", subscriptionArg]);
var account = JsonNode.Parse(await AzCli.Run(accountArgs.ToArray()))!.AsObject();
if (account["user"]?["type"]?.ToString() != "user")
    throw new InvalidOperationException("Use az login as a user; the template assigns User RBAC.");
var subscription = account["id"]!.ToString();
var scope = new[] { "--subscription", subscription };
var previous = File.Exists(recordPath) ? JsonNode.Parse(await File.ReadAllTextAsync(recordPath))!.AsObject() : null;
var name = Regex.Replace(Path.GetFileName(lab), "^\\d+-", "") + "-demo";
var group = previous is not null && prefix is null ? previous["resourceGroup"]!.ToString() : $"{prefix ?? "rg-foundry"}-{name}";
if (!Regex.IsMatch(group, "^[a-zA-Z0-9_-]{1,90}$") || !group.EndsWith("-" + name, StringComparison.Ordinal))
    throw new ArgumentException("Recorded group is not a valid group for this lab.");
if (previous is not null && (previous["subscription"]?.ToString() != subscription || previous["resourceGroup"]?.ToString() != group))
    throw new InvalidOperationException("Recorded lab targets a different subscription/group; do not overwrite its state.");
var exists = (await AzCli.Run(["group", "exists", "--name", group, .. scope])).Trim();
if (exists == "true")
{
    var tags = JsonNode.Parse(await AzCli.Run(["group", "show", "--name", group, .. scope, "--query", "tags", "-o", "json"]))?.AsObject();
    if (previous is null || tags is null || tags["managedBy"]?.ToString() != "foundry-lab-script"
        || tags["lab"]?.ToString() != Path.GetFileName(lab))
        throw new InvalidOperationException("Refusing to reuse a group not recorded and owned by this lab.");
}
else if (exists != "false")
    throw new InvalidDataException($"Unexpected group existence response: {exists}");
var principal = principalArg ?? (await AzCli.Run("ad", "signed-in-user", "show", "--query", "id", "-o", "tsv")).Trim();
if (string.IsNullOrEmpty(principal)) throw new InvalidOperationException("Provide --principal-id or sign in as an identifiable user.");
Directory.CreateDirectory(state);
if (!infraOnly)
{
    await Run("dotnet", ["run", "--file", "test_demo.cs"], lab);
}
var template = Path.Combine(lab, "infra", "main.bicep");
var compiled = JsonNode.Parse(await AzCli.Run("bicep", "build", "--file", template, "--stdout"))!.AsObject();
var providers = new SortedSet<string>(StringComparer.Ordinal);
foreach (var resource in compiled["resources"]?.AsArray() ?? new JsonArray())
{
    var type = resource?["type"]?.ToString() ?? "";
    if (!type.StartsWith("Microsoft.Authorization/", StringComparison.Ordinal))
        providers.Add(type.Split('/')[0]);
}
foreach (var provider in providers)
    await AzCli.Run(["provider", "register", "--namespace", provider, "--wait", .. scope]);
await AzCli.Run(["group", "create", "--name", group, "--location", location,
    "--tags", "managedBy=foundry-lab-script", $"lab={Path.GetFileName(lab)}",
    "SecurityControl=Ignore", "SecurityExemption=StorageAccountKeyAccess", .. scope, "-o", "none"]);
var result = new JsonObject { ["subscription"] = subscription, ["resourceGroup"] = group, ["outputs"] = new JsonObject() };
await File.WriteAllTextAsync(recordPath, result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
var outputsText = await AzCli.Run(["deployment", "group", "create", "--resource-group", group,
    "--name", name, .. scope, "--template-file", template, "--parameters",
    $"principalId={principal}", $"location={location}", "--query", "properties.outputs", "-o", "json"]);
result["outputs"] = JsonNode.Parse(outputsText);
await File.WriteAllTextAsync(recordPath, result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
var rootState = Path.Combine(lab, ".azure", "deployment.json");
await File.WriteAllTextAsync(rootState, result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
if (!infraOnly)
{
    var environment = new Dictionary<string, string?>();
    var mapping = new Dictionary<string, string> {
        ["endpoint"] = "AZURE_OPENAI_ENDPOINT", ["deployment"] = "AZURE_OPENAI_DEPLOYMENT",
        ["projectEndpoint"] = "FOUNDRY_PROJECT_ENDPOINT", ["projectResourceId"] = "FOUNDRY_PROJECT_RESOURCE_ID",
        ["embeddingDeployment"] = "AZURE_OPENAI_EMBEDDING_DEPLOYMENT", ["storageEndpoint"] = "AZURE_STORAGE_BLOB_ENDPOINT",
        ["storageResourceId"] = "AZURE_STORAGE_RESOURCE_ID", ["containerResourceId"] = "GOVERNED_IQ_CONTAINER_RESOURCE_ID",
        ["searchEndpoint"] = "AZURE_SEARCH_ENDPOINT", ["searchResourceId"] = "AZURE_SEARCH_RESOURCE_ID",
        ["tenantId"] = "AZURE_TENANT_ID", ["principalId"] = "AZURE_PRINCIPAL_ID"
    };
    foreach (var (key, env) in mapping)
        if (result["outputs"]?[key]?["value"] is JsonNode value) environment[env] = value.ToString();
    await Run("dotnet", ["run", "--file", "demo.cs"], lab, environment);
}

static string? Option(string[] values, string option)
{
    var index = Array.IndexOf(values, option);
    if (index < 0) return null;
    if (index + 1 >= values.Length) throw new ArgumentException($"{option} requires a value.");
    return values[index + 1];
}

static string? ValidateArguments(string[] values)
{
    var flags = new HashSet<string>(["--infra-only"], StringComparer.Ordinal);
    var options = new HashSet<string>(["--subscription", "--principal-id", "--prefix", "--location"], StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index++)
    {
        if (flags.Contains(values[index])) continue;
        if (!options.Contains(values[index])) return $"Unknown option: {values[index]}";
        if (index + 1 >= values.Length || values[index + 1].StartsWith("--", StringComparison.Ordinal))
            return $"{values[index]} requires a value.";
        index++;
    }
    return null;
}

static async Task Run(string executable, string[] arguments, string directory, IDictionary<string, string?>? env = null)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    if (env is not null) foreach (var pair in env) start.Environment[pair.Key] = pair.Value;
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Unable to start {executable}.");
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited with {process.ExitCode}.");
}

static class AzCli
{
    public static async Task<string> Run(params string[] args)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Azure CLI.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Azure CLI failed ({process.ExitCode}): {stderr.Trim()}");
        return stdout;
    }
}
