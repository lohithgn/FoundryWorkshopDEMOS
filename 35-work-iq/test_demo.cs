#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property DefineConstants=TEST_HARNESS
#:include demo.cs

internal static class TestDemo
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string User = "22222222-2222-2222-2222-222222222222";
    private const string Second = "33333333-3333-3333-3333-333333333333";
    private const string ProjectId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/lab/providers/Microsoft.CognitiveServices/accounts/lab/projects/work-iq";
    private static readonly WorkIq.Demo.Actor Actor = new(User, "user@example.test");
    private static readonly WorkIq.Demo.Config Config = new(Tenant, "model", ProjectId,
        "https://lab.services.ai.azure.com/api/projects/work-iq", ProjectId + "/connections/" + WorkIq.Demo.Connection);
    private static int assertions;

    private static void Equal<T>(T expected, T actual, string label)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Assertion failed ({label}): expected {expected}, got {actual}.");
    }
    private static void True(bool value, string label) => Equal(true, value, label);
    private static void Throws<T>(Action action, string message, string label) where T : Exception
    {
        assertions++;
        try { action(); }
        catch (T ex) when (ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"Assertion failed ({label}): expected {typeof(T).Name} containing '{message}'.");
    }
    private static System.Text.Json.Nodes.JsonNode Answer(WorkIq.Demo.Actor? actor = null) => System.Text.Json.Nodes.JsonNode.Parse("""
        {"id":"resp-one","status":"completed","error":null,"output":[{"type":"message","status":"completed","content":[{"type":"output_text","text":"{\"user_id\":\"22222222-2222-2222-2222-222222222222\",\"user_principal_name\":\"user@example.test\"}"}]}]}
        """)!;

    private static async Task<int> Main()
    {
        var armTokenArgs = WorkIq.Demo.TokenArguments(Tenant, WorkIq.Demo.TokenAudience.Arm);
        var foundryTokenArgs = WorkIq.Demo.TokenArguments(Tenant, WorkIq.Demo.TokenAudience.Foundry);
        var graphTokenArgs = WorkIq.Demo.TokenArguments(Tenant, WorkIq.Demo.TokenAudience.Graph);
        Equal(WorkIq.Demo.ArmResource, WorkIq.Demo.TokenResource(WorkIq.Demo.TokenAudience.Arm),
            "ARM token resource");
        Equal(WorkIq.Demo.FoundryResource, WorkIq.Demo.TokenResource(WorkIq.Demo.TokenAudience.Foundry),
            "Foundry token resource");
        True(WorkIq.Demo.TokenResource(WorkIq.Demo.TokenAudience.Arm) !=
             WorkIq.Demo.TokenResource(WorkIq.Demo.TokenAudience.Foundry),
            "ARM and Foundry audiences are distinct");
        True(armTokenArgs.Contains(WorkIq.Demo.ArmResource) &&
             foundryTokenArgs.Contains(WorkIq.Demo.FoundryResource) &&
             !foundryTokenArgs.Contains(WorkIq.Demo.ArmResource),
            "CLI token requests use the correct control/data-plane resources");
        True(graphTokenArgs.Contains("ms-graph"), "Graph identity uses its own token audience");

        Equal(true, WorkIq.Demo.ConfigureHelpRequested(["--help"]), "connection help option");
        Throws<ArgumentException>(() => WorkIq.Demo.ConfigureHelpRequested(["--typo"]),
            "unsupported", "reject connection option typo");
        Throws<ArgumentException>(() => WorkIq.Demo.ParseArgs(["--typo"]),
            "unsupported", "reject demo option typo");
        Throws<ArgumentException>(() => WorkIq.Demo.ParseArgs(["--help", "--typo"]),
            "unsupported", "reject typo before help shortcut");
        Throws<ArgumentException>(() => WorkIq.Demo.ParseArgs(["--verify-isolation", "a.json", "b.json", "extra"]),
            "unsupported", "reject trailing isolation argument");
        Throws<ArgumentException>(() => WorkIq.Demo.ParseArgs(["--validate-config", "--ask-only"]),
            "cannot be combined", "reject live option combined with validation");
        Equal(3, WorkIq.Demo.ParseArgs(["--ask-only", "--repetitions", "3"]).Repetitions,
            "accept valid repetitions");

        var props = WorkIq.Demo.ConnectionBody(Tenant, Second, "secret-for-test-only")["properties"]!;
        Equal("OAuth2", props["authType"]!.GetValue<string>(), "OAuth auth type");
        Equal("RemoteA2A", props["category"]!.GetValue<string>(), "RemoteA2A category");
        Equal(WorkIq.Demo.Target, props["target"]!.GetValue<string>(), "A2A target");
        Equal(Tenant, props["AuthorizationUrl"]!.GetValue<string>()!.Split('/')[3], "tenant-pinned authorization URL");
        True((props["Scopes"] as System.Text.Json.Nodes.JsonArray)!.Any(s => s!.GetValue<string>() == "offline_access"), "refresh scope");
        WorkIq.Demo.CheckConnection(new System.Text.Json.Nodes.JsonObject
        {
            ["properties"] = props.DeepClone()
        }, Config);
        True(!WorkIq.Demo.Question.Contains(User, StringComparison.OrdinalIgnoreCase) &&
             !WorkIq.Demo.Instructions.Contains(Actor.UserPrincipalName, StringComparison.OrdinalIgnoreCase),
             "no caller identity sent to model");
        var tool = WorkIq.Demo.AgentDefinition(Config)["tools"]![0]!;
        Equal("work_iq_preview", tool["type"]!.GetValue<string>(), "native Work IQ tool");
        Equal(Config.ConnectionId, tool["project_connection_id"]!.GetValue<string>(), "project-scoped connection");
        Throws<ArgumentException>(() => WorkIq.Demo.ConnectionBody(Tenant, Second, ""), "empty", "secret required");

        var binding = WorkIq.Demo.MakeBinding(Config, Actor, "1");
        Equal(Actor, WorkIq.Demo.ValidateAnswer(Answer(), Actor), "Graph-verified identity");
        Throws<InvalidOperationException>(() => WorkIq.Demo.ValidateAnswer(
            System.Text.Json.Nodes.JsonNode.Parse(Answer().ToJsonString().Replace(User, Second, StringComparison.Ordinal))!, Actor),
            "does not match", "reject different browser account");
        var unfinished = Answer();
        unfinished["status"] = "failed";
        Throws<InvalidOperationException>(() => WorkIq.Demo.ValidateAnswer(unfinished, Actor), "did not complete", "reject incomplete response");
        Throws<InvalidOperationException>(() => WorkIq.Demo.CheckConnection(System.Text.Json.Nodes.JsonNode.Parse("""
            {"properties":{"authType":"ManagedIdentity","category":"RemoteA2A","target":"https://workiq.svc.cloud.microsoft/a2a/"}}
            """), Config), "OAuth2", "reject managed identity");
        var wrongTenant = new System.Text.Json.Nodes.JsonObject
        {
            ["properties"] = props.DeepClone()
        };
        wrongTenant["properties"]!["TokenUrl"] = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
        Throws<InvalidOperationException>(() => WorkIq.Demo.CheckConnection(wrongTenant, Config), "tenant", "reject cross-tenant OAuth");

        var folder = Path.Combine(Directory.GetCurrentDirectory(), ".dotnet-test-work-iq");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, ".azure"));
            var deployment = new System.Text.Json.Nodes.JsonObject
            {
                ["outputs"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["tenantId"] = new System.Text.Json.Nodes.JsonObject { ["value"] = Tenant },
                    ["deployment"] = new System.Text.Json.Nodes.JsonObject { ["value"] = "model" },
                    ["projectResourceId"] = new System.Text.Json.Nodes.JsonObject { ["value"] = ProjectId },
                    ["projectEndpoint"] = new System.Text.Json.Nodes.JsonObject { ["value"] = Config.ProjectEndpoint }
                }
            };
            File.WriteAllText(Path.Combine(folder, ".azure", "deployment.json"), deployment.ToJsonString());
            Equal(Config, WorkIq.Demo.LoadConfig(folder, new Dictionary<string, string?>()),
                "read and validate deployment outputs offline");
            Throws<InvalidOperationException>(() => WorkIq.Demo.LoadConfig(folder,
                new Dictionary<string, string?> { ["FOUNDRY_PROJECT_ENDPOINT"] = "https://wrong.example" }),
                "must match", "reject mismatched project destination");
            var statePath = Path.Combine(folder, "consent.json");
            var consent = System.Text.Json.Nodes.JsonNode.Parse("""
                {"id":"resp-consent","output":[{"type":"oauth_consent_request","consent_link":"https://consent.example.test/login?state=opaque"}]}
                """)!;
            True(WorkIq.Demo.ValidateConsent(consent, binding, statePath), "recognize consent request");
            Equal("resp-consent", WorkIq.Demo.ResumeResponse(statePath, binding), "resume response ID");
            if (!OperatingSystem.IsWindows())
            {
                Equal(0x180, (int)File.GetUnixFileMode(statePath), "private consent file permissions");
                File.SetUnixFileMode(statePath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                WorkIq.Demo.WritePrivateJson(statePath, WorkIq.Demo.BindingNode(binding));
                Equal(0x180, (int)File.GetUnixFileMode(statePath), "restrict permissions before rewriting existing consent");
            }
            var wrong = binding with { Version = "2" };
            Throws<InvalidOperationException>(() => WorkIq.Demo.ResumeResponse(statePath, wrong), "different", "bind consent to version");
            Throws<InvalidOperationException>(() => WorkIq.Demo.ValidateConsent(System.Text.Json.Nodes.JsonNode.Parse("""
                {"id":"x","output":[{"type":"oauth_consent_request","consent_link":"http://bad.example"}]}
                """), binding, Path.Combine(folder, "bad.json")), "invalid", "HTTPS consent only");
            var expired = WorkIq.Demo.BindingNode(binding);
            expired["response_id"] = "old";
            expired["consent_link"] = "https://consent.example.test";
            expired["created_at"] = DateTimeOffset.UtcNow.AddHours(-2).ToString("O");
            File.WriteAllText(statePath, expired.ToJsonString());
            Throws<InvalidOperationException>(() => WorkIq.Demo.ResumeResponse(statePath, binding), "expired", "expire consent state");

            var actor2 = new System.Text.Json.Nodes.JsonObject { ["user_id"] = Second, ["user_principal_name"] = "second@example.test" };
            System.Text.Json.Nodes.JsonObject Report(System.Text.Json.Nodes.JsonObject actor, string response) => new()
            {
                ["complete"] = true, ["tenant"] = Tenant, ["project"] = Config.ProjectEndpoint,
                ["connection"] = Config.ConnectionId, ["agent"] = WorkIq.Demo.Agent, ["version"] = "1",
                ["question_sha256"] = binding.QuestionSha256, ["actor"] = actor.DeepClone(),
                ["cases"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["response_id"] = response, ["answer"] = actor.DeepClone() })
            };
            var reportA = Report(new System.Text.Json.Nodes.JsonObject { ["user_id"] = User, ["user_principal_name"] = Actor.UserPrincipalName }, "resp-A");
            var reportB = Report(actor2, "resp-B");
            var aPath = Path.Combine(folder, "a.json");
            var bPath = Path.Combine(folder, "b.json");
            File.WriteAllText(aPath, reportA.ToJsonString());
            File.WriteAllText(bPath, reportB.ToJsonString());
            var result = WorkIq.Demo.VerifyIsolation(aPath, bPath);
            True(result["complete"]!.GetValue<bool>(), "two-user independent report");
            Equal(2, result["independent_responses"]!.GetValue<int>(), "distinct responses");
            reportB["cases"]![0]!["response_id"] = "resp-A";
            File.WriteAllText(bPath, reportB.ToJsonString());
            Throws<InvalidOperationException>(() => WorkIq.Demo.VerifyIsolation(aPath, bPath), "shared", "reject shared response");
            reportB["cases"]![0]!["response_id"] = "resp-B";
            reportB["cases"]![0]!["answer"]!["user_id"] = User;
            File.WriteAllText(bPath, reportB.ToJsonString());
            Throws<InvalidOperationException>(() => WorkIq.Demo.VerifyIsolation(aPath, bPath), "match", "reject mismatched answer");
        }
        finally { Directory.Delete(folder, true); }

        Console.WriteLine($"PASS: {assertions} offline assertions.");
        await Task.CompletedTask;
        return 0;
    }
}
