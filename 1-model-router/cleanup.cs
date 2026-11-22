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
        if (argument is "--help" or "-h") { Usage(); return 0; }
        if (argument == "--yes") { yes = true; continue; }
        throw new ArgumentException($"Unknown argument: {argument}");
    }
    var lab = Path.GetFileName(Environment.CurrentDirectory.TrimEnd(Path.DirectorySeparatorChar));
    var statePath = Path.Combine(Environment.CurrentDirectory, ".azure", "lab-deployment.json");
    if (!File.Exists(statePath))
        throw new ArgumentException("No lab deployment record. Nothing will be deleted; run deploy_lab.cs first.");
    var state = JsonDocument.Parse(File.ReadAllText(statePath));
    using (state)
    {
        var root = state.RootElement;
        var subscription = root.GetProperty("subscription").GetString();
        var group = root.GetProperty("resourceGroup").GetString();
        var suffix = Regex.Escape(Regex.Replace(lab, "^\\d+-", "") + "-demo");
        if (root.GetProperty("lab").GetString() != lab || string.IsNullOrWhiteSpace(subscription) ||
            string.IsNullOrWhiteSpace(group) || !Regex.IsMatch(group, $"^[a-zA-Z0-9_-]{{1,50}}-{suffix}$"))
            throw new ArgumentException("Invalid or mismatched lab deployment record; refusing deletion.");
        var scope = new[] { "--subscription", subscription! };
        try
        {
            var exists = Run(["group", "exists", "--name", group!, .. scope]).Trim();
            if (exists is not ("true" or "false"))
                throw new InvalidOperationException($"Unexpected group existence response: {exists}");
            if (exists == "true")
            {
                var tagsText = Run(["group", "show", "--name", group!, .. scope, "--query", "tags", "-o", "json"]);
                using var tagsDocument = JsonDocument.Parse(tagsText);
                var tags = tagsDocument.RootElement;
                if (tags.ValueKind != JsonValueKind.Object ||
                    !tags.TryGetProperty("lab", out var tagLab) || tagLab.GetString() != lab ||
                    !tags.TryGetProperty("managedBy", out var owner) || owner.GetString() != "foundry-lab-script")
                    throw new ArgumentException("Group ownership tags do not match this lab; refusing deletion.");
                Console.WriteLine($"Delete ALL resources in {group}, subscription {subscription}.");
                if (!yes)
                {
                    Console.Write("Type the full resource group name to confirm: ");
                    if (!string.Equals(Console.ReadLine()?.Trim(), group, StringComparison.Ordinal))
                        throw new ArgumentException("Confirmation did not match. Nothing was deleted.");
                }
                _ = Run(["group", "delete", "--name", group!, .. scope, "--yes"]);
                if (Run(["group", "exists", "--name", group!, .. scope]).Trim() != "false")
                    throw new InvalidOperationException("Azure still reports the group exists; cleanup is not complete.");
            }
            UpdateState(statePath, "deleted", null);
            Console.WriteLine($"Verified absent: {group}. Local logs and results retained in {Path.GetDirectoryName(statePath)}.");
        }
        catch (Exception error)
        {
            UpdateState(statePath, "failed", error.Message);
            throw;
        }
    }
    return 0;
}

static void UpdateState(string path, string status, string? error)
{
    var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    state["cleanupStatus"] = status;
    if (error is null) state.Remove("cleanupError");
    else state["cleanupError"] = error;
    File.WriteAllText(path, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
}

static string Run(IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo("az") { WorkingDirectory = Environment.CurrentDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    var values = arguments.ToArray();
    foreach (var value in values) start.ArgumentList.Add(value);
    Console.WriteLine($"\n> az {string.Join(" ", values)}");
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Azure CLI.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    var output = stdout.GetAwaiter().GetResult();
    var error = stderr.GetAwaiter().GetResult();
    if (process.ExitCode != 0) throw new InvalidOperationException($"Azure CLI command failed ({process.ExitCode}): {error}{output}");
    if (error.Length > 0) Console.Error.Write(error);
    return output;
}

static void Usage() => Console.WriteLine("Usage: dotnet run --file cleanup.cs -- [--yes]");
