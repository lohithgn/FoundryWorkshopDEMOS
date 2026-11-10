#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Text.RegularExpressions;

await MonitoringDeploy.RunAsync(args);

static class MonitoringDeploy
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file deploy_lab.cs -- [--subscription ID] [--principal-id ID] [--prefix NAME] [--location REGION] [--infra-only]");
            return;
        }
        string Arg(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        var prefix = Arg("--prefix", "rg-foundry-student");
        if (!Regex.IsMatch(prefix, @"\A[a-zA-Z0-9_-]{1,50}\z")) throw new ArgumentException("--prefix must contain 1-50 letters, numbers, underscores, or hyphens.");
        var location = Arg("--location", "eastus2");
        var group = prefix + "-production-monitoring-demo";
        var lab = Environment.CurrentDirectory;
        var statePath = Path.Combine(lab, ".azure", "lab-deployment.json");
        var stateDir = Path.GetDirectoryName(statePath)!;
        Directory.CreateDirectory(stateDir);
        async Task<string> Run(params string[] values)
        {
            var p = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            foreach (var value in values) p.StartInfo.ArgumentList.Add(value);
            p.Start();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var output = await stdout;
            var error = await stderr;
            if (p.ExitCode != 0) throw new InvalidOperationException($"az command failed: {error}");
            return output.Trim();
        }
        var selectedSub = Arg("--subscription", "");
        var scope = selectedSub.Length == 0 ? Array.Empty<string>() : new[] { "--subscription", selectedSub };
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
            using var tags = System.Text.Json.JsonDocument.Parse(await Run(["group", "show", "--name", group, "--subscription", subscription, "--query", "tags", "-o", "json"]));
            if (!File.Exists(statePath) || tags.RootElement.GetProperty("managedBy").GetString() != "foundry-lab-script"
                || tags.RootElement.GetProperty("lab").GetString() != "37-production-monitoring")
                throw new InvalidOperationException("Refusing to reuse a group not recorded and owned by this lab.");
        }
        var principal = Arg("--principal-id", "");
        if (principal.Length == 0) principal = await Run(["ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"]);
        if (principal.Length == 0) throw new InvalidOperationException("Provide --principal-id or sign in as an identifiable user.");
        await RunDotnet("run", "--file", "test_demo.cs");
        var template = Path.Combine(lab, "infra", "main.bicep");
        using var compiled = System.Text.Json.JsonDocument.Parse(await Run("bicep", "build", "--file", template, "--stdout"));
        var providers = compiled.RootElement.GetProperty("resources").EnumerateArray()
            .Select(resource => resource.GetProperty("type").GetString()!.Split('/')[0])
            .Where(provider => !provider.Equals("Microsoft.Authorization", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
            await Run(["provider", "register", "--namespace", provider, "--wait", "--subscription", subscription]);
        await Run(["group", "create", "--name", group, "--location", location,
            "--tags", "managedBy=foundry-lab-script", "lab=37-production-monitoring", "SecurityControl=Ignore", "SecurityExemption=StorageAccountKeyAccess",
            "--subscription", subscription, "-o", "none"]);
        await File.WriteAllTextAsync(statePath, System.Text.Json.JsonSerializer.Serialize(new { subscription, resourceGroup = group, outputs = new { } }) + "\n");
        var outputs = await Run(["deployment", "group", "create", "--resource-group", group,
            "--name", "production-monitoring-demo", "--subscription", subscription, "--template-file", template,
            "--parameters", $"principalId={principal}", $"location={location}", "--query", "properties.outputs", "-o", "json"]);
        using var outputsJson = System.Text.Json.JsonDocument.Parse(outputs);
        await File.WriteAllTextAsync(Path.Combine(stateDir, "deployment.json"), outputs + "\n");
        await File.WriteAllTextAsync(statePath, System.Text.Json.JsonSerializer.Serialize(
            new { subscription, resourceGroup = group, outputs = outputsJson.RootElement.Clone() },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        if (!args.Contains("--infra-only"))
        {
            await RunDotnet("run", "--file", "demo.cs", "--", "--setup");
            await RunDotnet("run", "--file", "demo.cs", "--", "--live-test");
        }
        Console.WriteLine($"Infrastructure deployed to {group}.");
    }

    static async Task RunDotnet(params string[] values)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var value in values) process.StartInfo.ArgumentList.Add(value);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Console.Write(await stdout);
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"dotnet {string.Join(' ', values)} failed: {error}");
    }
}
