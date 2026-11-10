using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class AgentEvaluationCleanup
{
    private static readonly string Lab = Path.GetFileName(Directory.GetCurrentDirectory());
    private static readonly string State = Path.Combine(Directory.GetCurrentDirectory(), ".azure", "lab-deployment.json");

    public static string RunAz(params string[] args)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Azure CLI.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Azure CLI failed ({process.ExitCode}): {error.Trim()}");
        return output.Trim();
    }

    public static int Main(string[] args)
    {
        try
        {
            var yes = args.Contains("--yes", StringComparer.Ordinal);
            if (args.Contains("--help", StringComparer.Ordinal))
            {
                Console.WriteLine("Usage: dotnet run --file cleanup.cs -- [--yes]");
                return 0;
            }
            if (args.Any(arg => arg != "--yes")) throw new ArgumentException("Only --yes is supported.");
            if (!File.Exists(State)) throw new InvalidOperationException("No lab deployment record. Nothing will be deleted; run deploy_lab.cs first.");
            var record = JsonNode.Parse(File.ReadAllText(State))!.AsObject();
            var subscription = record["subscription"]?.GetValue<string>();
            var group = record["resourceGroup"]?.GetValue<string>();
            if (record["lab"]?.GetValue<string>() != Lab || string.IsNullOrWhiteSpace(subscription) ||
                string.IsNullOrWhiteSpace(group) ||
                !Regex.IsMatch(group, @"^[a-zA-Z0-9_-]{1,50}-agent-evaluation-demo$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Invalid or mismatched lab deployment record; refusing deletion.");
            var scope = new[] { "--subscription", subscription };
            try
            {
                var exists = RunAz(["group", "exists", "--name", group, .. scope]);
                if (exists is not ("true" or "false")) throw new InvalidOperationException($"Unexpected group existence response: {exists}");
                if (exists == "true")
                {
                    using var tags = JsonDocument.Parse(RunAz(["group", "show", "--name", group, .. scope, "--query", "tags", "-o", "json"]));
                    var values = tags.RootElement;
                    if (!values.TryGetProperty("lab", out var lab) || lab.GetString() != Lab ||
                        !values.TryGetProperty("managedBy", out var owner) || owner.GetString() != "foundry-lab-script")
                        throw new InvalidOperationException("Group ownership tags do not match this lab; refusing deletion.");
                    Console.WriteLine($"Delete ALL resources in {group}, subscription {subscription}.");
                    if (!yes)
                    {
                        Console.Write("Type the full resource group name to confirm: ");
                        if (Console.ReadLine()?.Trim() != group)
                            throw new InvalidOperationException("Confirmation did not match. Nothing was deleted.");
                    }
                    _ = RunAz(["group", "delete", "--name", group, .. scope, "--yes"]);
                    if (RunAz(["group", "exists", "--name", group, .. scope]) != "false")
                        throw new InvalidOperationException("Azure still reports the group exists; cleanup is not complete.");
                }
                record["cleanupStatus"] = "deleted";
                record.Remove("cleanupError");
                Console.WriteLine($"Verified absent: {group}. Local logs and results retained.");
                return 0;
            }
            catch (Exception error)
            {
                record["cleanupStatus"] = "failed";
                record["cleanupError"] = error.Message;
                throw;
            }
            finally
            {
                File.WriteAllText(State, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}");
            return 1;
        }
    }
}
