#!/usr/bin/env dotnet
#:property DefineConstants=RUNNER_TESTS
#:include deploy.cs

using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

var failures = new List<string>();
var passed = 0;
var root = Path.GetDirectoryName(Path.GetFullPath(TestSourcePath()))!;

string TestSourcePath([CallerFilePath] string sourceFile = "") => sourceFile;

void Check(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
        passed++;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {name}: {error.Message}");
        failures.Add(name);
    }
}

void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}. Expected '{expected}', got '{actual}'.");
    }
}

void Throws<T>(Action action, string messageFragment) where T : Exception
{
    try
    {
        action();
    }
    catch (T error) when (error.Message.Contains(messageFragment, StringComparison.Ordinal))
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name} containing '{messageFragment}'.");
}

Check("security tag values", () =>
{
    Equal("Ignore", DeployRunner.SecurityTags["SecurityControl"], "SecurityControl");
    Equal("StorageAccountKeyAccess", DeployRunner.SecurityTags["SecurityExemption"], "SecurityExemption");
});

Check("all taggable infrastructure resources have security tags", () =>
{
    var unsupported = new HashSet<string>(StringComparer.Ordinal)
    {
        "Microsoft.Authorization/roleAssignments",
        "Microsoft.Authorization/policyAssignments",
        "Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials",
        "Microsoft.Web/sites/functions",
        "Microsoft.Storage/storageAccounts/blobServices",
        "Microsoft.Storage/storageAccounts/blobServices/containers",
        "Microsoft.Storage/storageAccounts/managementPolicies",
        "Microsoft.Cache/redisEnterprise/databases",
        "Microsoft.ApiManagement/service/caches",
        "Microsoft.ApiManagement/service/backends",
        "Microsoft.ApiManagement/service/apis",
        "Microsoft.ApiManagement/service/apis/policies",
        "Microsoft.ApiManagement/service/apis/operations",
        "Microsoft.ApiManagement/service/apis/operations/policies",
        "Microsoft.CognitiveServices/accounts/projects/connections",
    };
    var templates = Directory.GetFiles(root, "*.bicep", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) == "main.bicep" &&
                       path.Contains($"{Path.DirectorySeparatorChar}infra{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToList();
    Assert(templates.Count > 0, "No Bicep templates found.");
    foreach (var template in templates)
    {
        var source = File.ReadAllText(template);
        var tags = Regex.Match(source, @"(?m)^var securityTags = \{([^}]+)\}");
        Assert(tags.Success, $"{template} has no securityTags variable.");
        var entries = Regex.Matches(tags.Groups[1].Value, @"(\w+): '([^']+)'")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
        Equal(DeployRunner.SecurityTags.Count, entries.Count, $"{template} tag count");
        foreach (var (key, value) in DeployRunner.SecurityTags)
        {
            Equal(value, entries[key], $"{template} {key}");
        }

        var resources = Regex.Matches(source,
            @"(?m)^resource \w+ '([^'@]+)@[^']+'( existing)? = [^\n]+\n");
        Assert(resources.Count > 0, $"{template} has no resources.");
        for (var index = 0; index < resources.Count; index++)
        {
            var resource = resources[index];
            var end = index + 1 < resources.Count ? resources[index + 1].Index : source.Length;
            var body = source[resource.Index..end];
            var tagged = Regex.IsMatch(body, @"(?m)^\s+tags: securityTags\s*$");
            var expected = resource.Groups[2].Length == 0 &&
                           !unsupported.Contains(resource.Groups[1].Value);
            Equal(expected, tagged, $"{template}: {resource.Value.Trim()}");
        }
    }
});

Check("manual resource group commands have security tags", () =>
{
    foreach (var demo in DeployRunner.Discover(root))
    {
        var readme = File.ReadAllText(Path.Combine(demo.FullName, "README.md"));
        var commands = Regex.Matches(readme, @"(?m)^az group create[^\n]*");
        Assert(commands.Count > 0, $"{demo.Name} has no documented group command.");
        foreach (Match command in commands)
        {
            Assert(command.Value.Contains("--tags", StringComparison.Ordinal), $"{demo.Name} command has no --tags.");
            foreach (var (key, value) in DeployRunner.SecurityTags)
            {
                Assert(command.Value.Contains($"{key}={value}", StringComparison.Ordinal),
                    $"{demo.Name} command is missing {key}={value}.");
            }
        }
    }
});

Check("numeric discovery finds all labs without requirements markers", () =>
{
    var demos = DeployRunner.Discover(root);
    Equal(47, demos.Count, "lab count");
    Equal(0, DeployRunner.DemoNumber(demos[0]), "first lab number");
    Equal("0-model-basics", demos[0].Name, "first lab");
    Equal("46-fleet-governance", demos[^1].Name, "last lab");

    using var temporary = new TemporaryDirectory();
    var marked = Path.Combine(temporary.Path, "2-marked", "infra");
    Directory.CreateDirectory(marked);
    File.WriteAllText(Path.Combine(marked, "main.bicep"), string.Empty);
    var requirementsOnly = Path.Combine(temporary.Path, "1-requirements-only");
    Directory.CreateDirectory(requirementsOnly);
    File.WriteAllText(Path.Combine(requirementsOnly, "requirements.txt"), string.Empty);
    var discovered = DeployRunner.Discover(temporary.Path);
    Equal(1, discovered.Count, "requirements-only folders must not mark a lab");
    Equal("2-marked", discovered[0].Name, "lab identified by infrastructure");
});

Check("discovery rejects an empty root and validates file-app entrypoints", () =>
{
    using var temporary = new TemporaryDirectory();
    Throws<InvalidOperationException>(() => DeployRunner.Discover(temporary.Path), "No demo templates");
    var demoPath = Path.Combine(temporary.Path, "0-example");
    var infra = Path.Combine(demoPath, "infra");
    Directory.CreateDirectory(infra);
    File.WriteAllText(Path.Combine(infra, "main.bicep"), string.Empty);
    var demo = DeployRunner.Discover(temporary.Path)[0];
    Throws<InvalidOperationException>(() => DeployRunner.ValidateEntrypoints([demo]), "missing demo.cs");
    File.WriteAllText(Path.Combine(demoPath, "demo.cs"), string.Empty);
    Throws<InvalidOperationException>(() => DeployRunner.ValidateEntrypoints([demo]), "missing test_demo.cs");
    File.WriteAllText(Path.Combine(demoPath, "test_demo.cs"), string.Empty);
    DeployRunner.ValidateEntrypoints([demo]);
});

Check("demo selection supports numbers, exact folders and deduplicates", () =>
{
    var demos = new[]
    {
        new DirectoryInfo("1-first"),
        new DirectoryInfo("12-identity"),
        new DirectoryInfo("14-fundamentals"),
    };
    Equal(3, DeployRunner.SelectDemos(demos, []).Count, "default selection count");
    var selected = DeployRunner.SelectDemos(demos, ["14", "1-first", "14"]);
    Equal(2, selected.Count, "selection should deduplicate");
    Equal("1-first", selected[0].Name, "selection order");
    Equal("14-fundamentals", selected[1].Name, "selection folder");
    Throws<InvalidOperationException>(() => DeployRunner.SelectDemos(demos, ["missing"]), "exactly one demo");
});

Check("file-app CLI preserves defaults and repeatable selectors", () =>
{
    var defaults = DeployRunner.ParseArguments([]);
    Equal("eastus2", defaults.Location, "default location");
    Equal("rg-foundry", defaults.Prefix, "default prefix");
    Equal(0, defaults.Demos.Count, "default demo selection");
    var parsed = DeployRunner.ParseArguments([
        "--subscription", "sub", "--location=westus2", "--search-location", "centralus",
        "--prefix", "workshop_1", "--principal-id", "presenter", "--demo", "0", "--demo", "0-model-basics",
    ]);
    Equal("sub", parsed.Subscription, "subscription");
    Equal("westus2", parsed.Location, "location");
    Equal("centralus", parsed.SearchLocation, "search location");
    Equal("workshop_1", parsed.Prefix, "prefix");
    Equal("presenter", parsed.PrincipalId, "principal");
    Equal(2, parsed.Demos.Count, "repeatable demo selection");
    Throws<RunnerArgumentException>(() => DeployRunner.ParseArguments(["--prefix", "invalid value"]), "--prefix");
    Throws<RunnerArgumentException>(() => DeployRunner.ParseArguments(["--unknown"]), "unrecognized argument");
});

Check("Bicep outputs map to environment with strict validation", () =>
{
    var outputPairs = new (string Output, string Variable)[]
    {
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
    };
    var outputValues = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < outputPairs.Length; index++)
    {
        outputValues[outputPairs[index].Output] = $"value-{index}";
    }

    var outputJson = "{\"endpoint\":{\"value\":\"https://new.example/\"}," +
                     string.Join(",", outputPairs.Select((pair, index) =>
                         $"\"{pair.Output}\":{{\"value\":\"value-{index}\"}}")) + "}";
    using var document = JsonDocument.Parse(outputJson);
    var baseEnvironment = outputPairs.ToDictionary(pair => pair.Variable, _ => "stale", StringComparer.Ordinal);
    baseEnvironment["AZURE_OPENAI_ENDPOINT"] = "stale";
    baseEnvironment["PATH"] = "/bin";
    var mapped = DeployRunner.DemoEnvironment(document.RootElement, baseEnvironment);
    Equal("https://new.example/", mapped["AZURE_OPENAI_ENDPOINT"], "endpoint");
    Equal("/bin", mapped["PATH"], "base environment");
    foreach (var (output, variable) in outputPairs)
    {
        Equal(outputValues[output], mapped[variable], variable);
    }

    using var withoutDeployment = JsonDocument.Parse(
        """{"endpoint":{"value":"https://example/"}}""");
    var cleaned = DeployRunner.DemoEnvironment(withoutDeployment.RootElement,
        new Dictionary<string, string> { ["AZURE_OPENAI_DEPLOYMENT"] = "stale" });
    Assert(!cleaned.ContainsKey("AZURE_OPENAI_DEPLOYMENT"), "stale deployment was not removed");
    using var missing = JsonDocument.Parse("{}");
    Throws<InvalidOperationException>(() => DeployRunner.DemoEnvironment(missing.RootElement, new Dictionary<string, string>()),
        "endpoint");
    using var emptyOptional = JsonDocument.Parse(
        """{"endpoint":{"value":"https://example/"},"deployment":{"value":" "}}""");
    Throws<InvalidOperationException>(
        () => DeployRunner.DemoEnvironment(emptyOptional.RootElement, new Dictionary<string, string>()), "deployment");
});

Check("Demo 12 identity validation is selection-scoped and secret is never a parameter", () =>
{
    var unrelated = DeployRunner.ValidateIdentity(
        [new DirectoryInfo("20-human-approval")], new Dictionary<string, string>());
    Equal(0, unrelated.Count, "unselected identity setup");
    Throws<RunnerArgumentException>(() =>
        DeployRunner.ValidateIdentity([new DirectoryInfo("12-entra-agent-id")], new Dictionary<string, string>()),
        "setup_identity.ps1");
    var environment = new Dictionary<string, string>
    {
        ["ENTRA_TENANT_ID"] = "11111111-1111-1111-1111-111111111111",
        ["ENTRA_BLUEPRINT_CLIENT_ID"] = "22222222-2222-2222-2222-222222222222",
        ["ENTRA_AGENT_ID"] = "33333333-3333-3333-3333-333333333333",
        ["ENTRA_BLUEPRINT_SECRET"] = "test-only-secret",
    };
    var parameters = DeployRunner.ValidateIdentity([new DirectoryInfo("12-entra-agent-id")], environment);
    Equal("agentIdentityId=33333333-3333-3333-3333-333333333333", parameters["12-entra-agent-id"],
        "identity deployment parameter");
    Assert(!string.Join(" ", parameters.Values).Contains("test-only-secret", StringComparison.Ordinal),
        "identity secret was exposed");
    environment["ENTRA_AGENT_ID"] = environment["ENTRA_BLUEPRINT_CLIENT_ID"];
    Throws<RunnerArgumentException>(
        () => DeployRunner.ValidateIdentity([new DirectoryInfo("12-entra-agent-id")], environment), "child agent ID");
});

Check("region validation rejects unsupported locations before deployment", () =>
{
    using var template = JsonDocument.Parse(
        """{"parameters":{"location":{"allowedValues":["eastus"]}}}""");
    Throws<RunnerArgumentException>(
        () => DeployRunner.ValidateLocation("32-browser-runbook", "eastus2", template.RootElement), "does not support");
    DeployRunner.ValidateLocation("32-browser-runbook", "eastus", template.RootElement);
});

Check("live retry classification remains bounded to transient Azure and DNS errors", () =>
{
    Assert(DeployRunner.IsDnsFailure(
        "openai.APIConnectionError: [Errno -3] Temporary failure in name resolution"), "DNS failure not recognized");
    Assert(DeployRunner.IsPropagatingFailure(
        "openai.PermissionDeniedError: Error code: 403"), "permission failure not recognized");
    Assert(DeployRunner.IsPropagatingFailure(
        "openai.NotFoundError: Error code: 404 DeploymentNotFound"), "deployment failure not recognized");
    Assert(!DeployRunner.IsDnsFailure(
        "openai.APIConnectionError: [SSL: CERTIFICATE_VERIFY_FAILED]"), "non-DNS connection error classified as DNS");
    Assert(!DeployRunner.IsPropagatingFailure("Invalid input"), "ordinary error classified as retryable");
});

Check("live retries log each attempt, stop after eleven, and do not retry unrelated errors", () =>
{
    using var temporary = new TemporaryDirectory();
    var demoPath = Path.Combine(temporary.Path, "1-example");
    Directory.CreateDirectory(Path.Combine(demoPath, ".azure"));
    var demo = new DirectoryInfo(demoPath);
    var attempts = 0;
    var sleeps = 0;
    var responses = new Queue<(int ExitCode, string Output)>(
    [
        (1, "openai.PermissionDeniedError: Error code: 403"),
        (0, "Answer: Hello"),
    ]);
    DeployRunner.RunLive(
        demo,
        new Dictionary<string, string>(),
        (_, _, _) =>
        {
            attempts++;
            return responses.Dequeue();
        },
        _ => sleeps++);
    Equal(2, attempts, "retry count");
    Equal(1, sleeps, "retry delay count");
    var transcript = File.ReadAllText(Path.Combine(demoPath, ".azure", "demo.log"));
    Assert(transcript.Contains("--- Attempt 1 ---", StringComparison.Ordinal), "first attempt not logged");
    Assert(transcript.Contains("Answer: Hello", StringComparison.Ordinal), "success not logged");

    attempts = 0;
    sleeps = 0;
    Throws<InvalidOperationException>(() => DeployRunner.RunLive(
        demo,
        new Dictionary<string, string>(),
        (_, _, _) =>
        {
            attempts++;
            return (1, "azure.core.exceptions.ServiceRequestError: [Errno -3] Temporary failure in name resolution");
        },
        _ => sleeps++), "DNS resolution failed");
    Equal(11, attempts, "DNS retry attempt bound");
    Equal(10, sleeps, "DNS retry delay bound");

    attempts = 0;
    sleeps = 0;
    Throws<InvalidOperationException>(() => DeployRunner.RunLive(
        demo,
        new Dictionary<string, string>(),
        (_, _, _) =>
        {
            attempts++;
            return (1, "Invalid input");
        },
        _ => sleeps++), "failed");
    Equal(1, attempts, "non-retryable attempt count");
    Equal(0, sleeps, "non-retryable delay count");
});

Check("catalog, backlog and lab discovery stay aligned", () =>
{
    var demos = DeployRunner.Discover(root);
    var proposals = Regex.Matches(File.ReadAllText(Path.Combine(root, "demos.md")), @"(?m)^(\d+)\. ")
        .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
        .ToList();
    var features = Regex.Matches(File.ReadAllText(Path.Combine(root, "BACKLOG.md")), @"(?m)^## F(\d{3}) - ")
        .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
        .ToList();
    var expected = Enumerable.Range(0, 47).ToList();
    Assert(proposals.SequenceEqual(expected), "catalog does not enumerate labs 0-46");
    Assert(features.SequenceEqual(expected), "backlog does not enumerate labs 0-46");
    foreach (var demo in demos)
    {
        Assert(File.Exists(Path.Combine(demo.FullName, "README.md")), $"{demo.Name} is missing README.md");
    }
});

Check("invalid selected lab fails before any Azure command", () =>
{
    using var temporary = new TemporaryDirectory();
    var demo = Path.Combine(temporary.Path, "1-example");
    var infra = Path.Combine(demo, "infra");
    Directory.CreateDirectory(infra);
    File.WriteAllText(Path.Combine(infra, "main.bicep"), string.Empty);
    File.WriteAllText(Path.Combine(demo, "demo.cs"), string.Empty);
    File.WriteAllText(Path.Combine(demo, "test_demo.cs"), string.Empty);
    var calls = 0;
    Throws<RunnerArgumentException>(() =>
        DeployRunner.RunWorkshop(
            DeployRunner.ParseArguments(["--demo", "99"]),
            temporary.Path,
            (_, _, _, _) =>
            {
                calls++;
                return string.Empty;
            }), "exactly one demo");
    Equal(0, calls, "Azure command count");
});

Check("local file-app tests run before any deployment command", () =>
{
    using var temporary = new TemporaryDirectory();
    var demo = Path.Combine(temporary.Path, "1-example");
    Directory.CreateDirectory(Path.Combine(demo, "infra"));
    File.WriteAllText(Path.Combine(demo, "infra", "main.bicep"), string.Empty);
    File.WriteAllText(Path.Combine(demo, "demo.cs"), string.Empty);
    File.WriteAllText(Path.Combine(demo, "test_demo.cs"), string.Empty);
    var calls = new List<IReadOnlyList<string>>();
    var liveCalled = false;
    string FakeRun(
        IReadOnlyList<string> command,
        string workingDirectory,
        bool capture,
        IReadOnlyDictionary<string, string>? environment)
    {
        calls.Add(command);
        if (command.SequenceEqual(["az", "account", "show", "-o", "json"]))
        {
            return """{"id":"sub","name":"test","user":{"type":"user"}}""";
        }

        if (command.Take(3).SequenceEqual(["az", "bicep", "build"]))
        {
            return """{"resources":[]}""";
        }

        if (command.Count > 0 && command[0] == "dotnet")
        {
            throw new CommandFailureException("test app failed");
        }

        return string.Empty;
    }

    Throws<CommandFailureException>(() =>
        DeployRunner.RunWorkshop(
            DeployRunner.ParseArguments(["--demo", "1", "--principal-id", "presenter"]),
            temporary.Path,
            FakeRun,
            (_, _) => liveCalled = true), "test app failed");
    var testIndex = calls.FindIndex(command =>
        command.Any(argument => argument.EndsWith("test_demo.cs", StringComparison.Ordinal)));
    Assert(testIndex >= 0, "test file app was not run");
    Assert(!calls.Any(command => command.Take(3).SequenceEqual(["az", "group", "create"])),
        "resource group created after local test failure");
    Assert(!calls.Any(command => command.Take(3).SequenceEqual(["az", "provider", "register"])),
        "provider registered after local test failure");
    Assert(!liveCalled, "live app ran after local test failure");
});

Check("deployment persists outputs and maps them for the live file app", () =>
{
    using var temporary = new TemporaryDirectory();
    var demoPath = Path.Combine(temporary.Path, "1-example");
    Directory.CreateDirectory(Path.Combine(demoPath, "infra"));
    File.WriteAllText(Path.Combine(demoPath, "infra", "main.bicep"), string.Empty);
    File.WriteAllText(Path.Combine(demoPath, "demo.cs"), string.Empty);
    File.WriteAllText(Path.Combine(demoPath, "test_demo.cs"), string.Empty);
    var calls = new List<IReadOnlyList<string>>();
    var outputs = """{"endpoint":{"value":"https://example.openai.azure.com/"},"deployment":{"value":"router"}}""";
    string FakeRun(
        IReadOnlyList<string> command,
        string workingDirectory,
        bool capture,
        IReadOnlyDictionary<string, string>? environment)
    {
        calls.Add(command);
        if (command.Take(3).SequenceEqual(["az", "account", "show"]))
        {
            return """{"id":"sub","name":"test","user":{"type":"user"}}""";
        }

        if (command.Take(3).SequenceEqual(["az", "bicep", "build"]))
        {
            return """{"resources":[{"type":"Microsoft.CognitiveServices/accounts"},{"type":"Microsoft.Storage/storageAccounts"},{"type":"Microsoft.Authorization/roleAssignments"}]}""";
        }

        if (command.Take(4).SequenceEqual(["az", "deployment", "group", "create"]))
        {
            return outputs;
        }

        return string.Empty;
    }

    Dictionary<string, string>? liveEnvironment = null;
    DeployRunner.RunWorkshop(
        DeployRunner.ParseArguments(["--demo", "1", "--principal-id", "presenter"]),
        temporary.Path,
        FakeRun,
        (_, environment) => liveEnvironment = new Dictionary<string, string>(environment, StringComparer.Ordinal));
    var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(demoPath, ".azure", "deployment.json")));
    Equal("sub", saved.RootElement.GetProperty("subscription").GetString(), "saved subscription");
    Equal("rg-foundry-example-demo", saved.RootElement.GetProperty("resourceGroup").GetString(), "saved resource group");
    Equal("router", saved.RootElement.GetProperty("outputs").GetProperty("deployment").GetProperty("value").GetString(),
        "saved output");
    Equal("https://example.openai.azure.com/", liveEnvironment!["AZURE_OPENAI_ENDPOINT"], "live endpoint");
    Equal("router", liveEnvironment["AZURE_OPENAI_DEPLOYMENT"], "live deployment");
    Assert(!calls.Any(command => command.Contains("Microsoft.Authorization/roleAssignments")),
        "authorization provider should not be registered");
    var testIndex = calls.ToList().FindIndex(command =>
        command.Any(argument => argument.EndsWith("test_demo.cs", StringComparison.Ordinal)));
    var deploymentIndex = calls.ToList().FindIndex(command =>
        command.Take(4).SequenceEqual(["az", "deployment", "group", "create"]));
    Assert(testIndex >= 0 && testIndex < deploymentIndex, "local test did not precede deployment");
});

Console.WriteLine($"\n{passed} offline checks passed; {failures.Count} failed.");
return failures.Count == 0 ? 0 : 1;

file sealed class TemporaryDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"foundry-runner-tests-{Guid.NewGuid():N}");

    internal TemporaryDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        Directory.Delete(Path, recursive: true);
    }
}
