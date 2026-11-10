#!/usr/bin/env dotnet
#:property PublishAot=false
#:include LabLifecycle.cs

using AgentOrchestrations;
using System.Text.Json;

if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
{
    Console.WriteLine("Run offline lab lifecycle tests. Usage: dotnet run --file test_deploy_lab.cs");
    return;
}

await LifecycleTests.RunAsync();

static class LifecycleTests
{
    private static int _passed;
    private static string _lab = Directory.GetCurrentDirectory();
    private static string _statePath = "";

    public static Task RunAsync()
    {
        Directory.CreateDirectory(Path.Combine(_lab, ".azure"));
        _statePath = Path.Combine(_lab, ".azure", $".test-lifecycle-{Guid.NewGuid():N}.json");
        try
        {
            PreparePersistsAndReusesAccountName();
            CleanupRequiresOwnershipAndRotatesOnlyWhenFresh();
            InvalidInputsAreRejectedBeforeAzureChanges();
            Console.WriteLine($"PASS: {_passed} offline deployment lifecycle tests.");
        }
        finally
        {
            foreach (var path in new[] { _statePath, _statePath + ".tmp" })
                if (File.Exists(path))
                    File.Delete(path);
            var azureDirectory = Path.Combine(_lab, ".azure");
            if (Directory.Exists(azureDirectory) && !Directory.EnumerateFileSystemEntries(azureDirectory).Any())
                Directory.Delete(azureDirectory);
        }
        return Task.CompletedTask;
    }

    private static void PreparePersistsAndReusesAccountName()
    {
        var azure = new FakeAzure();
        var args = new[] { "--prefix", "rg-foundry-offline", "--principal-id", "presenter", "--prepare-only" };
        var first = LabLifecycle.Prepare(args, _lab, _statePath, azure);
        var initialName = first.AccountName;
        True(initialName.StartsWith("foundry-patterns-", StringComparison.Ordinal), "generated account name");
        Equal("prepared", LabLifecycle.ReadState(_statePath).Status, "prepared status");
        True(File.ReadAllText(_statePath).Contains("\"accountName\"", StringComparison.Ordinal),
            "saved state retains the existing JSON contract");
        var retry = LabLifecycle.Prepare(args, _lab, _statePath, azure);
        Equal(initialName, retry.AccountName, "retry reuses account name");
        Pass("prepare-only saves and reuses a generated name");
    }

    private static void CleanupRequiresOwnershipAndRotatesOnlyWhenFresh()
    {
        var state = LabLifecycle.ReadState(_statePath);
        var azure = new FakeAzure { GroupExists = true };
        azure.Tags = new Dictionary<string, string>
        {
            ["managedBy"] = "foundry-lab-script",
            ["lab"] = Path.GetFileName(_lab),
        };
        var result = LabLifecycle.Cleanup(["--yes"], _lab, _statePath, azure);
        Equal(0, result, "owned group cleanup");
        Equal("deleted", LabLifecycle.ReadState(_statePath).CleanupStatus, "cleanup status");
        Throws<CommandLineException>(() => LabLifecycle.Prepare(
            ["--prefix", "rg-foundry-offline", "--principal-id", "presenter"],
            _lab, _statePath, azure));
        var fresh = LabLifecycle.Prepare(
            ["--prefix", "rg-foundry-offline", "--principal-id", "presenter", "--start-fresh", "--prepare-only"],
            _lab, _statePath, azure);
        True(fresh.AccountName != state.AccountName, "fresh lifecycle rotates account name");

        var unowned = new FakeAzure { GroupExists = true, Tags = new Dictionary<string, string>() };
        var standardError = Console.Error;
        using var expectedError = new StringWriter();
        Console.SetError(expectedError);
        int cleanupResult;
        try
        {
            cleanupResult = LabLifecycle.Cleanup(["--yes"], _lab, _statePath, unowned);
        }
        finally
        {
            Console.SetError(standardError);
        }
        Equal(2, cleanupResult, "unowned group rejected");
        True(!unowned.Commands.Any(command => command.Any(argument =>
                argument.Contains("delete", StringComparison.Ordinal))),
            "unowned resource group not deleted");
        Pass("cleanup validates ownership and fresh lifecycle rotation");
    }

    private static void InvalidInputsAreRejectedBeforeAzureChanges()
    {
        var azure = new FakeAzure();
        Throws<CommandLineException>(() => LabLifecycle.Prepare(
            ["--prefix", "../invalid", "--prepare-only"], _lab, _statePath, azure));
        Equal(0, azure.Commands.Count, "invalid prefix does not call Azure");
        Pass("invalid lifecycle input is rejected");
    }

    private static void Pass(string name)
    {
        _passed++;
        Console.WriteLine($"PASS: {name}");
    }

    private static void True(bool value, string label)
    {
        if (!value)
            throw new InvalidOperationException($"{label}: assertion failed.");
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

sealed class FakeAzure : IAzureCli
{
    public bool GroupExists { get; set; }
    public Dictionary<string, string> Tags { get; set; } = [];
    public List<string[]> Commands { get; } = [];

    public string Run(params string[] arguments)
    {
        Commands.Add(arguments);
        var command = string.Join(" ", arguments);
        if (command.StartsWith("account show", StringComparison.Ordinal))
            return """{"id":"offline-subscription","user":{"type":"user"}}""";
        if (arguments.Length >= 2 && arguments[0] == "group" && arguments[1] == "exists")
            return GroupExists ? "true" : "false";
        if (arguments.Length >= 2 && arguments[0] == "group" && arguments[1] == "show")
            return JsonSerializer.Serialize(Tags);
        if (arguments.Length >= 2 && arguments[0] == "group" && arguments[1] == "delete")
        {
            GroupExists = false;
            return "";
        }
        throw new InvalidOperationException($"Unexpected mocked Azure command: {command}");
    }
}
