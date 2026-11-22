namespace AgentOrchestrations;

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

public interface IAzureCli
{
    string Run(params string[] arguments);
}

public sealed class ProcessAzureCli : IAzureCli
{
    public string Run(params string[] arguments)
    {
        Console.WriteLine($"\n> az {string.Join(" ", arguments.Select(Quote))}");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("az")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"az {string.Join(" ", arguments)} failed (exit {process.ExitCode}): {stderr.Trim()}");
        return stdout.Trim();
    }

    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}

public sealed class LabState
{
    public string Lab { get; set; } = "";
    public string Subscription { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string Location { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Stage { get; set; } = "";
    public string? CleanupStatus { get; set; }
    public string? CleanupError { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, JsonElement> Outputs { get; set; } = [];
}

public sealed record DeploymentPreparation(
    string LabDirectory, string StatePath, string Prefix, string Group,
    string Name, string PrincipalId, string Subscription, string Location,
    string AccountName, bool PrepareOnly, LabState State);

public static class LabLifecycle
{
    private static readonly Dictionary<string, string> SecurityTags = new()
    {
        ["SecurityControl"] = "Ignore",
        ["SecurityExemption"] = "StorageAccountKeyAccess",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string StatePath(string labDirectory) =>
        Path.Combine(labDirectory, ".azure", "lab-deployment.json");

    public static DeploymentPreparation Prepare(
        string[] args, string labDirectory, string statePath, IAzureCli azure)
    {
        var options = ParseDeployArguments(args);
        var lab = Path.GetFullPath(labDirectory);
        var labName = Path.GetFileName(lab);
        foreach (var required in new[] { "infra/main.bicep", "test_demo.cs", "demo.cs" })
            if (!File.Exists(Path.Combine(lab, required)))
                throw new CommandLineException($"Missing lab input: {required}");

        var accountCommand = new List<string> { "account", "show", "-o", "json" };
        if (!string.IsNullOrWhiteSpace(options.Subscription))
            accountCommand.AddRange(["--subscription", options.Subscription]);
        using var account = JsonDocument.Parse(azure.Run(accountCommand.ToArray()));
        var root = account.RootElement;
        if (root.GetProperty("user").GetProperty("type").GetString() != "user")
            throw new CommandLineException("Run az login as a user; the template assigns a User role.");
        var subscription = root.GetProperty("id").GetString()
            ?? throw new CommandLineException("Azure CLI did not return a subscription ID.");

        var name = Regex.Replace(labName, @"^\d+-", "") + "-demo";
        var group = $"{options.Prefix}-{name}";
        var scope = new[] { "--subscription", subscription };
        LabState? previous = null;
        var sameTarget = false;
        if (File.Exists(statePath))
        {
            previous = ReadState(statePath);
            if (string.IsNullOrEmpty(previous.Lab))
                previous.Lab = labName;
            ValidateState(previous, labName, name);
            sameTarget = previous.Subscription == subscription && previous.ResourceGroup == group;
            if (!sameTarget)
            {
                if (previous.CleanupStatus != "deleted" || !options.StartFresh)
                    throw new CommandLineException(
                        "Recorded lab targets a different group/subscription; run cleanup.cs, then --start-fresh.");
                var oldExists = azure.Run([
                    "group", "exists", "--name", previous.ResourceGroup,
                    "--subscription", previous.Subscription,
                ]).Trim();
                if (oldExists != "false")
                    throw new CommandLineException("Cannot verify the previous group is absent; run cleanup.cs before starting fresh.");
            }
        }

        var exists = azure.Run(["group", "exists", "--name", group, .. scope]).Trim();
        if (exists == "true")
        {
            using var tagsDocument = JsonDocument.Parse(azure.Run([
                "group", "show", "--name", group, .. scope, "--query", "tags", "-o", "json",
            ]));
            var tags = tagsDocument.RootElement;
            if (!sameTarget || tags.ValueKind != JsonValueKind.Object ||
                !HasTag(tags, "managedBy", "foundry-lab-script") || !HasTag(tags, "lab", labName))
                throw new CommandLineException("Refusing to reuse an existing group not owned by this lab script.");
            if (options.StartFresh)
                throw new CommandLineException("--start-fresh requires the group to be absent. Run cleanup.cs first.");
            if (previous!.CleanupStatus == "deleted")
                throw new CommandLineException("The deleted group has reappeared; inspect its ownership before running cleanup.cs.");
        }
        else if (exists != "false")
            throw new InvalidOperationException($"Unexpected group existence response: {exists}");

        string accountName;
        if (previous is not null && !options.StartFresh)
        {
            if (previous.CleanupStatus == "deleted")
                throw new CommandLineException("This lifecycle was deleted. Use --start-fresh to create a new account name.");
            if (!Regex.IsMatch(previous.AccountName, @"^foundry-patterns-[0-9a-f]{32}$"))
                throw new CommandLineException("Saved lifecycle has no valid accountName. Run cleanup.cs, then deploy with --start-fresh.");
            if (previous.Location != options.Location)
                throw new CommandLineException("A lifecycle cannot change location. Run cleanup.cs, then --start-fresh.");
            accountName = previous.AccountName;
        }
        else
            accountName = $"foundry-patterns-{Guid.NewGuid():N}";

        var principal = options.PrincipalId;
        if (string.IsNullOrWhiteSpace(principal))
            principal = azure.Run("ad", "signed-in-user", "show", "--query", "id", "-o", "tsv").Trim();
        if (string.IsNullOrWhiteSpace(principal))
            throw new CommandLineException("Cannot resolve user object ID; provide --principal-id.");

        var state = new LabState
        {
            Lab = labName,
            Subscription = subscription,
            ResourceGroup = group,
            Location = options.Location,
            AccountName = accountName,
            Status = options.PrepareOnly ? "prepared" : "running",
            Stage = options.PrepareOnly ? "prepared" : "local-tests",
            Outputs = previous is not null && !options.StartFresh ? previous.Outputs : [],
        };
        SaveState(statePath, state);
        Console.WriteLine($"Lab lifecycle: {group} / {accountName} ({subscription}, {options.Location})");
        return new DeploymentPreparation(lab, statePath, options.Prefix, group, name,
            principal, subscription, options.Location, accountName, options.PrepareOnly, state);
    }

    public static async Task<int> DeployMainAsync(string[] args, string labDirectory)
    {
        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            PrintDeployHelp();
            return 0;
        }
        var lab = Path.GetFullPath(labDirectory);
        var statePath = StatePath(lab);
        var stateDirectory = Path.GetDirectoryName(statePath)!;
        var azure = new ProcessAzureCli();
        DeploymentPreparation preparation;
        try
        {
            preparation = Prepare(args, lab, statePath, azure);
        }
        catch (CommandLineException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return 1;
        }

        if (preparation.PrepareOnly)
        {
            Console.WriteLine($"Saved {statePath}. No Azure resources were created. Reuse accountName for manual deployment.");
            return 0;
        }

        var state = preparation.State;
        try
        {
            Console.WriteLine($"Billable Azure deployment: {state.Lab} -> {state.ResourceGroup} ({state.Subscription})");
            Directory.CreateDirectory(stateDirectory);
            var testLog = Path.Combine(stateDirectory, "lab-tests.log");
            var testOutput = "";
            try
            {
                testOutput += await RunChildAsync("dotnet", ["run", "--file", "test_demo.cs"], lab);
                testOutput += await RunChildAsync("dotnet", ["run", "--file", "test_deploy_lab.cs"], lab);
            }
            catch (Exception exception)
            {
                testOutput += exception.Message;
                await File.WriteAllTextAsync(testLog, testOutput);
                Console.Write(testOutput);
                throw;
            }
            await File.WriteAllTextAsync(testLog, testOutput);
            Console.Write(testOutput);

            var template = Path.Combine(lab, "infra", "main.bicep");
            using var compiled = JsonDocument.Parse(azure.Run("bicep", "build", "--file", template, "--stdout"));
            azure.Run("bicep", "lint", "--file", template);
            var providers = compiled.RootElement.GetProperty("resources").EnumerateArray()
                .Select(resource => resource.GetProperty("type").GetString() ?? "")
                .Where(type => !type.StartsWith("Microsoft.Authorization/", StringComparison.Ordinal))
                .Select(type => type.Split('/')[0]).Distinct().Order(StringComparer.Ordinal);
            foreach (var provider in providers)
                azure.Run("provider", "register", "--namespace", provider, "--wait",
                    "--subscription", state.Subscription);

            var tags = new Dictionary<string, string>
            {
                ["managedBy"] = "foundry-lab-script",
                ["lab"] = state.Lab,
            };
            foreach (var pair in SecurityTags)
                tags[pair.Key] = pair.Value;
            azure.Run([
                "group", "create", "--name", state.ResourceGroup, "--location", state.Location,
                "--tags", .. tags.Select(pair => $"{pair.Key}={pair.Value}"),
                "--subscription", state.Subscription, "-o", "none",
            ]);

            state.Stage = "deployment";
            var outputText = azure.Run("deployment", "group", "create",
                "--resource-group", state.ResourceGroup, "--name", preparation.Name,
                "--subscription", state.Subscription, "--template-file", template, "--parameters",
                $"principalId={preparation.PrincipalId}", $"location={state.Location}",
                $"accountName={state.AccountName}", "--query", "properties.outputs", "-o", "json");
            using (var output = JsonDocument.Parse(outputText))
                state.Outputs = output.RootElement.EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.Clone());
            state.Stage = "live-demo";
            SaveState(statePath, state);
            await RunLiveAsync(lab, state);
            state.Status = "passed";
            state.Stage = "complete";
            Console.WriteLine($"PASS: {state.Lab}. Resources retained. Results: {statePath}");
            return 0;
        }
        catch (Exception exception)
        {
            state.Status = "failed";
            state.Error = exception.Message;
            Console.Error.WriteLine(
                $"ERROR: {exception.Message}\nResources are retained; inspect {statePath} and run cleanup.cs.");
            return 1;
        }
        finally
        {
            SaveState(statePath, state);
        }
    }

    public static int CleanupMain(string[] args, string labDirectory)
    {
        var help = args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal);
        if (help)
        {
            PrintCleanupHelp();
            return 0;
        }
        if (args.Any(argument => argument != "--yes"))
        {
            Console.Error.WriteLine("error: Unrecognized cleanup option.");
            return 2;
        }
        var lab = Path.GetFullPath(labDirectory);
        return Cleanup(args, lab, StatePath(lab), new ProcessAzureCli(), ReadConfirmation);
    }

    public static int Cleanup(
        string[] args, string labDirectory, string statePath, IAzureCli azure,
        Func<string, bool>? confirm = null)
    {
        try
        {
            if (args.Any(argument => argument != "--yes"))
                throw new CommandLineException("Unrecognized cleanup option.");
            if (!File.Exists(statePath))
                throw new CommandLineException("No lab deployment record. Nothing will be deleted; run deploy_lab.cs first.");
            var lab = Path.GetFullPath(labDirectory);
            var labName = Path.GetFileName(lab);
            var state = ReadState(statePath);
            ValidateState(state, labName, Regex.Replace(labName, @"^\d+-", "") + "-demo");
            var scope = new[] { "--subscription", state.Subscription };
            var exists = azure.Run(["group", "exists", "--name", state.ResourceGroup, .. scope]).Trim();
            if (exists is not ("true" or "false"))
                throw new InvalidOperationException($"Unexpected group existence response: {exists}");
            if (exists == "true")
            {
                using var tags = JsonDocument.Parse(azure.Run([
                    "group", "show", "--name", state.ResourceGroup, .. scope, "--query", "tags", "-o", "json",
                ]));
                if (tags.RootElement.ValueKind != JsonValueKind.Object ||
                    !HasTag(tags.RootElement, "lab", labName) ||
                    !HasTag(tags.RootElement, "managedBy", "foundry-lab-script"))
                    throw new CommandLineException("Group ownership tags do not match this lab; refusing deletion.");
                Console.WriteLine($"Delete ALL resources in {state.ResourceGroup}, subscription {state.Subscription}.");
                if (!args.Contains("--yes", StringComparer.Ordinal) &&
                    !(confirm?.Invoke(state.ResourceGroup) ?? false))
                    throw new CommandLineException("Confirmation did not match. Nothing was deleted.");
                azure.Run(["group", "delete", "--name", state.ResourceGroup, .. scope, "--yes"]);
                if (azure.Run(["group", "exists", "--name", state.ResourceGroup, .. scope]).Trim() != "false")
                    throw new InvalidOperationException("Azure still reports the group exists; cleanup is not complete.");
            }
            state.CleanupStatus = "deleted";
            state.CleanupError = null;
            SaveState(statePath, state);
            Console.WriteLine($"Verified absent: {state.ResourceGroup}. Local logs and results retained in {Path.GetDirectoryName(statePath)}.");
            return 0;
        }
        catch (Exception exception)
        {
            if (File.Exists(statePath))
            {
                try
                {
                    var state = ReadState(statePath);
                    state.CleanupStatus = "failed";
                    state.CleanupError = exception.Message;
                    SaveState(statePath, state);
                }
                catch { }
            }
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return exception is CommandLineException ? 2 : 1;
        }
    }

    public static LabState ReadState(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<LabState>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException("Deployment record is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Invalid lab deployment record: {exception.Message}", exception);
        }
    }

    public static void SaveState(string path, LabState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions) + Environment.NewLine);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static bool IsValidPrefix(string prefix) =>
        Regex.IsMatch(prefix, @"^[a-zA-Z0-9_-]{1,50}$");

    private static async Task<string> RunChildAsync(string executable, string[] arguments, string workingDirectory)
    {
        Console.WriteLine($"\n> {executable} {string.Join(" ", arguments)}");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} {string.Join(" ", arguments)} failed: {stdout}{stderr}");
        return stdout + stderr;
    }

    private static async Task RunLiveAsync(string lab, LabState state)
    {
        var log = Path.Combine(lab, ".azure", "demo.log");
        var environment = new Dictionary<string, string?>
        {
            ["FOUNDRY_PROJECT_ENDPOINT"] = GetOutput(state, "projectEndpoint"),
            ["AZURE_OPENAI_DEPLOYMENT"] = GetOutput(state, "deployment"),
        };
        for (var attempt = 0; attempt < 11; attempt++)
        {
            Console.WriteLine($"\nRunning live demo: {state.Lab} (attempt {attempt + 1}/11)");
            var output = await RunLiveAttemptAsync(lab, environment);
            Console.Write(output.Text);
            await File.AppendAllTextAsync(log, $"\n--- Attempt {attempt + 1} ---\n{output.Text}");
            if (output.ExitCode == 0)
                return;
            var dns = output.Text.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase) ||
                      output.Text.Contains("Temporary failure in name resolution", StringComparison.OrdinalIgnoreCase) ||
                      output.Text.Contains("nodename nor servname provided", StringComparison.OrdinalIgnoreCase) ||
                      output.Text.Contains("getaddrinfo failed", StringComparison.OrdinalIgnoreCase);
            var propagating = output.Text.Contains("403", StringComparison.Ordinal) ||
                              output.Text.Contains("404", StringComparison.Ordinal);
            if ((!dns && !propagating) || attempt == 10)
                throw new InvalidOperationException($"Live demo {state.Lab} failed; see {log}.");
            Console.WriteLine($"{(dns ? "DNS lookup failed" : "Azure access/deployment is not ready")}. Retrying in 30 seconds.");
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    private static async Task<(int ExitCode, string Text)> RunLiveAttemptAsync(
        string lab, Dictionary<string, string?> environment)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = lab,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "run", "--file", "demo.cs", "--", "--pattern", "all" })
            process.StartInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in environment)
            process.StartInfo.Environment[key] = value;
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string GetOutput(LabState state, string name)
    {
        if (!state.Outputs.TryGetValue(name, out var value) ||
            !value.TryGetProperty("value", out var output) ||
            output.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(output.GetString()))
            throw new InvalidOperationException($"Bicep must return a nonempty '{name}' output.");
        return output.GetString()!;
    }

    private static bool HasTag(JsonElement tags, string key, string expected) =>
        tags.TryGetProperty(key, out var tag) && tag.ValueKind == JsonValueKind.String &&
        tag.GetString() == expected;

    private static void ValidateState(LabState state, string labName, string name)
    {
        if (state.Lab != labName || string.IsNullOrWhiteSpace(state.Subscription) ||
            !Regex.IsMatch(state.ResourceGroup, @"^[a-zA-Z0-9_-]{1,50}-" + Regex.Escape(name) + "$"))
            throw new CommandLineException("Invalid or mismatched lab deployment record; refusing to overwrite or delete it.");
    }

    private static DeployOptions ParseDeployArguments(string[] args)
    {
        var subscription = "";
        var location = "eastus2";
        var prefix = "rg-foundry-student";
        var principalId = "";
        var startFresh = false;
        var prepareOnly = false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--subscription":
                case "--location":
                case "--prefix":
                case "--principal-id":
                    if (++index >= args.Length)
                        throw new CommandLineException($"Missing value for {arg}.");
                    if (arg == "--subscription") subscription = args[index];
                    else if (arg == "--location") location = args[index];
                    else if (arg == "--prefix") prefix = args[index];
                    else principalId = args[index];
                    break;
                case "--start-fresh": startFresh = true; break;
                case "--prepare-only": prepareOnly = true; break;
                default: throw new CommandLineException($"Unrecognized option '{arg}'.");
            }
        }
        if (!IsValidPrefix(prefix))
            throw new CommandLineException("--prefix must contain 1-50 letters, numbers, underscores, or hyphens.");
        if (string.IsNullOrWhiteSpace(location))
            throw new CommandLineException("--location must not be empty.");
        return new DeployOptions(subscription, location, prefix, principalId, startFresh, prepareOnly);
    }

    private static bool ReadConfirmation(string group)
    {
        Console.Write("Type the full resource group name to confirm: ");
        return Console.ReadLine()?.Trim() == group;
    }

    private static void PrintDeployHelp()
    {
        Console.WriteLine("Deploy and test only this lab; leave its resources and results available.");
        Console.WriteLine("Usage: dotnet run --file deploy_lab.cs -- [--subscription ID] [--location eastus2]");
        Console.WriteLine("Options: --prefix PREFIX --principal-id ID --start-fresh --prepare-only --help");
        Console.WriteLine("Requires Azure CLI/Bicep and az login as a user. Deployment creates billable resources.");
        Console.WriteLine("After presenting, run dotnet run --file cleanup.cs to delete the recorded resource group.");
    }

    private static void PrintCleanupHelp()
    {
        Console.WriteLine("Delete only this lab's recorded resource group; keep local results.");
        Console.WriteLine("Usage: dotnet run --file cleanup.cs [--yes]");
        Console.WriteLine("Uses .azure/lab-deployment.json and deletes ALL resources in the dedicated group.");
    }

    private sealed record DeployOptions(
        string Subscription, string Location, string Prefix, string PrincipalId,
        bool StartFresh, bool PrepareOnly);
}

public sealed class CommandLineException(string message) : Exception(message);
