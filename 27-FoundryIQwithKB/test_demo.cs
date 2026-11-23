#!/usr/bin/env dotnet
#:include demo.cs
#:property StartupObject=OfflineEntryPoint

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

internal static class OfflineEntryPoint
{
    public static async Task Main()
    {
        var suite = new OfflineTests();
        await suite.RunAsync();
    }
}

internal sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request);
}

internal sealed class OfflineTests
{
    private int _passed;
    private static readonly LabConfig Config = new(
        "https://foundry.openai.azure.com", "gpt-4.1-mini", "text-embedding-3-small",
        "https://foundry.services.ai.azure.com/api/projects/surface-support",
        "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/foundry/projects/surface-support",
        "https://search.search.windows.net",
        "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Search/searchServices/search",
        "https://storage.blob.core.windows.net",
        "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/storage");
    private static readonly string Home = Lab.FindHome();

    internal async Task RunAsync()
    {
        await Test("managed Blob source uses identity, embedding, and hourly refresh", SourceDefinition);
        await TestAsync("all eight synthetic blobs upload, read back, and remain unchanged", SeedUploadAndReadback);
        await TestAsync("Blob readback mismatch fails", BlobMismatch);
        await Test("agent definition uses the KB MCP connection", AgentDefinition);
        await TestAsync("portal Search and project-identity MCP connections are created", Connections);
        await TestAsync("run evidence artifact is written as readable JSON", RunArtifact);
        await TestAsync("ingestion waits for success and index visibility", IngestionSuccess);
        await TestAsync("ingestion failures, warnings, and timeout fail explicitly", IngestionFailuresAndTimeout);
        await TestAsync("refresh waits for a new indexer result, not a stale success", StaleIngestion);
        await TestAsync("a new indexer is run and polled", NewIndexer);
        await TestAsync("knowledge configuration keeps two managed sources and extractive retrieval", ConfigureKnowledge);
        await TestAsync("matching chunk counts do not hide missing Blob coverage", CoverageMismatch);
        await TestAsync("retrieval asks for activity and source references", Retrieval);
        await Test("agent response requires a successful MCP call and cited evidence", ResponseValidation);
        await TestAsync("HTTP failures are explicit, not retried, and use bearer auth", HttpFailure);
        await Test("missing deployment configuration fails before Azure access", MissingConfiguration);
        await Test("lab root discovery depends on C# assets, not Python", RootDiscovery);
        await TestAsync("403 retries are visible and bounded", RbacRetry);
        await TestAsync("Foundry REST calls retain the actual agent lifecycle and Responses API", FoundryRestCalls);
        Console.WriteLine($"Passed {_passed} offline .NET regression tests.");
    }

    private async Task Test(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
        await Task.CompletedTask;
    }

    private async Task TestAsync(string name, Func<Task> test)
    {
        await test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static AzureApi Api(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send,
        Action<string>? write = null, Func<TimeSpan, Task>? delay = null,
        Func<string, CancellationToken, Task<string>>? token = null)
    {
        var client = new HttpClient(new FakeHandler(send)) { Timeout = Timeout.InfiniteTimeSpan };
        return new AzureApi(Config, client,
            token ?? ((_, _) => Task.FromResult("test-token")),
            delay ?? (_ => Task.CompletedTask), write, ownsClient: true);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body = "", string contentType = "application/json")
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };
        return response;
    }

    private static HttpResponseMessage Json(string body) => Reply(HttpStatusCode.OK, body);

    private static async Task<string> Body(HttpRequestMessage request) =>
        request.Content is null ? "" : await request.Content.ReadAsStringAsync();

    private static JsonObject Source(string indexer = "managed", string index = "chunks") =>
        Lab.Object(("azureBlobParameters", Lab.Object(
            ("createdResources", Lab.Object(
                ("indexer", JsonValue.Create(indexer)),
                ("index", JsonValue.Create(index)))))));

    private static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"Expected <{expected}>, got <{actual}>.");
    }

    private static void True(bool value, string? message = null)
    {
        if (!value) throw new InvalidOperationException(message ?? "Expected condition to be true.");
    }

    private static void Throws<TException>(Action action, string? contains = null) where TException : Exception
    {
        try { action(); }
        catch (TException exception)
        {
            if (contains is not null && !exception.ToString().Contains(contains, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Expected exception to contain '{contains}', got: {exception}");
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action, string? contains = null)
        where TException : Exception
    {
        try { await action(); }
        catch (TException exception)
        {
            if (contains is not null && !exception.ToString().Contains(contains, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Expected exception to contain '{contains}', got: {exception}");
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void SourceDefinition()
    {
        foreach (var (container, name) in new[]
                 {
                     ("support-cases", "surface-support-cases"),
                     ("surface-kb", "surface-reference")
                 })
        {
            var source = FoundryDemo.SourceDefinition(Config, container, name);
            Equal("azureBlob", Lab.Text(source, "kind"));
            var blob = Lab.Child(source, "azureBlobParameters")!;
            Equal($"ResourceId={Config.StorageResourceId}", Lab.Text(blob, "connectionString"));
            Equal(container, Lab.Text(blob, "containerName"));
            var ingestion = Lab.Child(blob, "ingestionParameters")!;
            Equal("PT1H", Lab.Text(ingestion["ingestionSchedule"], "interval"));
            Equal("minimal", Lab.Text(ingestion, "contentExtractionMode"));
            Equal("text-embedding-3-small",
                Lab.Text(Lab.Child(ingestion["embeddingModel"], "azureOpenAIParameters"), "modelName"));
            True(ingestion["ingestionPermissionOptions"] is null);
        }
    }

    private static async Task SeedUploadAndReadback()
    {
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var puts = 0;
        var scopes = new List<string>();
        using var api = Api(async request =>
        {
            var url = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Put)
            {
                Equal("BlockBlob", request.Headers.GetValues("x-ms-blob-type").Single());
                blobs[url] = await request.Content!.ReadAsByteArrayAsync();
                puts++;
                return Reply(HttpStatusCode.Created);
            }
            if (!blobs.TryGetValue(url, out var content))
                return Reply(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        }, token: (scope, _) =>
        {
            scopes.Add(scope);
            return Task.FromResult("test-token");
        });

        var previous = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            await FoundryDemo.SeedStorageAsync(api, Home);
            Equal(8, blobs.Count);
            True(blobs.Values.All(content => Encoding.UTF8.GetString(content).Contains("SYNTHETIC WORKSHOP DATA")));
            True(scopes.Count >= 16 && scopes.All(scope => scope == Lab.StorageScope),
                "Blob reads and writes must use the Storage Entra scope.");
            await FoundryDemo.SeedStorageAsync(api, Home);
            Equal(8, puts, "Unchanged blobs must be read/verified without another PUT.");
        }
        finally { Console.SetOut(previous); }
    }

    private static async Task BlobMismatch()
    {
        using var api = Api(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("wrong"u8.ToArray())
        }));
        await ThrowsAsync<InvalidOperationException>(() => FoundryDemo.SeedStorageAsync(api, Home), "readback mismatch");
    }

    private static void AgentDefinition()
    {
        var definition = FoundryDemo.AgentDefinition(Config);
        var tool = (JsonObject)Lab.Array(definition, "tools")![0]!;
        Equal("mcp", Lab.Text(tool, "type"));
        Equal(Config.McpEndpoint, Lab.Text(tool, "server_url"));
        Equal(Lab.ConnectionName, Lab.Text(tool, "project_connection_id"));
        Equal("knowledge_base_retrieve", Lab.Array(tool, "allowed_tools")![0]!.GetValue<string>());
        Equal("never", Lab.Text(tool, "require_approval"));
        True(Lab.Text(definition, "instructions")!.Contains("I don't know based on this knowledge base"));
    }

    private static async Task Connections()
    {
        var requests = new List<(HttpMethod Method, string Url, string Body)>();
        var scopes = new List<string>();
        using var api = Api(async request =>
        {
            var body = await Body(request);
            requests.Add((request.Method, request.RequestUri!.ToString(), body));
            if (request.Method == HttpMethod.Put)
                return Reply(HttpStatusCode.OK, body);
            var saved = requests.Last(item => item.Method == HttpMethod.Put && item.Url == request.RequestUri!.ToString());
            return Reply(HttpStatusCode.OK, saved.Body);
        }, token: (scope, _) =>
        {
            scopes.Add(scope);
            return Task.FromResult("test-token");
        });
        var result = await FoundryDemo.ConnectProjectAsync(api);
        Equal("CognitiveSearch", Lab.Text(Lab.Child(result["search"], "properties"), "category"));
        var search = requests.First(item => item.Method == HttpMethod.Put && item.Url.Contains("surface-iq-search"));
        var mcp = requests.First(item => item.Method == HttpMethod.Put && item.Url.Contains(Lab.ConnectionName));
        Equal("CognitiveSearch", Lab.Text(JsonNode.Parse(search.Body)?["properties"], "category"));
        var mcpProperties = JsonNode.Parse(mcp.Body)?["properties"];
        Equal("ProjectManagedIdentity", Lab.Text(mcpProperties, "authType"));
        Equal("https://search.azure.com/", Lab.Text(mcpProperties, "audience"));
        True(requests.All(item => item.Url.Contains($"api-version={Lab.ArmVersion}")));
        Equal(4, scopes.Count);
        True(scopes.All(scope => scope == Lab.ArmScope));
    }

    private static async Task RunArtifact()
    {
        var state = Path.Combine(Home, ".test-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            var report = Lab.Object(
                ("complete", JsonValue.Create(false)),
                ("agent", JsonValue.Create(Lab.AgentName)),
                ("question", JsonValue.Create("offline artifact test")),
                ("response", Lab.Object(("status", JsonValue.Create("incomplete")))));
            await FoundryDemo.WriteRunReportAsync(state, report);
            var path = Path.Combine(state, "run.json");
            True(File.Exists(path));
            var contents = await File.ReadAllTextAsync(path);
            True(contents.EndsWith('\n'));
            var saved = JsonNode.Parse(contents)!;
            Equal(false, saved["complete"]!.GetValue<bool>());
            Equal(Lab.AgentName, Lab.Text(saved, "agent"));
            Equal("offline artifact test", Lab.Text(saved, "question"));
            Equal("incomplete", Lab.Text(saved["response"], "status"));
        }
        finally
        {
            Directory.Delete(state, recursive: true);
            var parent = Path.GetDirectoryName(state)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
    }

    private static async Task IngestionSuccess()
    {
        var statuses = new Queue<string>(
        [
            """{"lastResult":{"status":"inProgress"}}""",
            """{"lastResult":{"status":"inProgress"}}""",
            """{"lastResult":{"status":"success","itemsFailed":0,"warnings":[]}}"""
        ]);
        var methods = new List<HttpMethod>();
        using var api = Api(request =>
        {
            methods.Add(request.Method);
            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
                return Task.FromResult(Json(statuses.Dequeue()));
            if (request.RequestUri.AbsolutePath.EndsWith("/$count", StringComparison.Ordinal))
                return Task.FromResult(Reply(HttpStatusCode.OK, "4", "text/plain"));
            return Task.FromResult(Json("{}"));
        });
        var previous = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            var result = await FoundryDemo.WaitForIngestionAsync(api, Source(), 4, 30,
                delay: _ => Task.CompletedTask);
            Equal(4, FoundryDemo.Number(result, "chunks"));
            True(methods.All(method => method is not null));
        }
        finally { Console.SetOut(previous); }
    }

    private static async Task IngestionFailuresAndTimeout()
    {
        foreach (var last in new[]
                 {
                     """{"status":"persistentFailure"}""",
                     """{"status":"transientFailure"}""",
                     """{"status":"success","errors":[{"message":"bad"}]}""",
                     """{"status":"success","itemsFailed":1}""",
                     """{"status":"success","warnings":[{"message":"embedding failed"}]}"""
                 })
        {
            var statuses = new Queue<string>([
                """{"lastResult":{"status":"inProgress"}}""",
                $"{{\"lastResult\":{last}}}"
            ]);
            using var api = Api(_ => Task.FromResult(Json(statuses.Dequeue())));
            await ThrowsAsync<InvalidOperationException>(
                () => FoundryDemo.WaitForIngestionAsync(api, Source(), 4, 30), "ingestion");
        }

        var tick = 0;
        using var timeoutApi = Api(_ => Task.FromResult(Json("""{"lastResult":{"status":"inProgress"}}""")));
        await ThrowsAsync<TimeoutException>(() => FoundryDemo.WaitForIngestionAsync(
            timeoutApi, Source(), 4, 30,
            monotonic: () => tick++,
            delay: _ => Task.CompletedTask), "did not finish");
    }

    private static async Task StaleIngestion()
    {
        var statuses = new Queue<string>(
        [
            """{"lastResult":{"status":"success","startTime":"old"}}""",
            """{"lastResult":{"status":"success","startTime":"old"}}""",
            """{"lastResult":{"status":"success","startTime":"new"}}"""
        ]);
        var sleeps = 0;
        using var api = Api(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
                return Task.FromResult(Json(statuses.Dequeue()));
            if (request.RequestUri.AbsolutePath.EndsWith("/$count", StringComparison.Ordinal))
                return Task.FromResult(Reply(HttpStatusCode.OK, "4", "text/plain"));
            return Task.FromResult(Json("{}"));
        }, delay: _ => { sleeps++; return Task.CompletedTask; });
        var previous = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            var result = await FoundryDemo.WaitForIngestionAsync(api, Source(), 4, 30,
                delay: _ => { sleeps++; return Task.CompletedTask; });
            Equal("new", Lab.Text(result["lastResult"], "startTime"));
            Equal(1, sleeps);
        }
        finally { Console.SetOut(previous); }
    }

    private static async Task NewIndexer()
    {
        var statuses = new Queue<string>(
        [
            """{"lastResult":null}""",
            """{}""",
            """{"lastResult":{"status":"success","startTime":"new"}}"""
        ]);
        var run = false;
        using var api = Api(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
                return Task.FromResult(Json(statuses.Dequeue()));
            if (request.RequestUri.AbsolutePath.EndsWith("/run", StringComparison.Ordinal))
            {
                run = true;
                return Task.FromResult(Json("{}"));
            }
            return Task.FromResult(Reply(HttpStatusCode.OK, "4", "text/plain"));
        });
        var previous = Console.Out;
        Console.SetOut(TextWriter.Null);
        try { await FoundryDemo.WaitForIngestionAsync(api, Source("i", "x"), 4, 30); }
        finally { Console.SetOut(previous); }
        True(run);
    }

    private static async Task ConfigureKnowledge()
    {
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        var statusCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        string? savedKnowledgeBase = null;
        using var api = Api(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/knowledgesources/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
                return Json("{}");
            if (path.Contains("/knowledgesources/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                var name = path.Split('/').Last();
                var index = name + "-index";
                stored[index] = name == "surface-support-cases" ? "support-cases" : "surface-kb";
                return Json(Lab.Object(
                    ("kind", JsonValue.Create("azureBlob")),
                    ("azureBlobParameters", Lab.Object(
                        ("createdResources", Lab.Object(
                            ("indexer", JsonValue.Create(name + "-indexer")),
                            ("index", JsonValue.Create(index))))))).ToJsonString());
            }
            if (path.Contains("/indexers/", StringComparison.Ordinal) && path.EndsWith("/status", StringComparison.Ordinal))
            {
                var indexer = path.Split('/')[^2];
                statusCalls.TryGetValue(indexer, out var calls);
                statusCalls[indexer] = calls + 1;
                return Json(calls == 0
                    ? """{"lastResult":{"status":"inProgress"}}"""
                    : """{"lastResult":{"status":"success","itemsFailed":0,"warnings":[]}}""");
            }
            if (path.EndsWith("/$count", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, "4", "text/plain");
            if (path.Contains("/docs/search", StringComparison.Ordinal))
            {
                var container = stored[path.Split('/')[^3]];
                var urls = Directory.GetFiles(Path.Combine(Home, "data", container), "*.txt")
                    .Select(file => $"{Config.StorageEndpoint}/{container}/{Path.GetFileName(file)}");
                var docs = new JsonArray(urls.Select(url => (JsonNode?)Lab.Object(
                    ("blob_url", JsonValue.Create(url)),
                    ("snippet", JsonValue.Create("synthetic")))).ToArray());
                return Json(Lab.Object(("value", docs)).ToJsonString());
            }
            if (path.Contains("/knowledgebases/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return Json(savedKnowledgeBase ?? """{"name":"surface-support-kb"}""");
            if (path.Contains("/knowledgebases/", StringComparison.Ordinal))
            {
                savedKnowledgeBase = await Body(request);
                return Json(savedKnowledgeBase);
            }
            throw new InvalidOperationException($"Unexpected request {request.Method} {path}");
        });
        var previous = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            var report = await FoundryDemo.ConfigureKnowledgeAsync(api, Home, 30);
            var kb = Lab.Child(report, "knowledgeBase")!;
            Equal(2, Lab.Array(kb, "knowledgeSources")!.Count);
            Equal("extractiveData", Lab.Text(kb, "outputMode"));
            Equal("low", Lab.Text(kb["retrievalReasoningEffort"], "kind"));
            Equal(Config.Deployment,
                Lab.Text(Lab.Child(Lab.Array(kb, "models")![0], "azureOpenAIParameters"), "deploymentId"));
        }
        finally { Console.SetOut(previous); }
    }

    private static async Task CoverageMismatch()
    {
        var statusCalls = 0;
        using var api = Api(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/knowledgesources/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return Task.FromResult(Json("""{"kind":"azureBlob","azureBlobParameters":{"createdResources":{"indexer":"i","index":"i"}}}"""));
            if (path.Contains("/indexers/", StringComparison.Ordinal) && path.EndsWith("/status", StringComparison.Ordinal))
                return Task.FromResult(Json(statusCalls++ == 0
                    ? """{"lastResult":{"status":"inProgress"}}"""
                    : """{"lastResult":{"status":"success","itemsFailed":0,"warnings":[]}}"""));
            if (path.EndsWith("/$count", StringComparison.Ordinal))
                return Task.FromResult(Reply(HttpStatusCode.OK, "4", "text/plain"));
            if (path.Contains("/docs/search", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"value":[{"blob_url":"https://wrong-blob","snippet":"content"}]}"""));
            return Task.FromResult(Json("{}"));
        });
        await ThrowsAsync<InvalidOperationException>(
            () => FoundryDemo.ConfigureKnowledgeAsync(api, Home, 30), "coverage mismatch");
    }

    private static async Task Retrieval()
    {
        var response = """{"response":[{"content":[]}],"activity":[{"type":"search"}],"references":[{"id":"0"}]}""";
        string? requestBody = null;
        using var api = Api(async request =>
        {
            requestBody = await Body(request);
            return Json(response);
        });
        var previous = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            await FoundryDemo.RetrieveAsync(api, "question");
            var body = JsonNode.Parse(requestBody!)!;
            Equal(2, Lab.Array(body, "knowledgeSourceParams")!.Count);
            True(Lab.Array(body, "knowledgeSourceParams")!.All(source =>
                Lab.Text(source, "kind") == "azureBlob" &&
                source?["includeReferenceSourceData"]!.GetValue<bool>() == true));
            response = """{"response":[{"content":[]}],"activity":[{"error":{"message":"denied"}}]}""";
            await ThrowsAsync<InvalidOperationException>(
                () => FoundryDemo.RetrieveAsync(api, "question"), "contains errors");
        }
        finally { Console.SetOut(previous); }
    }

    private static void ResponseValidation()
    {
        var response = JsonNode.Parse(
            """{"status":"completed","output":[{"type":"mcp_call","name":"knowledge_base_retrieve","output":"{\"source\":\"CASE-1001.txt\"}","error":null},{"type":"message","content":[{"type":"output_text","text":"Resolved with dock firmware (CASE-1001)."}]}]}""")!.AsObject();
        var previous = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            FoundryDemo.ValidateAgentResponse(response);
            foreach (var variant in new[] { "no-call", "error", "no-text", "invented-id", "incomplete" })
            {
                var changed = response.DeepClone().AsObject();
                switch (variant)
                {
                    case "no-call": changed["output"]!.AsArray().RemoveAt(0); break;
                    case "error": changed["output"]![0]!["error"] = JsonValue.Create("403"); break;
                    case "no-text": changed["output"]![1]!["content"] = new JsonArray(); break;
                    case "invented-id": changed["output"]![1]!["content"]![0]!["text"] = "CASE-9999"; break;
                    default: changed["status"] = "incomplete"; break;
                }
                Throws<InvalidOperationException>(() => FoundryDemo.ValidateAgentResponse(changed));
            }
            response["output"]![1]!["content"]![0]!["text"] =
                "I don't know based on this knowledge base";
            FoundryDemo.ValidateAgentResponse(response);
        }
        finally { Console.SetOut(previous); }
    }

    private static async Task HttpFailure()
    {
        var requests = 0;
        var scopes = new List<string>();
        using var api = Api(request =>
        {
            requests++;
            return Task.FromResult(Reply(HttpStatusCode.BadRequest, """{"error":"bad schema"}"""));
        }, token: (scope, _) =>
        {
            scopes.Add(scope);
            return Task.FromResult("test-token");
        });
        await ThrowsAsync<InvalidOperationException>(
            () => api.SearchAsync(HttpMethod.Put, "knowledgesources/source", new JsonObject()),
            "400):");
        Equal(1, requests);
        Equal(Lab.SearchScope, scopes.Single());

        using var authApi = Api(request =>
        {
            Equal("Bearer " + "test-token", request.Headers.Authorization?.ToString());
            True(request.RequestUri!.Query.Contains($"api-version={Lab.ApiVersion}"));
            return Task.FromResult(Json("{}"));
        });
        await authApi.SearchAsync(HttpMethod.Get, "knowledgebases/base");
    }

    private static void MissingConfiguration()
    {
        var saved = Lab.OutputNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in Lab.OutputNames) Environment.SetEnvironmentVariable(name, null);
            Throws<InvalidOperationException>(() => Lab.LoadConfig(Path.Combine(Home, "does-not-exist.json")), "Deploy lab 27");
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static void RootDiscovery()
    {
        var parent = Path.Combine(Home, ".test-lab-roots");
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(Path.Combine(root, "data", "support-cases"));
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllText(Path.Combine(root, "demo.cs"), "");
            True(!File.Exists(Path.Combine(root, "demo.py")));
            Equal(Path.GetFullPath(root), Path.GetFullPath(Lab.FindHome(nested)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
    }

    private static async Task RbacRetry()
    {
        var requests = 0;
        var tokens = 0;
        var messages = new List<string>();
        using var api = Api(_ =>
        {
            requests++;
            return Task.FromResult(Reply(HttpStatusCode.Forbidden, "denied"));
        }, write: messages.Add, token: (_, _) =>
        {
            tokens++;
            return Task.FromResult("test-token");
        });
        await ThrowsAsync<InvalidOperationException>(
            () => api.SearchAsync(HttpMethod.Get, "knowledgebases/base"), "403");
        Equal(11, requests);
        Equal(11, tokens);
        Equal(10, messages.Count);
        True(messages[0].Contains("waiting 30s for RBAC", StringComparison.Ordinal));
    }

    private static async Task FoundryRestCalls()
    {
        var requests = new List<(string Method, string Url, string Body, string? Authorization)>();
        var scopes = new List<string>();
        using var api = Api(async request =>
        {
            var body = await Body(request);
            requests.Add((request.Method.Method, request.RequestUri!.ToString(), body,
                request.Headers.Authorization?.ToString()));
            if (request.RequestUri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
                return Json("""{"name":"surface-support-agent","version":"1"}""");
            return Json("""{"name":"surface-support-agent","version":"1","definition":{}}""");
        }, token: (scope, _) =>
        {
            scopes.Add(scope);
            return Task.FromResult("test-token");
        });
        var agent = await FoundryDemo.CreateAgentVersionAsync(api);
        await FoundryDemo.GetAgentVersionAsync(api, Lab.Text(agent, "version")!);
        var create = requests[0];
        Equal("POST", create.Method);
        True(create.Url.Contains("/agents/surface-support-agent/versions?api-version=v1"));
        Equal("Bearer " + "test-token", create.Authorization);
        var definition = JsonNode.Parse(create.Body)?["definition"];
        Equal("prompt", Lab.Text(definition, "kind"));
        var saved = requests[1];
        Equal("GET", saved.Method);
        True(saved.Url.EndsWith("/agents/surface-support-agent/versions/1?api-version=v1"));
        Equal(2, scopes.Count);
        True(scopes.All(scope => scope == Lab.FoundryScope));

        var responseApiCalls = new List<(string Url, string Body)>();
        using var responseApi = Api(async request =>
        {
            responseApiCalls.Add((request.RequestUri!.ToString(), await Body(request)));
            return Json("""{"status":"completed","output":[]}""");
        });
        await FoundryDemo.AskAgentAsync(responseApi, "question");
        True(responseApiCalls[0].Url.EndsWith("/openai/v1/responses"));
        var bodyJson = JsonNode.Parse(responseApiCalls[0].Body)!;
        Equal("question", Lab.Text(bodyJson, "input"));
        Equal(Lab.AgentName, Lab.Text(bodyJson["agent_reference"], "name"));
        True(Lab.Child(bodyJson, "agent_reference")!["version"] is null);
        Equal("required", Lab.Text(bodyJson, "tool_choice"));
        True(responseApiCalls[0].Url.StartsWith(Config.ProjectEndpoint, StringComparison.Ordinal));
    }
}
