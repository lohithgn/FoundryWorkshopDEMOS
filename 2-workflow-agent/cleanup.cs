#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

try { return RunCleanup(args); }
catch (Exception error)
{
    Console.Error.WriteLine($"ERROR: {error.Message}");
    return 1;
}

static int RunCleanup(string[] arguments)
{
    var yes = false;
    foreach (var argument in arguments)
    {
        switch (argument)
        {
            case "--help": case "-h": Usage(); return 0;
            case "--yes": yes = true; break;
            default: throw new ArgumentException($"Unknown argument: {argument}");
        }
    }

    var lab = Environment.CurrentDirectory;
    var labName = Path.GetFileName(lab.TrimEnd(Path.DirectorySeparatorChar));
    var statePath = Path.Combine(lab, ".azure", "lab-deployment.json");
    if (!File.Exists(statePath))
        throw new ArgumentException("No lab deployment record. Nothing will be deleted; run deploy_lab.cs first.");
    var state = JsonNode.Parse(File.ReadAllText(statePath))?.AsObject()
        ?? throw new ArgumentException("Invalid lab deployment record; refusing deletion.");
    var subscription = state["subscription"]?.GetValue<string>();
    var group = state["resourceGroup"]?.GetValue<string>();
    var suffix = Regex.Escape(Regex.Replace(labName, "^\\d+-", "") + "-demo");
    if (state["lab"]?.GetValue<string>() != labName || string.IsNullOrWhiteSpace(subscription) ||
        group is null || !Regex.IsMatch(group, "^[a-zA-Z0-9_-]{1,50}-" + suffix + "$"))
        throw new ArgumentException("Invalid or mismatched lab deployment record; refusing deletion.");

    var scope = new[] { "--subscription", subscription };
    try
    {
        var exists = Az(["group", "exists", "--name", group, .. scope], lab);
        if (exists is not ("true" or "false"))
            throw new InvalidOperationException($"Unexpected group existence response: {exists}");
        if (exists == "true")
        {
            var tags = JsonNode.Parse(Az(["group", "show", "--name", group, .. scope, "--query", "tags", "-o", "json"], lab))?.AsObject();
            if (tags?["lab"]?.ToString() != labName || tags?["managedBy"]?.ToString() != "foundry-lab-script")
                throw new CleanupRefusalException("Group ownership tags do not match this lab; refusing deletion.");
            Console.WriteLine($"Delete ALL resources in {group}, subscription {subscription}.");
            if (!yes)
            {
                Console.Write("Type the full resource group name to confirm: ");
                if (Console.ReadLine()?.Trim() != group)
                    throw new CleanupRefusalException("Confirmation did not match. Nothing was deleted.");
            }
            _ = Az(["group", "delete", "--name", group, .. scope, "--yes"], lab);
            if (Az(["group", "exists", "--name", group, .. scope], lab) != "false")
                throw new InvalidOperationException("Azure still reports the group exists; cleanup is not complete.");
        }
        state["cleanupStatus"] = "deleted";
        state.Remove("cleanupError");
        Console.WriteLine($"Verified absent: {group}. Local logs and results retained in {Path.GetDirectoryName(statePath)}.");
    }
    catch (Exception error) when (error is not CleanupRefusalException)
    {
        state["cleanupStatus"] = "failed";
        state["cleanupError"] = error.Message;
        throw;
    }
    finally
    {
        File.WriteAllText(statePath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
    return 0;
}

static string Az(IEnumerable<string> arguments, string directory)
{
    var values = arguments.ToArray();
    Console.WriteLine($"\n> az {string.Join(" ", values)}");
    var start = new ProcessStartInfo("az") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var value in values) start.ArgumentList.Add(value);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start az.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    var output = stdout.GetAwaiter().GetResult();
    var errorOutput = stderr.GetAwaiter().GetResult();
    Console.Write(errorOutput);
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"az command failed ({process.ExitCode}): {output}{errorOutput}");
    return output.Trim();
}

static void Usage() => Console.WriteLine(
    "Delete only the recorded lab resource group; local results are retained.\n" +
    "Usage: dotnet run --file cleanup.cs -- [--yes]\n" +
    "Uses .azure/lab-deployment.json and its subscription, not the current Azure CLI selection.");

sealed class CleanupRefusalException(string message) : Exception(message) { }
