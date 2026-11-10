#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

return await DeployLab.MainAsync(args);

internal static class DeployLab
{
    private static readonly string Lab = Directory.GetCurrentDirectory();
    private static readonly string State = Path.Combine(Lab, ".azure", "lab-deployment.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Dictionary<string, string> Owner = new() { ["managedBy"] = "foundry-lab-script", ["lab"] = "39-gateway-token-limits" };
    private static readonly Dictionary<string, string> SecurityTags = new() { ["SecurityControl"] = "Ignore", ["SecurityExemption"] = "StorageAccountKeyAccess" };

    private static async Task<(int ExitCode, string Out, string Error)> Run(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = Lab, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Unable to start {executable}.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<string> Az(params string[] args)
    {
        var result = await Run("az", args);
        if (result.ExitCode != 0) throw new InvalidOperationException($"az {string.Join(' ', args)} failed: {result.Error.Trim()}");
        return result.Out.Trim();
    }

    public static async Task<int> MainAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file deploy_lab.cs [-- --subscription ID] [--principal-id ID] [--prefix NAME] [--location REGION] [--infra-only] [--offline-only]");
            return 0;
        }
        string prefix = "rg-foundry-student", location = "eastus2", subscriptionArg = "", principalArg = "";
        bool infraOnly = false, offlineOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--subscription" when i + 1 < args.Length: subscriptionArg = args[++i]; break;
                case "--principal-id" when i + 1 < args.Length: principalArg = args[++i]; break;
                case "--prefix" when i + 1 < args.Length: prefix = args[++i]; break;
                case "--location" when i + 1 < args.Length: location = args[++i]; break;
                case "--infra-only": infraOnly = true; break;
                case "--offline-only": offlineOnly = true; break;
                default: Console.Error.WriteLine($"Unknown or incomplete option: {args[i]}"); return 2;
            }
        }
        if (!Regex.IsMatch(prefix, "^[a-zA-Z0-9_-]{1,50}$"))
        {
            Console.Error.WriteLine("Invalid resource-group prefix.");
            return 2;
        }
        if (!infraOnly)
        {
            var tests = await Run("dotnet", "run", "--file", "test_demo.cs");
            Console.Write(tests.Out);
            if (tests.ExitCode != 0) { Console.Error.Write(tests.Error); return tests.ExitCode; }
        }
        var template = Path.Combine(Lab, "infra", "main.bicep");
        var compiledText = await Az("bicep", "build", "--file", template, "--stdout");
        var compiled = JsonDocument.Parse(compiledText).RootElement;
        var lint = await Run("az", "bicep", "lint", "--file", template);
        if (lint.ExitCode != 0) { Console.Error.WriteLine(lint.Error); return lint.ExitCode; }
        if (offlineOnly) return 0;

        var accountArgs = new List<string> { "account", "show", "-o", "json" };
        if (subscriptionArg.Length > 0) accountArgs.AddRange(["--subscription", subscriptionArg]);
        var account = JsonDocument.Parse(await Az(accountArgs.ToArray())).RootElement;
        if (account.GetProperty("user").GetProperty("type").GetString() != "user")
        {
            Console.Error.WriteLine("Sign in as a user; principalId uses User RBAC.");
            return 2;
        }
        var subscription = account.GetProperty("id").GetString()!;
        var group = $"{prefix}-gateway-token-limits-demo";
        if (File.Exists(State))
        {
            var previous = JsonDocument.Parse(await File.ReadAllTextAsync(State)).RootElement;
            if (previous.GetProperty("subscription").GetString() != subscription || previous.GetProperty("resourceGroup").GetString() != group)
            {
                Console.Error.WriteLine("Recorded deployment targets another subscription/group.");
                return 2;
            }
        }
        var exists = await Az("group", "exists", "--name", group, "--subscription", subscription);
        if (exists == "true")
        {
            var tagsText = await Az("group", "show", "--name", group, "--subscription", subscription, "--query", "tags", "-o", "json");
            var tags = JsonDocument.Parse(tagsText).RootElement;
            if (!File.Exists(State) || tags.ValueKind != JsonValueKind.Object
                || Owner.Any(pair => !tags.TryGetProperty(pair.Key, out var value) || value.GetString() != pair.Value))
            {
                Console.Error.WriteLine("Refusing to reuse a group not recorded and owned by this lab.");
                return 2;
            }
        }
        else if (exists != "false") throw new InvalidOperationException($"Unexpected group existence response: {exists}");
        var principal = principalArg.Length > 0 ? principalArg :
            (await Az("ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"));
        var providers = compiled.GetProperty("resources").EnumerateArray()
            .Select(resource => resource.GetProperty("type").GetString()!.Split('/')[0])
            .Where(name => name != "Microsoft.Authorization").Distinct().Order().ToArray();
        foreach (var provider in providers)
            await Az("provider", "register", "--namespace", provider, "--wait", "--subscription", subscription);
        var tagsToWrite = Owner.Concat(SecurityTags).Select(pair => $"{pair.Key}={pair.Value}").ToArray();
        var groupArgs = new List<string> { "group", "create", "--name", group, "--location", location, "--tags" };
        groupArgs.AddRange(tagsToWrite);
        groupArgs.AddRange(["--subscription", subscription, "-o", "none"]);
        await Az(groupArgs.ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(State)!);
        var state = new Dictionary<string, object?> { ["subscription"] = subscription, ["resourceGroup"] = group, ["outputs"] = new Dictionary<string, object>() };
        await File.WriteAllTextAsync(State, JsonSerializer.Serialize(state, JsonOptions));
        var deploymentText = await Az("deployment", "group", "create", "--resource-group", group, "--name", "workshop",
            "--subscription", subscription, "--template-file", template, "--parameters", $"principalId={principal}", $"location={location}",
            "--query", "properties.outputs", "-o", "json");
        state["outputs"] = JsonDocument.Parse(deploymentText).RootElement;
        await File.WriteAllTextAsync(State, JsonSerializer.Serialize(state, JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(State)!, "deployment.json"), JsonSerializer.Serialize(state, JsonOptions));
        if (!infraOnly)
        {
            var demo = await Run("dotnet", "run", "--file", "demo.cs");
            Console.Write(demo.Out);
            Console.Error.Write(demo.Error);
            return demo.ExitCode;
        }
        return 0;
    }
}
