using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class FoundryIqDeployLab
{
    private static readonly string Lab = Path.GetFileName(Directory.GetCurrentDirectory());
    private static readonly string State = Path.Combine(Directory.GetCurrentDirectory(), ".azure", "lab-deployment.json");
    private static readonly Dictionary<string, string> Owner = new() { ["managedBy"] = "foundry-lab-script", ["lab"] = Lab };
    private static readonly Dictionary<string, string> SecurityTags = new() { ["SecurityControl"] = "Ignore", ["SecurityExemption"] = "StorageAccountKeyAccess" };

    public sealed record Options(string? Subscription, string Location, string Prefix, string? PrincipalId);

    public static Options Parse(string[] args)
    {
        string? subscription = null, principal = null;
        var location = "eastus2";
        var prefix = "rg-foundry-student";
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--help") throw new HelpRequestedException();
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {option}.");
            var value = args[++index];
            switch (option)
            {
                case "--subscription": subscription = value; break;
                case "--location": location = value; break;
                case "--prefix": prefix = value; break;
                case "--principal-id": principal = value; break;
                default: throw new ArgumentException($"Unknown option: {option}.");
            }
        }
        if (!Regex.IsMatch(prefix, @"^[a-zA-Z0-9_-]{1,50}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("--prefix must contain 1-50 letters, numbers, underscores, or hyphens.");
        return new Options(subscription, location, prefix, principal);
    }

    public static string Run(string[] command, string? cwd = null, IDictionary<string, string?>? environment = null, bool capture = false)
    {
        Console.WriteLine($"\n> {string.Join(' ', command)}");
        var start = new ProcessStartInfo(command[0])
        {
            WorkingDirectory = cwd ?? Directory.GetCurrentDirectory(), RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var arg in command.Skip(1)) start.ArgumentList.Add(arg);
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command[0]}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (!capture && output.Length > 0) Console.Write(output);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{command[0]} failed ({process.ExitCode}): {error}{output}");
        return capture ? output : "";
    }

    public static void ValidateInputs(string directory)
    {
        foreach (var path in new[] { "infra/main.bicep", "demo.cs", "test_demo.cs" })
            if (!File.Exists(Path.Combine(directory, path))) throw new FileNotFoundException($"Missing lab input: {path}");
    }

    public static int Main(string[] args)
    {
        try
        {
            Options options;
            try { options = Parse(args); }
            catch (HelpRequestedException)
            {
                Console.WriteLine("Usage: dotnet run --file deploy_lab.cs -- [--subscription ID] [--location eastus2] [--prefix NAME] [--principal-id ID]");
                return 0;
            }
            var directory = Directory.GetCurrentDirectory();
            ValidateInputs(directory);
            var accountArgs = new List<string> { "az", "account", "show", "-o", "json" };
            if (options.Subscription is not null) accountArgs.AddRange(["--subscription", options.Subscription]);
            using var account = JsonDocument.Parse(Run(accountArgs.ToArray(), capture: true));
            if (account.RootElement.GetProperty("user").GetProperty("type").GetString() != "user")
                throw new ArgumentException("Run az login as a user; the template assigns a User role.");
            var subscription = account.RootElement.GetProperty("id").GetString()!;
            var name = Regex.Replace(Lab, @"^\d+-", "") + "-demo";
            var group = $"{options.Prefix}-{name}";
            if (File.Exists(State))
            {
                using var prior = JsonDocument.Parse(File.ReadAllText(State));
                var state = prior.RootElement;
                if ((state.GetProperty("subscription").GetString() != subscription || state.GetProperty("resourceGroup").GetString() != group) &&
                    (!state.TryGetProperty("cleanupStatus", out var cleanup) || cleanup.GetString() != "deleted"))
                    throw new ArgumentException("Recorded lab targets a different group/subscription; clean it up first.");
            }
            var exists = Run(["az", "group", "exists", "--name", group, "--subscription", subscription], capture: true).Trim();
            if (exists == "true")
            {
                using var tags = JsonDocument.Parse(Run(["az", "group", "show", "--name", group, "--subscription", subscription, "--query", "tags", "-o", "json"], capture: true));
                if (!File.Exists(State) || Owner.Any(pair => !tags.RootElement.TryGetProperty(pair.Key, out var item) || item.GetString() != pair.Value))
                    throw new ArgumentException("Refusing to reuse an existing group not owned by this lab script.");
            }
            else if (exists != "false") throw new InvalidOperationException($"Unexpected group existence response: {exists}");
            var principal = options.PrincipalId ?? Run(["az", "ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"], capture: true).Trim();
            if (string.IsNullOrWhiteSpace(principal)) throw new ArgumentException("Cannot resolve user object ID; provide --principal-id.");

            Directory.CreateDirectory(Path.GetDirectoryName(State)!);
            var stateRecord = new JsonObject
            {
                ["lab"] = Lab, ["subscription"] = subscription, ["resourceGroup"] = group,
                ["status"] = "running", ["stage"] = "local-tests", ["outputs"] = new JsonObject()
            };
            Save(stateRecord);
            try
            {
                Console.WriteLine($"Billable Azure deployment: {Lab} -> {group} ({subscription})");
                var tests = Run(["dotnet", "run", "--file", "test_demo.cs"], directory, capture: true);
                Console.Write(tests);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(State)!, "lab-tests.log"), tests);
                stateRecord["stage"] = "deployment";
                var template = Path.Combine(directory, "infra", "main.bicep");
                using var built = JsonDocument.Parse(Run(["az", "bicep", "build", "--file", template, "--stdout"], capture: true));
                Run(["az", "bicep", "lint", "--file", template]);
                var providers = built.RootElement.GetProperty("resources").EnumerateArray()
                    .Select(item => item.GetProperty("type").GetString()!.Split('/')[0])
                    .Where(provider => provider != "Microsoft.Authorization").Distinct().Order().ToArray();
                foreach (var provider in providers) Run(["az", "provider", "register", "--namespace", provider, "--wait", "--subscription", subscription]);
                var tags = Owner.Concat(SecurityTags).Select(pair => $"{pair.Key}={pair.Value}");
                Run(["az", "group", "create", "--name", group, "--location", options.Location, "--tags", .. tags, "--subscription", subscription, "-o", "none"]);
                var outputs = JsonDocument.Parse(Run([
                    "az", "deployment", "group", "create", "--resource-group", group, "--name", name,
                    "--subscription", subscription, "--template-file", template, "--parameters",
                    $"principalId={principal}", $"location={options.Location}", "--query", "properties.outputs", "-o", "json"
                ], capture: true));
                stateRecord["outputs"] = JsonNode.Parse(outputs.RootElement.GetRawText());
                stateRecord["stage"] = "live-demo";
                Save(stateRecord);
                RunLive(directory, outputs.RootElement.GetProperty("endpoint").GetProperty("value").GetString()!,
                    outputs.RootElement.GetProperty("searchEndpoint").GetProperty("value").GetString()!,
                    outputs.RootElement.GetProperty("projectEndpoint").GetProperty("value").GetString()!,
                    outputs.RootElement.GetProperty("deployment").GetProperty("value").GetString()!);
                stateRecord["status"] = "passed";
                stateRecord["stage"] = "complete";
                Console.WriteLine($"PASS: {Lab}. Resources retained. Results: {State}");
                return 0;
            }
            catch (Exception error)
            {
                stateRecord["status"] = "failed";
                stateRecord["error"] = error.Message;
                throw;
            }
            finally { Save(stateRecord); }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}\nResources are retained; inspect .azure/lab-deployment.json and run cleanup.cs.");
            return 1;
        }
    }

    public static void RunLive(string directory, string endpoint, string searchEndpoint, string projectEndpoint, string deployment)
    {
        var log = Path.Combine(directory, ".azure", "demo.log");
        for (var attempt = 0; attempt < 11; attempt++)
        {
            Console.WriteLine($"\nRunning live demo: {Lab} (attempt {attempt + 1}/11)");
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "run", "--file", "demo.cs", "--" }) start.ArgumentList.Add(arg);
            start.Environment["AZURE_OPENAI_ENDPOINT"] = endpoint;
            start.Environment["AZURE_SEARCH_ENDPOINT"] = searchEndpoint;
            start.Environment["FOUNDRY_PROJECT_ENDPOINT"] = projectEndpoint;
            start.Environment["AZURE_OPENAI_DEPLOYMENT"] = deployment;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start .NET demo.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var output = outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult();
            Console.Write(output);
            File.AppendAllText(log, $"\n--- Attempt {attempt + 1} ---\n{output}");
            if (process.ExitCode == 0) return;
            var dnsFailure = new[] { "Name or service not known", "Temporary failure in name resolution", "nodename nor servname", "getaddrinfo failed" }
                .Any(message => output.Contains(message, StringComparison.Ordinal));
            var transient = dnsFailure || output.Contains("403", StringComparison.Ordinal) ||
                            output.Contains("404", StringComparison.Ordinal) || output.Contains("DeploymentNotFound", StringComparison.Ordinal);
            if (!transient || attempt == 10) throw new InvalidOperationException($"Live demo {Lab} failed; see {log}.");
            Console.WriteLine($"{(dnsFailure ? "DNS lookup failed." : "Azure access/deployment is not ready.")} Retrying in 30 seconds.");
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }
    }

    private static void Save(JsonObject record) =>
        File.WriteAllText(State, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

    private sealed class HelpRequestedException : Exception { }
}
