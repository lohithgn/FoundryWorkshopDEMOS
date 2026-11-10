#!/usr/bin/env dotnet
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

#if !RUNNER_TESTS
return DeployRunner.Execute(args);
#endif

internal sealed record RunnerOptions(
    string? Subscription,
    string Location,
    string? SearchLocation,
    string Prefix,
    string? PrincipalId,
    IReadOnlyList<string> Demos);

internal sealed class RunnerArgumentException(string message) : Exception(message);

internal sealed class CommandFailureException(string message) : Exception(message);

internal static class DeployRunner
{
    internal static readonly Dictionary<string, string> SecurityTags = new()
    {
        ["SecurityControl"] = "Ignore",
        ["SecurityExemption"] = "StorageAccountKeyAccess",
    };

    private static readonly (string Output, string Variable)[] OptionalOutputs =
    [
        ("deployment", "AZURE_OPENAI_DEPLOYMENT"),
        ("reasoningDeployment", "AZURE_OPENAI_REASONING_DEPLOYMENT"),
        ("batchDeployment", "AZURE_OPENAI_BATCH_DEPLOYMENT"),
        ("embeddingDeployment", "AZURE_OPENAI_EMBEDDING_DEPLOYMENT"),
        ("projectEndpoint", "FOUNDRY_PROJECT_ENDPOINT"),
        ("toolEndpoint", "WORKSHOP_TOOL_ENDPOINT"),
        ("searchEndpoint", "AZURE_SEARCH_ENDPOINT"),
        ("agentIdentityId", "ENTRA_AGENT_ID"),
        ("contentSafetyEndpoint", "CONTENT_SAFETY_ENDPOINT"),
        ("deniedEndpoint", "WORKSHOP_DENIED_ENDPOINT"),
        ("alternateDeployment", "AZURE_OPENAI_ALTERNATE_DEPLOYMENT"),
        ("routerDeployment", "AZURE_OPENAI_ROUTER_DEPLOYMENT"),
        ("applicationInsightsConnectionString", "APPLICATIONINSIGHTS_CONNECTION_STRING"),
        ("projectResourceId", "FOUNDRY_PROJECT_RESOURCE_ID"),
        ("tenantId", "AZURE_TENANT_ID"),
        ("searchResourceId", "AZURE_SEARCH_RESOURCE_ID"),
        ("storageEndpoint", "AZURE_STORAGE_BLOB_ENDPOINT"),
        ("storageResourceId", "AZURE_STORAGE_RESOURCE_ID"),
    ];

    internal static int Execute(string[] argv)
    {
        try
        {
            if (argv.Contains("--help") || argv.Contains("-h"))
            {
                PrintUsage();
                return 0;
            }

            var options = ParseArguments(argv);
            RunWorkshop(options, Directory.GetCurrentDirectory());
            return 0;
        }
        catch (RunnerArgumentException error)
        {
            Console.Error.WriteLine($"deploy.cs: error: {error.Message}");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"\nERROR: {error.Message}");
            return 1;
        }
    }

    internal static RunnerOptions ParseArguments(IReadOnlyList<string> argv)
    {
        string? subscription = null;
        var location = "eastus2";
        string? searchLocation = null;
        var prefix = "rg-foundry";
        string? principalId = null;
        var demos = new List<string>();
        var valueOptions = new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--subscription"] = value => subscription = value,
            ["--location"] = value => location = value,
            ["--search-location"] = value => searchLocation = value,
            ["--prefix"] = value => prefix = value,
            ["--principal-id"] = value => principalId = value,
            ["--demo"] = demos.Add,
        };

        for (var index = 0; index < argv.Count; index++)
        {
            var argument = argv[index];
            var separator = argument.IndexOf('=');
            var name = separator < 0 ? argument : argument[..separator];
            if (!valueOptions.TryGetValue(name, out var assign))
            {
                throw new RunnerArgumentException($"unrecognized argument: {argument}");
            }

            string value;
            if (separator >= 0)
            {
                value = argument[(separator + 1)..];
            }
            else if (index + 1 < argv.Count && !argv[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = argv[++index];
            }
            else
            {
                throw new RunnerArgumentException($"argument {name}: expected one argument");
            }

            assign(value);
        }

        if (!Regex.IsMatch(prefix, @"\A[a-zA-Z0-9_-]{1,50}\z", RegexOptions.CultureInvariant))
        {
            throw new RunnerArgumentException("--prefix must contain 1-50 letters, numbers, underscores, or hyphens.");
        }

        return new RunnerOptions(subscription, location, searchLocation, prefix, principalId, demos);
    }

    internal static List<string> UsageLines() =>
    [
        "Usage: dotnet run --file deploy.cs -- [--subscription SUBSCRIPTION]",
        "       [--location LOCATION] [--search-location SEARCH_LOCATION]",
        "       [--prefix PREFIX] [--principal-id PRINCIPAL_ID]",
        "       [--demo NUMBER_OR_FOLDER]...",
    ];

    private static void PrintUsage()
    {
        foreach (var line in UsageLines())
        {
            Console.WriteLine(line);
        }
    }

    internal static List<DirectoryInfo> Discover(string root)
    {
        var demos = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Where(path => File.Exists(Path.Combine(path, "infra", "main.bicep")))
                .Select(path => new DirectoryInfo(path))
                .OrderBy(DemoNumber)
                .ThenBy(demo => demo.Name, StringComparer.Ordinal)
                .ToList()
            : [];
        if (demos.Count == 0)
        {
            throw new InvalidOperationException($"No demo templates found under {root}.");
        }

        return demos;
    }

    internal static int DemoNumber(DirectoryInfo demo)
    {
        var match = Regex.Match(demo.Name, @"\A\d+");
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : int.MaxValue;
    }

    internal static List<DirectoryInfo> SelectDemos(
        IReadOnlyList<DirectoryInfo> demos,
        IReadOnlyList<string> selectors)
    {
        if (selectors.Count == 0)
        {
            return [.. demos];
        }

        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selector in selectors)
        {
            var matches = demos.Where(demo =>
                demo.Name == selector ||
                demo.Name.Split('-', 2)[0] == selector).ToList();
            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    $"--demo '{selector}' must identify exactly one demo number or folder.");
            }

            selected.Add(matches[0].FullName);
        }

        return demos.Where(demo => selected.Contains(demo.FullName)).ToList();
    }

    internal static void ValidateEntrypoints(IEnumerable<DirectoryInfo> demos)
    {
        foreach (var demo in demos)
        {
            foreach (var required in new[] { "demo.cs", "test_demo.cs" })
            {
                if (!File.Exists(Path.Combine(demo.FullName, required)))
                {
                    throw new InvalidOperationException($"{demo.Name} is missing {required}.");
                }
            }
        }
    }

    internal static Dictionary<string, string> DemoEnvironment(
        JsonElement outputs,
        IReadOnlyDictionary<string, string> baseEnvironment)
    {
        var values = new Dictionary<string, string>(baseEnvironment, StringComparer.Ordinal);
        values.Remove("AZURE_OPENAI_DEPLOYMENT");
        var required = GetOutputValue(outputs, "endpoint", required: true);
        values["AZURE_OPENAI_ENDPOINT"] = required!;

        foreach (var (output, variable) in OptionalOutputs)
        {
            if (outputs.ValueKind == JsonValueKind.Object && outputs.TryGetProperty(output, out _))
            {
                values[variable] = GetOutputValue(outputs, output, required: true)!;
            }
        }

        return values;
    }

    private static string? GetOutputValue(JsonElement outputs, string output, bool required)
    {
        if (outputs.ValueKind == JsonValueKind.Object &&
            outputs.TryGetProperty(output, out var item) &&
            item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        if (required)
        {
            var message = output == "endpoint"
                ? $"Bicep must return a nonempty '{output}' output."
                : $"Bicep output '{output}' must be a nonempty string.";
            throw new InvalidOperationException(message);
        }

        return null;
    }

    internal static Dictionary<string, string> ValidateIdentity(
        IReadOnlyList<DirectoryInfo> demos,
        IReadOnlyDictionary<string, string> environment)
    {
        if (!demos.Any(demo => demo.Name == "12-entra-agent-id"))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var identityVariables = new[]
        {
            "ENTRA_TENANT_ID", "ENTRA_BLUEPRINT_CLIENT_ID", "ENTRA_AGENT_ID", "ENTRA_BLUEPRINT_SECRET",
        };
        if (identityVariables.Any(name =>
                !environment.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)))
        {
            throw new RunnerArgumentException(
                "Demo 12 requires setup_identity.ps1 first: set " + string.Join(", ", identityVariables));
        }

        foreach (var name in identityVariables.Take(3))
        {
            if (!Guid.TryParse(environment[name], out _))
            {
                throw new RunnerArgumentException("Demo 12 tenant, blueprint, and agent IDs must be UUIDs.");
            }
        }

        if (environment["ENTRA_BLUEPRINT_CLIENT_ID"] == environment["ENTRA_AGENT_ID"])
        {
            throw new RunnerArgumentException("Demo 12 requires a child agent ID, not the blueprint client ID.");
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["12-entra-agent-id"] = $"agentIdentityId={environment["ENTRA_AGENT_ID"]}",
        };
    }

    internal static void ValidateLocation(string demoName, string requested, JsonElement template)
    {
        if (!template.TryGetProperty("parameters", out var parameters) ||
            !parameters.TryGetProperty("location", out var location) ||
            !location.TryGetProperty("allowedValues", out var allowed) ||
            allowed.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var locations = allowed.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToList();
        if (locations.Count > 0 && !locations.Contains(requested, StringComparer.Ordinal))
        {
            throw new RunnerArgumentException(
                $"{demoName} does not support --location '{requested}'; choose one of: {string.Join(", ", locations)}.");
        }
    }

    internal static bool IsDnsFailure(string output)
    {
        var serviceError = output.Contains("openai.APIConnectionError:", StringComparison.Ordinal) ||
                           output.Contains("azure.core.exceptions.ServiceRequestError:", StringComparison.Ordinal);
        return serviceError && new[]
        {
            "[Errno 8] nodename nor servname provided, or not known",
            "[Errno -2] Name or service not known",
            "[Errno -3] Temporary failure in name resolution",
            "[Errno 11001] getaddrinfo failed",
        }.Any(output.Contains);
    }

    internal static bool IsPropagatingFailure(string output) =>
        output.Contains("openai.PermissionDeniedError: Error code: 403", StringComparison.Ordinal) ||
        (output.Contains("azure.core.exceptions.HttpResponseError", StringComparison.Ordinal) &&
         (output.Contains("(PermissionDenied)", StringComparison.Ordinal) ||
          output.Contains("(Forbidden)", StringComparison.Ordinal))) ||
        (output.Contains("openai.NotFoundError: Error code: 404", StringComparison.Ordinal) &&
         output.Contains("DeploymentNotFound", StringComparison.Ordinal));

    internal delegate string CommandInvoker(
        IReadOnlyList<string> command,
        string cwd,
        bool capture,
        IReadOnlyDictionary<string, string>? environment);

    internal static void RunWorkshop(
        RunnerOptions options,
        string root,
        CommandInvoker? runCommand = null,
        Action<DirectoryInfo, IReadOnlyDictionary<string, string>>? runLive = null)
    {
        var execute = runCommand ?? Run;
        List<DirectoryInfo> demos;
        try
        {
            var discovered = Discover(root);
            ValidateEntrypoints(discovered);
            demos = SelectDemos(discovered, options.Demos);
        }
        catch (InvalidOperationException error)
        {
            throw new RunnerArgumentException(error.Message);
        }

        var extraParameters = ValidateIdentity(demos, GetEnvironment());

        var accountCommand = new List<string> { "az", "account", "show", "-o", "json" };
        if (!string.IsNullOrEmpty(options.Subscription))
        {
            accountCommand.AddRange(["--subscription", options.Subscription]);
        }

        using var accountDocument = JsonDocument.Parse(execute(accountCommand, root, true, null));
        var account = accountDocument.RootElement;
        var subscription = account.GetProperty("id").GetString()
                           ?? throw new InvalidOperationException("Azure account response has no subscription ID.");
        if (account.GetProperty("user").GetProperty("type").GetString() != "user")
        {
            throw new RunnerArgumentException("Sign in with az login as a user; the Bicep role assignment requires a User.");
        }

        var principal = options.PrincipalId;
        if (string.IsNullOrWhiteSpace(principal))
        {
            principal = execute(
                ["az", "ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"],
                root,
                true,
                null).Trim();
        }

        if (string.IsNullOrWhiteSpace(principal))
        {
            throw new RunnerArgumentException("Could not resolve the presenter object ID; provide --principal-id.");
        }

        Console.WriteLine($"Subscription: {account.GetProperty("name").GetString()} ({subscription})");
        Console.WriteLine("This deploys billable Azure resources and sends live model requests.");

        var providers = new SortedSet<string>(StringComparer.Ordinal);
        var extraParametersByDemo = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var searchLocationApplied = false;
        foreach (var demo in demos)
        {
            using var templateDocument = JsonDocument.Parse(execute(
                ["az", "bicep", "build", "--file", Path.Combine(demo.FullName, "infra/main.bicep"), "--stdout"],
                root,
                true,
                null));
            var template = templateDocument.RootElement;
            ValidateLocation(demo.Name, options.Location, template);
            if (template.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
            {
                foreach (var resource in resources.EnumerateArray())
                {
                    if (!resource.TryGetProperty("type", out var type) ||
                        type.ValueKind != JsonValueKind.String ||
                        type.GetString()!.StartsWith("Microsoft.Authorization/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    providers.Add(type.GetString()!.Split('/')[0]);
                }
            }

            if (options.SearchLocation is not null &&
                template.TryGetProperty("parameters", out var parameters) &&
                parameters.TryGetProperty("searchLocation", out _))
            {
                extraParametersByDemo.GetOrAdd(demo.Name).Add($"searchLocation={options.SearchLocation}");
                searchLocationApplied = true;
            }

            execute(["az", "bicep", "lint", "--file", Path.Combine(demo.FullName, "infra/main.bicep")],
                root, false, null);
            foreach (var test in Directory.GetFiles(demo.FullName, "test_*.cs", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                RunFileApp(test, [], demo.FullName, execute: execute);
            }
            if (demo.Name is "21-permission-aware-retrieval" or "31-native-agent-memory" or "32-browser-runbook")
            {
                RunFileApp(Path.Combine(demo.FullName, "demo.cs"), ["--preflight"], demo.FullName, execute: execute);
            }

            if (demo.Name is "40-voice-agent" or "41-content-understanding")
            {
                RunFileApp(Path.Combine(demo.FullName, "demo.cs"),
                    ["--preflight", "--location", options.Location], demo.FullName, execute: execute);
            }
        }

        if (options.SearchLocation is not null && !searchLocationApplied)
        {
            throw new RunnerArgumentException("--search-location requires a selected template exposing searchLocation.");
        }

        foreach (var provider in providers)
        {
            execute([
                "az", "provider", "register", "--namespace", provider,
                "--wait", "--subscription", subscription,
            ], root, false, null);
        }

        foreach (var demo in demos)
        {
            var demoName = Regex.Replace(demo.Name, @"\A\d+-", string.Empty);
            var deploymentName = $"{demoName}-demo";
            var group = $"{options.Prefix}-{deploymentName}";
            var groupCommand = new List<string>
            {
                "az", "group", "create", "--name", group, "--location", options.Location, "--tags",
            };
            groupCommand.AddRange(SecurityTags.Select(tag => $"{tag.Key}={tag.Value}"));
            groupCommand.AddRange(["--subscription", subscription, "-o", "none"]);
            execute(groupCommand, root, false, null);

            var state = Path.Combine(demo.FullName, ".azure");
            Directory.CreateDirectory(state);
            var deploymentCommand = new List<string>
            {
                "az", "deployment", "group", "create", "--resource-group", group,
                "--name", deploymentName, "--subscription", subscription,
                "--template-file", Path.Combine(demo.FullName, "infra/main.bicep"),
                "--parameters", $"principalId={principal}", $"location={options.Location}",
            };
            if (extraParameters.TryGetValue(demo.Name, out var identityParameter))
            {
                deploymentCommand.Add(identityParameter);
            }

            if (extraParametersByDemo.TryGetValue(demo.Name, out var demoParameters))
            {
                deploymentCommand.AddRange(demoParameters);
            }

            deploymentCommand.AddRange(["--query", "properties.outputs", "-o", "json"]);
            using var outputsDocument = JsonDocument.Parse(execute(deploymentCommand, root, true, null));
            var outputs = outputsDocument.RootElement.Clone();
            using (var stream = File.Create(Path.Combine(state, "deployment.json")))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("subscription", subscription);
                writer.WriteString("resourceGroup", group);
                writer.WritePropertyName("outputs");
                outputs.WriteTo(writer);
                writer.WriteEndObject();
                writer.Flush();
                stream.WriteByte((byte)'\n');
            }
            var environment = DemoEnvironment(outputs, GetEnvironment());

            if (demo.Name is "42-managed-harness-tool-search" or "43-agent-routines" or "44-evaluation-optimizer")
            {
                RunFileApp(Path.Combine(demo.FullName, "demo.cs"), ["--preflight"], demo.FullName,
                    environment, execute);
            }

            (runLive ?? ((liveDemo, liveEnvironment) => RunLive(liveDemo, liveEnvironment)))(demo, environment);
            Console.WriteLine($"\nPASS: {demo.Name}. Resource group: {group}.");
        }

        Console.WriteLine("\nAll demo deployments, local tests, and live runs passed.");
    }

    private static Dictionary<string, string> GetEnvironment() =>
        Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string && entry.Value is string)
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);

    private static List<TValue> GetOrAdd<TKey, TValue>(
        this Dictionary<TKey, List<TValue>> dictionary,
        TKey key) where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out var list))
        {
            list = [];
            dictionary.Add(key, list);
        }

        return list;
    }

    private static string Run(
        IReadOnlyList<string> command,
        string cwd,
        bool capture = false,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Console.WriteLine($"\n> {string.Join(" ", command.Select(ShellQuote))}");
        var startInfo = CreateStartInfo(command, cwd, environment);
        startInfo.RedirectStandardOutput = capture;
        using var process = Process.Start(startInfo)
                           ?? throw new InvalidOperationException($"Could not start {command[0]}.");
        var output = capture ? process.StandardOutput.ReadToEnd() : string.Empty;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new CommandFailureException(
                $"Command '{string.Join(" ", command)}' returned non-zero exit status {process.ExitCode}.");
        }

        return output;
    }

    private static void RunFileApp(
        string source,
        IReadOnlyList<string> arguments,
        string cwd,
        IReadOnlyDictionary<string, string>? environment = null,
        CommandInvoker? execute = null)
    {
        var command = new List<string> { "dotnet", "run", "--file", source, "--" };
        command.AddRange(arguments);
        (execute ?? Run)(command, cwd, false, environment);
    }

    private static ProcessStartInfo CreateStartInfo(
        IReadOnlyList<string> command,
        string cwd,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            WorkingDirectory = cwd,
            UseShellExecute = false,
        };
        foreach (var argument in command.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        return startInfo;
    }

    private static string ShellQuote(string value) =>
        value.Length > 0 && value.All(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or ':')
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal static void RunLive(
        DirectoryInfo demo,
        IReadOnlyDictionary<string, string> environment,
        Func<IReadOnlyList<string>, string, IReadOnlyDictionary<string, string>, (int ExitCode, string Output)>? runCommand = null,
        Action<TimeSpan>? sleep = null)
    {
        var log = Path.Combine(demo.FullName, ".azure", "demo.log");
        var execute = runCommand ?? ((command, cwd, env) => RunCapturing(command, cwd, env));
        for (var attempt = 0; attempt < 11; attempt++)
        {
            Console.WriteLine($"\nRunning live demo: {demo.Name} (attempt {attempt + 1}/11)");
            var command = new[] { "dotnet", "run", "--file", Path.Combine(demo.FullName, "demo.cs"), "--" };
            var result = execute(command, demo.FullName, environment);
            Console.Write(result.Output);
            using (var stream = new StreamWriter(log, append: true))
            {
                stream.Write($"\n--- Attempt {attempt + 1} ---\n{result.Output}");
            }

            if (result.ExitCode == 0)
            {
                return;
            }

            var dnsFailure = IsDnsFailure(result.Output);
            var propagating = IsPropagatingFailure(result.Output);
            if ((!propagating && !dnsFailure) || attempt == 10)
            {
                if (dnsFailure)
                {
                    throw new InvalidOperationException(
                        $"DNS resolution failed for live demo {demo.Name} after {attempt + 1} attempts. " +
                        $"Check the endpoints in {Path.Combine(demo.FullName, ".azure", "deployment.json")} " +
                        $"and local DNS/VPN settings; see {log}.");
                }

                throw new InvalidOperationException($"Live demo {demo.Name} failed; see {log}.");
            }

            Console.WriteLine(
                $"{(dnsFailure ? "DNS lookup failed; the endpoint or local DNS may not be ready." : "Azure access/deployment is not ready.")} " +
                "Retrying in 30 seconds.");
            (sleep ?? Thread.Sleep)(TimeSpan.FromSeconds(30));
        }
    }

    private static (int ExitCode, string Output) RunCapturing(
        IReadOnlyList<string> command,
        string cwd,
        IReadOnlyDictionary<string, string> environment)
    {
        Console.WriteLine($"\n> {string.Join(" ", command.Select(ShellQuote))}");
        var startInfo = CreateStartInfo(command, cwd, environment);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var process = Process.Start(startInfo)
                           ?? throw new InvalidOperationException($"Could not start {command[0]}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        return (process.ExitCode, stdoutTask.Result + stderrTask.Result);
    }
}
