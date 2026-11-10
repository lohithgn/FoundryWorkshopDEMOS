#:property TargetFramework=net10.0

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class EntraAgentDeployLab
{
    private static readonly string Lab = Path.GetFileName(Directory.GetCurrentDirectory());
    private static readonly string State = Path.Combine(Directory.GetCurrentDirectory(), ".azure", "lab-deployment.json");
    private static readonly Dictionary<string, string> Owner = new()
    {
        ["managedBy"] = "foundry-lab-script",
        ["lab"] = Lab
    };
    private static readonly Dictionary<string, string> SecurityTags = new()
    {
        ["SecurityControl"] = "Ignore",
        ["SecurityExemption"] = "StorageAccountKeyAccess"
    };

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

    public static (string Tenant, string Agent) IdentitySettings(
        string directory, IReadOnlyDictionary<string, string?> environment)
    {
        var names = new[]
        {
            "ENTRA_TENANT_ID", "ENTRA_BLUEPRINT_CLIENT_ID", "ENTRA_AGENT_ID", "ENTRA_BLUEPRINT_SECRET"
        };
        var missing = names.Where(name => string.IsNullOrWhiteSpace(environment.GetValueOrDefault(name))).ToArray();
        if (missing.Length > 0)
            throw new ArgumentException("Run setup_identity.ps1 in PowerShell first, then stay in that session. Missing: " +
                string.Join(", ", missing));
        if (!Guid.TryParse(environment[names[0]], out var tenant) ||
            !Guid.TryParse(environment[names[1]], out var blueprint) ||
            !Guid.TryParse(environment[names[2]], out var agent))
            throw new ArgumentException("Entra tenant, blueprint, and child agent IDs must be UUIDs.");
        if (blueprint == agent)
            throw new ArgumentException("ENTRA_AGENT_ID must identify the child agent, not the blueprint.");
        var identityPath = Path.Combine(directory, ".azure", "identity.json");
        if (!File.Exists(identityPath))
            throw new ArgumentException("Missing .azure/identity.json from setup_identity.ps1; cannot track tenant cleanup.");
        using var document = JsonDocument.Parse(File.ReadAllText(identityPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Identity setup record must be an object.");
        foreach (var (key, value) in new[]
        {
            ("tenantId", tenant), ("blueprintClientId", blueprint), ("agentIdentityId", agent)
        })
        {
            if (!document.RootElement.TryGetProperty(key, out var recorded) ||
                recorded.ValueKind != JsonValueKind.String ||
                !string.Equals(recorded.GetString(), value.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Identity setup record does not match {key}; refusing deployment.");
        }
        foreach (var key in new[] { "blueprintObjectId", "blueprintPrincipalId" })
        {
            if (!document.RootElement.TryGetProperty(key, out var recorded) ||
                recorded.ValueKind != JsonValueKind.String || !Guid.TryParse(recorded.GetString(), out _))
                throw new ArgumentException($"Identity setup record has no valid {key}.");
        }
        return (tenant.ToString(), agent.ToString());
    }

    public static string Run(
        IReadOnlyList<string> command, string? cwd = null, bool capture = false, bool combineError = false,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        Console.WriteLine($"\n> {string.Join(' ', command)}");
        var start = new ProcessStartInfo(command[0])
        {
            WorkingDirectory = cwd ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command[0]}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (!capture && output.Length > 0) Console.Write(output);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{command[0]} failed ({process.ExitCode}): {error}{output}");
        return capture ? (combineError ? output + error : output) : "";
    }

    public static void ValidateInputs(string directory)
    {
        foreach (var path in new[] { "infra/main.bicep", "test_demo.cs", "demo.cs" })
            if (!File.Exists(Path.Combine(directory, path)))
                throw new FileNotFoundException($"Missing lab input: {path}");
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
            (string tenant, string agent) identity;
            try
            {
                identity = IdentitySettings(directory, EnvironmentMap());
            }
            catch (Exception error) when (error is IOException or ArgumentException or JsonException)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(State)!);
                Save(new JsonObject
                {
                    ["lab"] = Lab, ["status"] = "blocked", ["stage"] = "identity-prerequisites", ["error"] = error.Message
                }, Path.Combine(Path.GetDirectoryName(State)!, "lab-preflight.json"));
                throw;
            }

            var accountCommand = new List<string> { "az", "account", "show", "-o", "json" };
            if (options.Subscription is not null) accountCommand.AddRange(["--subscription", options.Subscription]);
            using var account = JsonDocument.Parse(Run(accountCommand, capture: true));
            if (account.RootElement.GetProperty("user").GetProperty("type").GetString() != "user")
                throw new ArgumentException("Run az login as a user; the template assigns a User role.");
            if (!string.Equals(account.RootElement.GetProperty("tenantId").GetString(), identity.tenant,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Azure subscription tenant does not match the recorded Entra identity tenant.");
            var subscription = account.RootElement.GetProperty("id").GetString()!;
            var name = Regex.Replace(Lab, @"^\d+-", "") + "-demo";
            var group = $"{options.Prefix}-{name}";
            var scope = new[] { "--subscription", subscription };
            if (File.Exists(State))
            {
                using var previous = JsonDocument.Parse(File.ReadAllText(State));
                var prior = previous.RootElement;
                var cleanupDeleted = prior.TryGetProperty("cleanupStatus", out var cleanup) && cleanup.GetString() == "deleted";
                if ((prior.GetProperty("subscription").GetString() != subscription ||
                     prior.GetProperty("resourceGroup").GetString() != group) && !cleanupDeleted)
                    throw new ArgumentException("Recorded lab targets a different group/subscription; clean it up first.");
            }
            var exists = Run(["az", "group", "exists", "--name", group, .. scope], capture: true).Trim();
            if (exists == "true")
            {
                using var tags = JsonDocument.Parse(Run(
                    ["az", "group", "show", "--name", group, .. scope, "--query", "tags", "-o", "json"], capture: true));
                if (!File.Exists(State) || tags.RootElement.ValueKind != JsonValueKind.Object ||
                    Owner.Any(pair => !tags.RootElement.TryGetProperty(pair.Key, out var item) || item.GetString() != pair.Value))
                    throw new ArgumentException("Refusing to reuse an existing group not owned by this lab script.");
            }
            else if (exists != "false")
                throw new InvalidOperationException($"Unexpected group existence response: {exists}");
            var principal = options.PrincipalId ?? Run(
                ["az", "ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"], capture: true).Trim();
            if (string.IsNullOrWhiteSpace(principal))
                throw new ArgumentException("Cannot resolve user object ID; provide --principal-id.");

            Directory.CreateDirectory(Path.GetDirectoryName(State)!);
            var result = new JsonObject
            {
                ["lab"] = Lab, ["subscription"] = subscription, ["resourceGroup"] = group,
                ["status"] = "running", ["stage"] = "local-tests", ["outputs"] = new JsonObject()
            };
            Save(result, State);
            try
            {
                Console.WriteLine($"Billable Azure deployment: {Lab} -> {group} ({subscription})");
                Console.WriteLine("\n> dotnet run --file test_demo.cs");
                var testStart = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false
                };
                foreach (var argument in new[] { "run", "--file", "test_demo.cs" })
                    testStart.ArgumentList.Add(argument);
                using var testProcess = Process.Start(testStart) ??
                    throw new InvalidOperationException("Could not start .NET offline tests.");
                var testOutput = testProcess.StandardOutput.ReadToEndAsync();
                var testError = testProcess.StandardError.ReadToEndAsync();
                testProcess.WaitForExit();
                var tests = testOutput.GetAwaiter().GetResult() + testError.GetAwaiter().GetResult();
                Console.Write(tests);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(State)!, "lab-tests.log"), tests);
                if (testProcess.ExitCode != 0)
                    throw new InvalidOperationException($"Offline .NET tests failed ({testProcess.ExitCode}).");
                result["stage"] = "deployment";
                var template = Path.Combine(directory, "infra", "main.bicep");
                using var compiled = JsonDocument.Parse(Run(["az", "bicep", "build", "--file", template, "--stdout"], capture: true));
                Run(["az", "bicep", "lint", "--file", template]);
                var providers = compiled.RootElement.GetProperty("resources").EnumerateArray()
                    .Select(resource => resource.GetProperty("type").GetString()!)
                    .Where(type => !type.StartsWith("Microsoft.Authorization/", StringComparison.Ordinal))
                    .Select(type => type.Split('/')[0]).Distinct().Order().ToArray();
                foreach (var provider in providers)
                    Run(["az", "provider", "register", "--namespace", provider, "--wait", .. scope]);
                var tagsToSet = Owner.Concat(SecurityTags).Select(pair => $"{pair.Key}={pair.Value}");
                Run(["az", "group", "create", "--name", group, "--location", options.Location, "--tags",
                    .. tagsToSet, .. scope, "-o", "none"]);
                using var deployment = JsonDocument.Parse(Run([
                    "az", "deployment", "group", "create", "--resource-group", group, "--name", name,
                    .. scope, "--template-file", template, "--parameters",
                    $"principalId={principal}", $"location={options.Location}", $"agentIdentityId={identity.agent}",
                    "--query", "properties.outputs", "-o", "json"
                ], capture: true));
                result["outputs"] = JsonNode.Parse(deployment.RootElement.GetRawText());
                result["stage"] = "live-demo";
                Save(result, State);
                RunLive(directory, result["outputs"]!);
                result["status"] = "passed";
                result["stage"] = "complete";
                Console.WriteLine($"PASS: {Lab}. Resources retained. Results: {State}");
                return 0;
            }
            catch (Exception error)
            {
                result["status"] = "failed";
                result["error"] = error.Message;
                throw;
            }
            finally { Save(result, State); }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}\nResources are retained; inspect .azure/lab-deployment.json and run cleanup.cs.");
            return 1;
        }
    }

    public static IReadOnlyDictionary<string, string?> EnvironmentMap() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(item => (string)item.Key, item => item.Value?.ToString(), StringComparer.Ordinal);

    private static void RunLive(string directory, JsonNode outputs)
    {
        var endpoint = outputs["endpoint"]?["value"]?.GetValue<string>();
        var deployment = outputs["deployment"]?["value"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
            throw new ArgumentException("Bicep must return nonempty endpoint and deployment outputs.");
        var log = Path.Combine(directory, ".azure", "demo.log");
        for (var attempt = 0; attempt < 11; attempt++)
        {
            Console.WriteLine($"\nRunning live demo: {Lab} (attempt {attempt + 1}/11)");
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[] { "run", "--file", "demo.cs", "--" }) start.ArgumentList.Add(argument);
            start.Environment["AZURE_OPENAI_ENDPOINT"] = endpoint;
            start.Environment["AZURE_OPENAI_DEPLOYMENT"] = deployment;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start .NET demo.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var output = outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult();
            Console.Write(output);
            File.AppendAllText(log, $"\n--- Attempt {attempt + 1} ---\n{output}");
            if (process.ExitCode == 0) return;
            var dnsFailure = new[]
            {
                "Name or service not known", "Temporary failure in name resolution",
                "No such host is known", "nodename nor servname"
            }.Any(message => output.Contains(message, StringComparison.OrdinalIgnoreCase));
            var propagating = output.Contains("403", StringComparison.Ordinal) ||
                              output.Contains("DeploymentNotFound", StringComparison.Ordinal);
            if (!(dnsFailure || propagating) || attempt == 10)
                throw new InvalidOperationException($"Live demo {Lab} failed; see {log}.");
            Console.WriteLine($"{(dnsFailure ? "DNS lookup failed; the endpoint or local DNS may not be ready." : "Azure access/deployment is not ready.")} Retrying in 30 seconds.");
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }
    }

    private static void Save(JsonNode record, string path) =>
        File.WriteAllText(path, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

    private sealed class HelpRequestedException : Exception { }
}
