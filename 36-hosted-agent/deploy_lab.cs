#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Text.RegularExpressions;

await HostedDeploy.RunAsync(args);

static class HostedDeploy
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file deploy_lab.cs -- [--subscription ID] [--principal-id ID] [--prefix NAME] [--location REGION] [--infra-only]");
            return;
        }
        if (!args.Contains("--infra-only"))
            throw new NotSupportedException("The C# path cannot publish or route its runtime image while runtime/Dockerfile remains Python-only; run only --infra-only here. The Python deployment path remains transitional and unchanged. See README for the exact packaging exception.");
        string Arg(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        var prefix = Arg("--prefix", "rg-foundry-student");
        if (!Regex.IsMatch(prefix, @"\A[a-zA-Z0-9_-]{1,50}\z")) throw new ArgumentException("--prefix must contain 1-50 letters, numbers, underscores, or hyphens.");
        var location = Arg("--location", "eastus2");
        var group = prefix + "-hosted-agent-demo";
        var lab = Environment.CurrentDirectory;
        var statePath = Path.Combine(lab, ".azure", "lab-deployment.json");
        var stateDirectory = Path.GetDirectoryName(statePath)!;
        Directory.CreateDirectory(stateDirectory);
        async Task<string> Run(params string[] values)
        {
            var p = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            foreach (var value in values) p.StartInfo.ArgumentList.Add(value);
            p.Start();
            var output = await p.StandardOutput.ReadToEndAsync();
            var error = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new InvalidOperationException($"az {string.Join(' ', values)} failed: {error}");
            return output.Trim();
        }
        var sub = Arg("--subscription", "");
        var scope = string.IsNullOrEmpty(sub) ? Array.Empty<string>() : new[] { "--subscription", sub };
        using var account = System.Text.Json.JsonDocument.Parse(await Run(["account", "show", "-o", "json", .. scope]));
        if (account.RootElement.GetProperty("user").GetProperty("type").GetString() != "user")
            throw new InvalidOperationException("Use az login as a user; the template assigns User RBAC.");
        var subscription = account.RootElement.GetProperty("id").GetString()!;
        if (File.Exists(statePath))
        {
            using var prior = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            if (prior.RootElement.GetProperty("subscription").GetString() != subscription || prior.RootElement.GetProperty("resourceGroup").GetString() != group)
                throw new InvalidOperationException("Recorded lab targets a different subscription/group; do not overwrite its state.");
        }
        var exists = await Run(["group", "exists", "--name", group, "--subscription", subscription]);
        if (exists is not ("true" or "false"))
            throw new InvalidOperationException($"Unexpected group existence response: {exists}");
        if (exists == "true")
        {
            var tagsText = await Run(["group", "show", "--name", group, "--subscription", subscription, "--query", "tags", "-o", "json"]);
            using var tags = System.Text.Json.JsonDocument.Parse(tagsText);
            if (!File.Exists(statePath) || !tags.RootElement.TryGetProperty("managedBy", out var managed) || managed.GetString() != "foundry-lab-script"
                || !tags.RootElement.TryGetProperty("lab", out var labTag) || labTag.GetString() != "36-hosted-agent")
                throw new InvalidOperationException("Refusing to reuse a group not recorded and owned by this lab.");
        }
        var principal = Arg("--principal-id", "");
        if (principal.Length == 0) principal = await Run(["ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"]);
        if (principal.Length == 0) throw new InvalidOperationException("Provide --principal-id or sign in as an identifiable user.");
        var template = Path.Combine(lab, "infra", "main.bicep");
        using var compiled = System.Text.Json.JsonDocument.Parse(await Run("bicep", "build", "--file", template, "--stdout"));
        var providers = compiled.RootElement.GetProperty("resources").EnumerateArray()
            .Select(resource => resource.GetProperty("type").GetString()!.Split('/')[0])
            .Where(provider => !provider.Equals("Microsoft.Authorization", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
            await Run(["provider", "register", "--namespace", provider, "--wait", "--subscription", subscription]);
        await Run(["group", "create", "--name", group, "--location", location,
            "--tags", "managedBy=foundry-lab-script", "lab=36-hosted-agent", "SecurityControl=Ignore", "SecurityExemption=StorageAccountKeyAccess",
            "--subscription", subscription, "-o", "none"]);
        var record = new { subscription, resourceGroup = group, outputs = new { } };
        await File.WriteAllTextAsync(statePath, System.Text.Json.JsonSerializer.Serialize(record, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        var deployment = await Run(["deployment", "group", "create", "--resource-group", group,
            "--name", "hosted-agent-demo", "--subscription", subscription, "--template-file", template,
            "--parameters", $"principalId={principal}", $"location={location}", "--query", "properties.outputs", "-o", "json"]);
        using var outputs = System.Text.Json.JsonDocument.Parse(deployment);
        await File.WriteAllTextAsync(Path.Combine(stateDirectory, "deployment.json"), deployment + "\n");
        await File.WriteAllTextAsync(statePath, System.Text.Json.JsonSerializer.Serialize(
            new { subscription, resourceGroup = group, outputs = outputs.RootElement.Clone() },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Infrastructure deployed to {group}.");
    }
}
