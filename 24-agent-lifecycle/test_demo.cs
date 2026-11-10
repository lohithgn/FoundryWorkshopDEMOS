#!/usr/bin/env dotnet
#:package Azure.Identity@1.21.0
#:include demo.cs
#:property StartupObject=LifecycleTests

using System.Text.Json.Nodes;
using System.Net;
using Azure.Core;

public sealed class LifecycleTests
{
    private sealed class FakeService : Lifecycle.IService
    {
        public readonly List<string> Versions = [];
        public readonly List<string> Selected = [];
        public readonly List<string> ExpectedAnswers = [];
        public bool FailChangedInvoke;
        private int versionNumber;
        public Task<string> CreateVersionAsync(string name, string deployment, string instructions) =>
            Task.FromResult((++versionNumber).ToString());
        public Task<JsonNode> PublishAsync(string id, string name) =>
            Task.FromResult<JsonNode>(new JsonObject { ["properties"] = new JsonObject { ["provisioningState"] = "Succeeded" } });
        public Task<JsonNode> SelectVersionAsync(string id, string name, string version, int timeoutSeconds = 300)
        {
            Selected.Add(version);
            return Task.FromResult<JsonNode>(new JsonObject { ["properties"] = new JsonObject { ["state"] = "Running" } });
        }

        public Task InvokeAsync(string endpoint, string version, string expected, JsonObject report)
        {
            Versions.Add(version);
            ExpectedAnswers.Add(expected);
            if (FailChangedInvoke && version == "2") throw new InvalidOperationException("bad rollout");
            return Task.CompletedTask;
        }
        public Task<JsonNode> ActivityLogAsync(string subscription, string deploymentId, string startedAt) =>
            Task.FromResult<JsonNode>(new JsonObject { ["value"] = new JsonArray() });
    }

    private sealed class FixedCredential : TokenCredential
    {
        public readonly List<string> Scopes = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.AddRange(requestContext.Scopes);
            return new AccessToken("offline-token", DateTimeOffset.UtcNow.AddMinutes(5));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed record CapturedRequest(string Url, string? Authorization, string RequestId, string Body);
    private sealed class ResponseHandler : HttpMessageHandler
    {
        public readonly List<CapturedRequest> Requests = [];
        public HttpStatusCode StatusCode = HttpStatusCode.OK;
        public string Output = "USD 25";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Headers.GetValues("x-ms-client-request-id").Single(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            var json = StatusCode == HttpStatusCode.OK
                ? new JsonObject
                {
                    ["id"] = "resp", ["status"] = "completed",
                    ["output"] = new JsonArray(new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = Output })
                    }),
                    ["usage"] = new JsonObject { ["input_tokens"] = 3 }
                }.ToJsonString()
                : """{"error":"denied"}""";
            return new HttpResponseMessage(StatusCode) { Content = new StringContent(json) };
        }
    }

    private sealed class ArmHandler(string state, string agentName = "agent", string version = "2") : HttpMessageHandler
    {
        public string? PutBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put) PutBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = request.Method == HttpMethod.Put ? "{}" :
                new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["state"] = state,
                        ["agents"] = new JsonArray(new JsonObject { ["agentName"] = agentName, ["agentVersion"] = version })
                    }
                }.ToJsonString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected {expected}, got {actual}");
    }
    private static void True(bool value, string label)
    {
        if (!value) throw new Exception(label);
    }

    private static async Task TestManagedDeploymentContract()
    {
        var body = Lifecycle.DeploymentBody("agent", "2")["properties"]!;
        Equal("Managed", (string?)body["deploymentType"], "deployment type");
        Equal("Responses", (string?)body["protocols"]![0]!["protocol"], "protocol");
        Equal("1.0", (string?)body["protocols"]![0]!["version"], "protocol version");
        Equal("agent", (string?)body["agents"]![0]!["agentName"], "agent name");
        Equal("2", (string?)body["agents"]![0]!["agentVersion"], "agent version");
        Equal("Ignore", (string?)body["tags"]!["SecurityControl"], "security tag");
        Equal("StorageAccountKeyAccess", (string?)body["tags"]!["SecurityExemption"], "security exemption");
        await Task.CompletedTask;
    }

    private static async Task TestApplicationTags()
    {
        var body = Lifecycle.ApplicationBody("agent");
        True(body["tags"] is null, "tags belong inside application properties");
        Equal("StorageAccountKeyAccess", (string?)body["properties"]!["tags"]!["SecurityExemption"], "app security tag");
        await Task.CompletedTask;
    }

    private static async Task TestRolloutAndRollback()
    {
        var fake = new FakeService();
        var report = new JsonObject { ["started_at"] = "2026-10-25T00:00:00Z" };
        await Lifecycle.RunLifecycleAsync(fake, "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/a/projects/p",
            "https://a.services.ai.azure.com/api/projects/p", "model", report);
        True((bool)report["rollback_verified"]!, "rollback verification flag");
        Equal("1,2,1", string.Join(",", fake.Selected), "selected versions");
        Equal("1,2,1", string.Join(",", fake.Versions), "invoked versions");
        Equal("USD 25,USD 40,USD 25", string.Join(",", fake.ExpectedAnswers), "expected outputs");
        True(((string?)report["endpoint"])!.EndsWith("/applications/" + ((string?)report["application_id"])!.Split("/applications/")[1] +
            "/protocols/openai/responses"), "real application endpoint");
        True(((string?)report["application_id"])!.Contains("/applications/"), "published application ID");
    }

    private static async Task TestFailedRolloutStillRollsBack()
    {
        var fake = new FakeService { FailChangedInvoke = true };
        var report = new JsonObject { ["started_at"] = "2026-10-25T00:00:00Z" };
        try
        {
            await Lifecycle.RunLifecycleAsync(fake, "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/a/projects/p",
                "https://a.services.ai.azure.com/api/projects/p", "model", report);
            throw new Exception("changed invocation should fail");
        }
        catch (InvalidOperationException ex) when (ex.Message == "bad rollout") { }
        Equal("1,2,1", string.Join(",", fake.Selected), "rollback after failed rollout");
        True((bool)report["rollback_verified"]!, "rollback verification after failed rollout");
    }

    private static async Task TestResponseExtractionAndVersionCheck()
    {
        var result = JsonNode.Parse("""{"output":[{"content":[{"type":"output_text","text":" USD "},{"type":"output_text","text":"25 "}]}]}""");
        Equal("USD 25", Lifecycle.ResponseText(result), "response text extraction");
        var deployment = JsonNode.Parse("""{"properties":{"state":"Running","agents":[{"agentName":"agent","agentVersion":"2"}]}}""");
        True(Lifecycle.IsExpectedDeployment(deployment, "agent", "2"), "expected running version");
        True(!Lifecycle.IsExpectedDeployment(deployment, "agent", "3"), "stale version rejected");
        await Task.CompletedTask;
    }

    private static async Task TestResourceIdValidation()
    {
        True(Lifecycle.ValidateProjectResourceId(
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/acct/projects/proj",
            out var account, out var project), "valid Bicep resource ID");
        Equal("acct", account, "account extraction");
        Equal("proj", project, "project extraction");
        True(!Lifecycle.ValidateProjectResourceId("/subscriptions/sub/projects/proj", out _, out _), "invalid resource ID rejection");
        await Task.CompletedTask;
    }

    private static async Task TestInvocationHttpAndAuthenticationContract()
    {
        var handler = new ResponseHandler();
        using var http = new HttpClient(handler);
        var credential = new FixedCredential();
        var report = new JsonObject { ["requests"] = new JsonArray() };
        await new Lifecycle.AzureService(http, credential, "https://project").InvokeAsync(
            "https://app/responses", "1", "USD 25", report);
        Equal(2, handler.Requests.Count, "question request count");
        Equal("https://ai.azure.com/.default,https://ai.azure.com/.default", string.Join(",", credential.Scopes), "data-plane audience");
        Equal("Bearer " + "offline-token", handler.Requests[0].Authorization, "Entra bearer");
        True(handler.Requests[0].Url.EndsWith("?api-version=" + Lifecycle.DataVersion), "Responses API version");
        True(handler.Requests[0].RequestId.Length == 32, "client request ID");
        Equal("What is the workshop price?", (string?)JsonNode.Parse(handler.Requests[0].Body)!["input"], "question body");
        Equal(2, ((JsonArray)report["requests"]!).Count, "request report records");

        handler.StatusCode = HttpStatusCode.Forbidden;
        try
        {
            await new Lifecycle.AzureService(http, credential, "https://project").InvokeAsync(
                "https://app/responses", "1", "USD 25", new JsonObject { ["requests"] = new JsonArray() });
            throw new Exception("non-200 invocation should fail");
        }
        catch (RuntimeError ex) when (ex.Message.Contains("invocation failed (403)", StringComparison.Ordinal)) { }

        handler.StatusCode = HttpStatusCode.OK;
        handler.Output = "USD 40";
        try
        {
            await new Lifecycle.AzureService(http, credential, "https://project").InvokeAsync(
                "https://app/responses", "1", "USD 25", new JsonObject { ["requests"] = new JsonArray() });
            throw new Exception("mismatched output should fail");
        }
        catch (RuntimeError ex) when (ex.Message.Contains("mismatch", StringComparison.Ordinal)) { }
    }

    private static async Task TestFailedAndStaleDeploymentsAreRejected()
    {
        var failedHandler = new ArmHandler("Failed");
        using (var http = new HttpClient(failedHandler))
        {
            var credential = new FixedCredential();
            try
            {
                await new Lifecycle.AzureService(http, credential, "https://project")
                    .SelectVersionAsync("/deployment", "agent", "2");
                throw new Exception("failed deployment should be rejected");
            }
            catch (RuntimeError ex) when (ex.Message.Contains("Deployment not running", StringComparison.Ordinal)) { }
            var body = JsonNode.Parse(failedHandler.PutBody!)!["properties"]!;
            Equal("Managed", (string?)body["deploymentType"], "ARM managed deployment request");
            Equal("https://management.azure.com/.default", credential.Scopes.First(), "ARM audience");
        }
        using var staleHttp = new HttpClient(new ArmHandler("Running", "agent", "1"));
        try
        {
            await new Lifecycle.AzureService(staleHttp, new FixedCredential(), "https://project")
                .SelectVersionAsync("/deployment", "agent", "2", timeoutSeconds: 0);
            throw new Exception("stale deployment should time out");
        }
        catch (TimeoutException ex) when (ex.Message.Contains("did not become Running", StringComparison.Ordinal)) { }
    }

    private static async Task TestLifecycleReportArtifact()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), ".offline-lifecycle-report-" + Guid.NewGuid().ToString("N") + ".json");
        var report = new JsonObject
        {
            ["started_at"] = "2026-10-25T00:00:00Z",
            ["rollback_verified"] = true,
            ["requests"] = new JsonArray(new JsonObject { ["configured_version"] = "1", ["correct"] = true })
        };
        try
        {
            await LifecycleApp.WriteReportAsync(path, report);
            var text = await File.ReadAllTextAsync(path);
            True(text.EndsWith("\n", StringComparison.Ordinal), "report trailing newline");
            var persisted = JsonNode.Parse(text)!;
            Equal("2026-10-25T00:00:00Z", (string?)persisted["started_at"], "persisted start time");
            True((bool)persisted["rollback_verified"]!, "persisted rollback verification");
            Equal("1", (string?)persisted["requests"]![0]!["configured_version"], "persisted request version");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("managed deployment contract", TestManagedDeploymentContract),
            ("application security tags", TestApplicationTags),
            ("rollout and rollback", TestRolloutAndRollback),
            ("rollback after failed rollout", TestFailedRolloutStillRollsBack),
            ("response and stale-version validation", TestResponseExtractionAndVersionCheck),
            ("project resource ID validation", TestResourceIdValidation),
            ("invocation HTTP and auth contract", TestInvocationHttpAndAuthenticationContract),
            ("failed and stale deployment rejection", TestFailedAndStaleDeploymentsAreRejected),
            ("lifecycle report output artifact", TestLifecycleReportArtifact)
        };
        foreach (var test in tests)
        {
            await test.Run();
            Console.WriteLine($"PASS {test.Name}");
        }
        Console.WriteLine($"Passed {tests.Length} offline lifecycle tests.");
        return 0;
    }
}
