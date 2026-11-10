using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class SemanticCacheDeployLab
{
    public static readonly string[] RedisSkus = ["Balanced_B0", "Balanced_B1", "Balanced_B3", "Balanced_B5"];
    public static readonly string Lab = Path.GetFileName(Directory.GetCurrentDirectory());
    public static readonly string State = Path.Combine(Directory.GetCurrentDirectory(), ".azure", "lab-deployment.json");
    public static readonly Dictionary<string, string> Owner = new() { ["managedBy"] = "foundry-lab-script", ["lab"] = Lab };
    public static readonly Dictionary<string, string> SecurityTags = new() { ["SecurityControl"] = "Ignore", ["SecurityExemption"] = "StorageAccountKeyAccess" };

    public sealed record Options(string? Subscription, string Location, string Prefix, string? PrincipalId, string RedisSku);

    public static Options Parse(string[] args)
    {
        string? subscription = null, principal = null;
        var location = "eastus2";
        var prefix = "rg-foundry-student";
        var sku = "Balanced_B0";
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--help")
                throw new HelpRequestedException();
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}.");
            var value = args[++index];
            switch (option)
            {
                case "--subscription": subscription = value; break;
                case "--location": location = value; break;
                case "--prefix": prefix = value; break;
                case "--principal-id": principal = value; break;
                case "--redis-sku": sku = value; break;
                default: throw new ArgumentException($"Unknown option: {option}.");
            }
        }
        if (!Regex.IsMatch(prefix, @"^[a-zA-Z0-9_-]{1,50}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("--prefix must contain 1-50 letters, numbers, underscores, or hyphens.");
        if (!RedisSkus.Contains(sku, StringComparer.Ordinal))
            throw new ArgumentException($"Unsupported Redis SKU: {sku}.");
        return new Options(subscription, location, prefix, principal, sku);
    }

    public static string GroupName(string prefix, string lab) =>
        $"{prefix}-{Regex.Replace(lab, @"^\d+-", "")}-demo";

    public static string[] DeploymentArguments(string group, string name, string subscription, string template,
        string principal, string location, string sku) =>
    [
        "deployment", "group", "create", "--resource-group", group, "--name", name,
        "--subscription", subscription, "--template-file", template, "--parameters",
        $"principalId={principal}", $"location={location}", $"redisSku={sku}",
        "--query", "properties.outputs", "-o", "json"
    ];

    public static string Run(string[] command, string? workingDirectory = null, IDictionary<string, string?>? environment = null, bool capture = false)
    {
        Console.WriteLine($"\n> {string.Join(' ', command)}");
        var start = new ProcessStartInfo(command[0])
        {
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in command.Skip(1))
            start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment)
                start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command[0]}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (!capture && output.Length > 0) Console.Write(output);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{command[0]} failed ({process.ExitCode}): {error}{output}");
        return capture ? output : "";
    }

    public static void ValidateInputs(string labDirectory)
    {
        foreach (var path in new[] { "infra/main.bicep", "demo.cs", "test_demo.cs" })
            if (!File.Exists(Path.Combine(labDirectory, path)))
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
                Console.WriteLine("Usage: dotnet run --file deploy_lab.cs -- [--subscription ID] [--location eastus2] [--redis-sku Balanced_B0|Balanced_B1|Balanced_B3|Balanced_B5] [--prefix NAME] [--principal-id ID]");
                return 0;
            }
            var labPath = Directory.GetCurrentDirectory();
            ValidateInputs(labPath);
            var accountArgs = new List<string> { "az", "account", "show", "-o", "json" };
            if (options.Subscription is not null)
                accountArgs.AddRange(["--subscription", options.Subscription]);
            using var account = JsonDocument.Parse(Run(accountArgs.ToArray(), capture: true));
            var accountRoot = account.RootElement;
            if (accountRoot.GetProperty("user").GetProperty("type").GetString() != "user")
                throw new ArgumentException("Run az login as a user; the template assigns a User role.");
            var subscriptionId = accountRoot.GetProperty("id").GetString()!;
            var name = Regex.Replace(Lab, @"^\d+-", "") + "-demo";
            var group = GroupName(options.Prefix, Lab);
            if (File.Exists(State))
            {
                using var previous = JsonDocument.Parse(File.ReadAllText(State));
                var old = previous.RootElement;
                if ((old.GetProperty("subscription").GetString() != subscriptionId ||
                     old.GetProperty("resourceGroup").GetString() != group) &&
                    (!old.TryGetProperty("cleanupStatus", out var status) || status.GetString() != "deleted"))
                    throw new ArgumentException("Recorded lab targets a different group/subscription; clean it up first.");
            }
            var exists = Run(["az", "group", "exists", "--name", group, "--subscription", subscriptionId], capture: true).Trim();
            if (exists == "true")
            {
                using var tagsDoc = JsonDocument.Parse(Run(["az", "group", "show", "--name", group, "--subscription", subscriptionId, "--query", "tags", "-o", "json"], capture: true));
                if (!File.Exists(State) || Owner.Any(pair => !tagsDoc.RootElement.TryGetProperty(pair.Key, out var value) || value.GetString() != pair.Value))
                    throw new ArgumentException("Refusing to reuse an existing group not owned by this lab script.");
            }
            else if (exists != "false")
                throw new InvalidOperationException($"Unexpected group existence response: {exists}");
            var principal = options.PrincipalId ?? Run(["az", "ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"], capture: true).Trim();
            if (string.IsNullOrWhiteSpace(principal))
                throw new ArgumentException("Cannot resolve user object ID; provide --principal-id.");

            Directory.CreateDirectory(Path.GetDirectoryName(State)!);
            var result = new JsonObject
            {
                ["lab"] = Lab, ["subscription"] = subscriptionId, ["resourceGroup"] = group,
                ["redisSku"] = options.RedisSku, ["status"] = "running", ["stage"] = "local-tests",
                ["outputs"] = new JsonObject()
            };
            Save(result);
            try
            {
                Console.WriteLine($"Billable Azure deployment: {Lab} -> {group} ({subscriptionId})");
                var testLog = Run(["dotnet", "run", "--file", "test_demo.cs"], labPath, capture: true);
                Console.Write(testLog);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(State)!, "lab-tests.log"), testLog);
                result["stage"] = "deployment";
                var template = Path.Combine(labPath, "infra", "main.bicep");
                var built = JsonDocument.Parse(Run(["az", "bicep", "build", "--file", template, "--stdout"], capture: true));
                Run(["az", "bicep", "lint", "--file", template]);
                var providers = built.RootElement.GetProperty("resources").EnumerateArray()
                    .Select(resource => resource.GetProperty("type").GetString()!.Split('/')[0])
                    .Where(provider => provider != "Microsoft.Authorization").Distinct().Order().ToArray();
                foreach (var provider in providers)
                    Run(["az", "provider", "register", "--namespace", provider, "--wait", "--subscription", subscriptionId]);
                var tags = Owner.Concat(SecurityTags).Select(pair => $"{pair.Key}={pair.Value}");
                Run(["az", "group", "create", "--name", group, "--location", options.Location, "--tags", .. tags, "--subscription", subscriptionId, "-o", "none"]);
                var outputs = JsonDocument.Parse(Run(DeploymentArguments(group, name, subscriptionId, template, principal, options.Location, options.RedisSku), capture: true));
                result["outputs"] = JsonNode.Parse(outputs.RootElement.GetRawText());
                result["stage"] = "live-demo";
                Save(result);
                var endpoint = outputs.RootElement.GetProperty("endpoint").GetProperty("value").GetString()!;
                var deployment = outputs.RootElement.GetProperty("deployment").GetProperty("value").GetString()!;
                RunLive(labPath, endpoint, deployment);
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
            finally { Save(result); }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}\nResources are retained; inspect .azure/lab-deployment.json and run cleanup.cs.");
            return 1;
        }
    }

    private static void Save(JsonObject value) =>
        File.WriteAllText(State, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

    public static void RunLive(string labPath, string endpoint, string deployment)
    {
        var logPath = Path.Combine(labPath, ".azure", "demo.log");
        for (var attempt = 0; attempt < 11; attempt++)
        {
            Console.WriteLine($"\nRunning live demo: {Lab} (attempt {attempt + 1}/11)");
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = labPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[] { "run", "--file", "demo.cs", "--" })
                start.ArgumentList.Add(argument);
            start.Environment["AZURE_OPENAI_ENDPOINT"] = endpoint;
            start.Environment["AZURE_OPENAI_DEPLOYMENT"] = deployment;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start .NET demo.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var output = outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult();
            Console.Write(output);
            File.AppendAllText(logPath, $"\n--- Attempt {attempt + 1} ---\n{output}");
            if (process.ExitCode == 0)
                return;
            var dnsFailure = (output.Contains("HttpRequestException", StringComparison.Ordinal) ||
                              output.Contains("Azure CLI", StringComparison.Ordinal)) &&
                new[] { "Name or service not known", "Temporary failure in name resolution", "nodename nor servname", "getaddrinfo failed" }
                    .Any(message => output.Contains(message, StringComparison.Ordinal));
            var propagating = output.Contains("403", StringComparison.Ordinal) ||
                              output.Contains("DeploymentNotFound", StringComparison.Ordinal);
            if (!(dnsFailure || propagating) || attempt == 10)
                throw new InvalidOperationException($"Live demo {Lab} failed; see {logPath}.");
            Console.WriteLine($"{(dnsFailure ? "DNS lookup failed; the endpoint or local DNS may not be ready." : "Azure access/deployment is not ready.")} Retrying in 30 seconds.");
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class HelpRequestedException : Exception { }
}
