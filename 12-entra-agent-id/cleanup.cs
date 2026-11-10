#:property TargetFramework=net10.0

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class EntraAgentCleanup
{
    private static readonly string Lab = Path.GetFileName(Directory.GetCurrentDirectory());
    private static readonly string State = Path.Combine(Directory.GetCurrentDirectory(), ".azure", "lab-deployment.json");

    private const string GraphCleanup = """
        $ErrorActionPreference = "Stop"
        Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
        $ids = $env:LAB_IDENTITY_CLEANUP | ConvertFrom-Json
        Connect-MgGraph -TenantId $ids.tenantId -ContextScope Process -NoWelcome -Scopes @(
            "AgentIdentity.DeleteRestore.All",
            "AgentIdentityBlueprintPrincipal.DeleteRestore.All",
            "AgentIdentityBlueprint.DeleteRestore.All"
        )
        if ((Get-MgContext).TenantId -ne $ids.tenantId) { throw "Graph tenant mismatch; refusing deletion." }
        $targets = @()
        if ($ids.agentIdentityId) {
            $targets += "beta/servicePrincipals/$($ids.agentIdentityId)/microsoft.graph.agentIdentity"
        }
        if ($ids.blueprintPrincipalId) {
            $targets += "v1.0/servicePrincipals/$($ids.blueprintPrincipalId)/microsoft.graph.agentIdentityBlueprintPrincipal"
        }
        $targets += "v1.0/applications/$($ids.blueprintObjectId)/microsoft.graph.agentIdentityBlueprint"
        foreach ($target in $targets) {
            try {
                Invoke-MgGraphRequest -Method DELETE -Uri "https://graph.microsoft.com/$target" | Out-Null
                Write-Host "Deleted recorded tenant object: $target"
            } catch {
                if ($_.Exception.Response.StatusCode -eq 404 -or $_.Exception.ResponseStatusCode -eq 404) {
                    Write-Host "Recorded tenant object already absent: $target"
                } else {
                    throw
                }
            }
        }
        """;

    public static Dictionary<string, string> IdentityRecord(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Identity setup record must be an object.");
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[]
        {
            "tenantId", "blueprintObjectId", "blueprintClientId", "blueprintPrincipalId", "agentIdentityId"
        })
        {
            if (key is "blueprintPrincipalId" or "agentIdentityId" &&
                !document.RootElement.TryGetProperty(key, out _))
                continue;
            if (!document.RootElement.TryGetProperty(key, out var item) || item.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(item.GetString(), out var id))
                throw new ArgumentException($"Invalid identity setup field {key}; refusing tenant deletion.");
            ids[key] = id.ToString();
        }
        var objectIds = new[] { "blueprintObjectId", "blueprintPrincipalId", "agentIdentityId" }
            .Where(ids.ContainsKey).Select(key => ids[key]).ToArray();
        if (objectIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != objectIds.Length ||
            (ids.TryGetValue("agentIdentityId", out var agent) &&
             string.Equals(agent, ids["blueprintClientId"], StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Identity setup reuses object IDs; refusing tenant deletion.");
        return ids;
    }

    public static void ValidateDeploymentRecord(JsonObject result, string lab)
    {
        var suffix = Regex.Escape(Regex.Replace(lab, @"^\d+-", "") + "-demo");
        var subscription = result["subscription"]?.GetValue<string>();
        var group = result["resourceGroup"]?.GetValue<string>();
        if (result["lab"]?.GetValue<string>() != lab || string.IsNullOrWhiteSpace(subscription) ||
            string.IsNullOrWhiteSpace(group) ||
            !Regex.IsMatch(group, @"^[a-zA-Z0-9_-]{1,50}-" + suffix + "$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid or mismatched lab deployment record; refusing deletion.");
    }

    private static string RunAz(params string[] args) => Run(["az", .. args]);

    private static string Run(
        IReadOnlyList<string> command, IReadOnlyDictionary<string, string?>? environment = null, bool showOutput = false)
    {
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            if (!environment.ContainsKey("ENTRA_BLUEPRINT_SECRET"))
                start.Environment.Remove("ENTRA_BLUEPRINT_SECRET");
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command[0]}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (showOutput && output.Length > 0) Console.Write(output);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{command[0]} failed ({process.ExitCode}): {error.Trim()}");
        return output.Trim();
    }

    public static int Main(string[] args)
    {
        try
        {
            var yes = false;
            var azureOnly = false;
            foreach (var argument in args)
            {
                switch (argument)
                {
                    case "--yes": yes = true; break;
                    case "--azure-only": azureOnly = true; break;
                    case "--help":
                        Console.WriteLine("Usage: dotnet run --file cleanup.cs -- [--yes] [--azure-only]");
                        return 0;
                    default: throw new ArgumentException($"Unknown option: {argument}.");
                }
            }
            var directory = Directory.GetCurrentDirectory();
            var identityPath = Path.Combine(directory, ".azure", "identity.json");
            if (!File.Exists(State) && !File.Exists(identityPath))
                throw new ArgumentException("No recorded Azure deployment or tenant identity. Nothing will be deleted.");
            var ids = File.Exists(identityPath) && !azureOnly ? IdentityRecord(identityPath) : null;
            if (ids is not null && File.Exists(State))
            {
                var deployed = JsonNode.Parse(File.ReadAllText(State))?["outputs"]?["agentIdentityId"]?["value"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(deployed) &&
                    !string.Equals(deployed, ids.GetValueOrDefault("agentIdentityId"), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Recorded tenant identity does not match the Azure deployment; refusing cleanup.");
            }
            if (File.Exists(State)) CleanupAzure(yes);
            if (azureOnly)
            {
                Console.WriteLine("Azure-only cleanup requested. Tenant identities and their credential are NOT deleted.");
                return 0;
            }
            if (ids is null)
            {
                using var state = JsonDocument.Parse(File.ReadAllText(State));
                if (!state.RootElement.TryGetProperty("identityCleanupStatus", out var status) || status.GetString() != "deleted")
                    throw new InvalidOperationException("Identity record is missing; tenant cleanup cannot be verified.");
                Console.WriteLine("Recorded tenant identities were already deleted.");
                return 0;
            }
            Console.WriteLine($"Delete recorded Entra objects in tenant {ids["tenantId"]}; blueprint {ids["blueprintObjectId"]}.");
            if (!yes)
            {
                Console.Write("Type the blueprint object ID to confirm tenant deletion: ");
                if (Console.ReadLine()?.Trim() != ids["blueprintObjectId"])
                    throw new ArgumentException("Confirmation did not match. Tenant identities were not deleted.");
            }
            var reportPath = Path.Combine(directory, ".azure", "identity-cleanup.json");
            var report = new JsonObject
            {
                ["lab"] = Lab, ["identities"] = ToJsonObject(ids), ["status"] = "running"
            };
            try
            {
                var environment = EnvironmentMap()
                    .Where(pair => pair.Key != "ENTRA_BLUEPRINT_SECRET")
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                environment["LAB_IDENTITY_CLEANUP"] = ToJsonObject(ids).ToJsonString();
                Run(["pwsh", "-NoProfile", "-Command", GraphCleanup], environment, showOutput: true);
                report["status"] = "deleted";
                Save(report, reportPath);
                if (File.Exists(State))
                {
                    var state = JsonNode.Parse(File.ReadAllText(State))!.AsObject();
                    state["identityCleanupStatus"] = "deleted";
                    Save(state, State);
                }
                File.Delete(identityPath);
                Console.WriteLine("Recorded tenant identities deleted; non-secret IDs archived. " +
                    "Remove Env:ENTRA_BLUEPRINT_SECRET in the calling PowerShell session.");
                return 0;
            }
            catch (Exception error)
            {
                report["status"] = "failed";
                report["error"] = error is InvalidOperationException
                    ? $"Graph cleanup failed; identity record retained for retry. ({error.Message})"
                    : error.Message;
                Save(report, reportPath);
                throw;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}");
            return 1;
        }
    }

    private static void CleanupAzure(bool yes)
    {
        var record = JsonNode.Parse(File.ReadAllText(State))!.AsObject();
        try
        {
            ValidateDeploymentRecord(record, Lab);
            var subscription = record["subscription"]!.GetValue<string>();
            var group = record["resourceGroup"]!.GetValue<string>();
            var scope = new[] { "--subscription", subscription };
            var exists = RunAz(["group", "exists", "--name", group, .. scope]);
            if (exists is not ("true" or "false"))
                throw new InvalidOperationException($"Unexpected group existence response: {exists}");
            if (exists == "true")
            {
                using var tags = JsonDocument.Parse(RunAz(["group", "show", "--name", group,
                    .. scope, "--query", "tags", "-o", "json"]));
                if (tags.RootElement.ValueKind != JsonValueKind.Object ||
                    !tags.RootElement.TryGetProperty("lab", out var lab) || lab.GetString() != Lab ||
                    !tags.RootElement.TryGetProperty("managedBy", out var owner) || owner.GetString() != "foundry-lab-script")
                    throw new ArgumentException("Group ownership tags do not match this lab; refusing deletion.");
                Console.WriteLine($"Delete ALL resources in {group}, subscription {subscription}.");
                if (!yes)
                {
                    Console.Write("Type the full resource group name to confirm: ");
                    if (Console.ReadLine()?.Trim() != group)
                        throw new ArgumentException("Confirmation did not match. Nothing was deleted.");
                }
                _ = RunAz(["group", "delete", "--name", group, .. scope, "--yes"]);
                if (RunAz(["group", "exists", "--name", group, .. scope]) != "false")
                    throw new InvalidOperationException("Azure still reports the group exists; cleanup is not complete.");
            }
            record["cleanupStatus"] = "deleted";
            record.Remove("cleanupError");
            Console.WriteLine($"Verified absent: {group}. Local logs and results retained in {Path.GetDirectoryName(State)}.");
        }
        catch (Exception error)
        {
            record["cleanupStatus"] = "failed";
            record["cleanupError"] = error.Message;
            throw;
        }
        finally { Save(record, State); }
    }

    private static IReadOnlyDictionary<string, string?> EnvironmentMap() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(item => (string)item.Key, item => item.Value?.ToString(), StringComparer.Ordinal);

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> values)
    {
        var result = new JsonObject();
        foreach (var pair in values) result[pair.Key] = pair.Value;
        return result;
    }

    private static void Save(JsonNode value, string path) =>
        File.WriteAllText(path, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
}
