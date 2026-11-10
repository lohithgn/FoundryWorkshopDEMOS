#:property TargetFramework=net10.0
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const string SearchVersion = "2026-08-01-preview";
const string Source = "synthetic-governed-source";
const string BaseName = "synthetic-governed-kb";
const string Marker = "HELIOS-045-ORCHID";
const string Management = "https://management.azure.com";
var lab = Environment.CurrentDirectory;
var state = Path.Combine(lab, ".azure");
if (!File.Exists(Path.Combine(lab, "infra", "main.bicep")))
    throw new DirectoryNotFoundException("Run this file from 45-governed-iq.");
if (args.Contains("--self-test")) { OfflineTests(); return; }
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Permission-native Blob/Search and Fabric data-agent smoke tests.\nUsage: dotnet run --file demo.cs -- [--timeout 600]");
    return;
}
var timeout = ParseTimeout(args);
Directory.CreateDirectory(state);
JsonObject config;
try { config = Config(state); }
catch (Exception error)
{
    Console.Error.WriteLine($"BLOCKED: {error.Message}");
    Environment.ExitCode = 2;
    return;
}
var report = new JsonObject { ["lab"] = 45, ["resourceGroup"] = config["resourceGroup"]!.DeepClone() };
var api = new NativeApi();
try
{
    try
    {
        var setup = await SetupIq(api, config, timeout);
        report["nativeIQSetup"] = setup;
        using var source = await api.Request(HttpMethod.Get, config["storageEndpoint"]!.ToString()
            + "/synthetic-restricted/restricted.txt", "https://storage.azure.com/.default");
        var sourceBytes = await source.Content.ReadAsByteArrayAsync();
        if (source.StatusCode != HttpStatusCode.OK || !Encoding.UTF8.GetString(sourceBytes).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException($"Authorized synthetic source read HTTP {(int)source.StatusCode}; expected 200 and marker.");
        report["authorizedSourceRead"] = new JsonObject { ["status"] = "PASS", ["httpStatus"] = 200 };
        report["nativeIQAccess"] = await TestIqAccess(api, config);
    }
    catch (Exception error) { report["nativeIQ"] = new JsonObject { ["status"] = "BLOCKED", ["evidence"] = SafeError(error) }; }
    try { report["fabricIQ"] = await FabricSmoke(api, config); }
    catch (Exception error) { report["fabricIQ"] = new JsonObject { ["status"] = "BLOCKED", ["evidence"] = SafeError(error) }; }
}
catch (Exception error) { report["error"] = SafeError(error); }
var passed = report["nativeIQAccess"]?["authorizedCaller"]?.ToString() == "PASS"
    && report["nativeIQAccess"]?["twoRealUsers"]?.ToString() == "PASS"
    && report["fabricIQ"]?["status"]?.ToString() == "PASS";
report["status"] = passed ? "PASS" : "BLOCKED";
await Save("live-report.json", report);
Console.WriteLine(report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
if (!passed) Environment.ExitCode = 2;

static int ParseTimeout(string[] args)
{
    var index = Array.IndexOf(args, "--timeout");
    if (index < 0) return 600;
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value) || value <= 0)
        throw new ArgumentException("--timeout must be a positive integer.");
    return value;
}

static JsonObject Config(string state)
{
    var recorded = File.Exists(Path.Combine(state, "deployment.json"))
        ? JsonNode.Parse(File.ReadAllText(Path.Combine(state, "deployment.json")))!.AsObject() : new JsonObject();
    var saved = recorded["outputs"]?.AsObject() ?? new JsonObject();
    var mappings = new Dictionary<string, string> {
        ["endpoint"] = "AZURE_OPENAI_ENDPOINT", ["deployment"] = "AZURE_OPENAI_DEPLOYMENT",
        ["projectEndpoint"] = "FOUNDRY_PROJECT_ENDPOINT", ["projectResourceId"] = "FOUNDRY_PROJECT_RESOURCE_ID",
        ["embeddingDeployment"] = "AZURE_OPENAI_EMBEDDING_DEPLOYMENT", ["storageEndpoint"] = "AZURE_STORAGE_BLOB_ENDPOINT",
        ["storageResourceId"] = "AZURE_STORAGE_RESOURCE_ID", ["containerResourceId"] = "GOVERNED_IQ_CONTAINER_RESOURCE_ID",
        ["searchEndpoint"] = "AZURE_SEARCH_ENDPOINT", ["searchResourceId"] = "AZURE_SEARCH_RESOURCE_ID",
        ["tenantId"] = "AZURE_TENANT_ID", ["principalId"] = "AZURE_PRINCIPAL_ID"
    };
    var result = new JsonObject();
    foreach (var (key, env) in mappings)
        result[key] = Environment.GetEnvironmentVariable(env) is { Length: > 0 } value ? value : saved[key]?["value"]?.ToString();
    var missing = mappings.Keys.Where(key => string.IsNullOrWhiteSpace(result[key]?.ToString())).Select(key => mappings[key]).ToArray();
    if (missing.Length > 0) throw new InvalidOperationException("Missing deployment outputs: " + string.Join(", ", missing));
    foreach (var key in new[] { "endpoint", "projectEndpoint", "storageEndpoint", "searchEndpoint" })
    {
        if (!result[key]!.ToString().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{key} must be HTTPS.");
        result[key] = result[key]!.ToString().TrimEnd('/');
    }
    var group = recorded["resourceGroup"]?.ToString() ?? "rg-foundry-governed-iq-demo";
    if (!Regex.IsMatch(group, "^[a-zA-Z0-9_-]{1,90}$") || !group.EndsWith("-governed-iq-demo", StringComparison.Ordinal))
        throw new ArgumentException("Only a recorded synthetic governed-IQ resource group may be mutated.");
    foreach (var key in new[] { "projectResourceId", "storageResourceId", "containerResourceId", "searchResourceId" })
        if (!result[key]!.ToString().Contains($"/resourceGroups/{group}/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Resource IDs must stay within the recorded synthetic resource group.");
    result["resourceGroup"] = group;
    return result;
}

static JsonObject SourceDefinition(JsonObject config) => new() {
    ["name"] = Source, ["kind"] = "azureBlob", ["description"] = "Synthetic restricted Helios maintenance instructions.",
    ["azureBlobParameters"] = new JsonObject { ["connectionString"] = $"ResourceId={config["storageResourceId"]}/;",
        ["containerName"] = "synthetic-restricted", ["isADLSGen2"] = false,
        ["ingestionParameters"] = new JsonObject {
            ["embeddingModel"] = new JsonObject { ["kind"] = "azureOpenAI", ["azureOpenAIParameters"] = new JsonObject {
                ["resourceUri"] = config["endpoint"]!.DeepClone(), ["deploymentId"] = config["embeddingDeployment"]!.DeepClone(),
                ["modelName"] = "text-embedding-3-small" } },
            ["disableImageVerbalization"] = true, ["contentExtractionMode"] = "minimal",
            ["ingestionPermissionOptions"] = new JsonArray("rbacScope") } }
};

static JsonObject RetrievalBody() => new() {
    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(
        new JsonObject { ["type"] = "text", ["text"] = "What is the Helios restricted maintenance code?" }) }),
    ["knowledgeSourceParams"] = new JsonArray(new JsonObject { ["knowledgeSourceName"] = Source,
        ["kind"] = "azureBlob", ["includeReferences"] = true, ["alwaysQuerySource"] = true })
};

static bool ContainsMarker(JsonNode result) => result.ToJsonString().Contains(Marker, StringComparison.Ordinal);

async Task<JsonObject> SetupIq(NativeApi api, JsonObject config, int timeout)
{
    async Task<JsonObject> Search(HttpMethod method, string path, JsonObject? body = null)
        => await api.Json(method, config["searchEndpoint"]!.ToString() + "/" + path,
            "https://search.azure.com/.default", body, new Dictionary<string, string> { ["api-version"] = SearchVersion });
    var content = await File.ReadAllBytesAsync(Path.Combine(lab, "data", "restricted.txt"));
    if (!Encoding.UTF8.GetString(content).Contains("SYNTHETIC WORKSHOP DATA", StringComparison.Ordinal))
        throw new InvalidDataException("Only synthetic assets may be uploaded.");
    var blob = config["storageEndpoint"]!.ToString() + "/synthetic-restricted/restricted.txt";
    using (var response = await api.Request(HttpMethod.Put, blob, "https://storage.azure.com/.default", content,
        new Dictionary<string, string> { ["x-ms-version"] = "2023-11-03", ["x-ms-date"] = DateTimeOffset.UtcNow.ToString("R"),
            ["x-ms-blob-type"] = "BlockBlob", ["Content-Type"] = "text/plain" }))
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
            throw new InvalidOperationException($"Synthetic blob upload HTTP {(int)response.StatusCode}");
    await Search(HttpMethod.Put, $"knowledgesources/{Source}", SourceDefinition(config));
    var source = await Search(HttpMethod.Get, $"knowledgesources/{Source}");
    var parameters = source["azureBlobParameters"]!;
    if (!(parameters["ingestionParameters"]?["ingestionPermissionOptions"]?.AsArray() ?? new JsonArray()).Select(n => n!.ToString()).SequenceEqual(["rbacScope"]))
        throw new InvalidDataException("Service didn't retain native permission ingestion.");
    var resources = parameters["createdResources"]!;
    var datasource = await Search(HttpMethod.Get, $"datasources/{resources["datasource"]}");
    if (!(datasource["indexerPermissionOptions"]?.AsArray() ?? new JsonArray()).Select(n => n!.ToString()).SequenceEqual(["rbacScope"]))
        throw new InvalidDataException("Generated native data source isn't synchronizing RBAC scope.");
    var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
    JsonObject status;
    while (true)
    {
        status = await Search(HttpMethod.Get, $"indexers/{resources["indexer"]}/status");
        var last = status["lastResult"]?.AsObject() ?? new JsonObject();
        if (last["status"]?.ToString() == "success" && (int?)last["itemsProcessed"] > 0)
        {
            if (last["errors"] is JsonArray { Count: > 0 } || last["warnings"] is JsonArray { Count: > 0 } || (int?)last["itemsFailed"] > 0)
                throw new InvalidOperationException("Native ingestion returned errors/warnings.");
            break;
        }
        if (last["status"]?.ToString() is "persistentFailure" or "transientFailure")
            throw new InvalidOperationException($"Native indexer {last["status"]}; errors={last["errors"]}");
        if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException($"Native ingestion timeout; last status={last["status"]}");
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
    var schema = await Search(HttpMethod.Get, $"indexes/{resources["index"]}");
    var permissionFields = (schema["fields"]?.AsArray() ?? new JsonArray())
        .Where(f => f?["permissionFilter"]?.GetValue<bool>() == true).Select(f => f!["name"]!.ToString()).ToArray();
    if (permissionFields.Length == 0 || schema["permissionFilterOption"]?.ToString() != "enabled")
        throw new InvalidDataException("Generated index lacks enforced native permission fields.");
    await Search(HttpMethod.Put, $"knowledgebases/{BaseName}", new JsonObject { ["name"] = BaseName,
        ["knowledgeSources"] = new JsonArray(new JsonObject { ["name"] = Source }),
        ["models"] = new JsonArray(new JsonObject { ["kind"] = "azureOpenAI", ["azureOpenAIParameters"] = new JsonObject {
            ["resourceUri"] = config["endpoint"]!.DeepClone(), ["deploymentId"] = config["deployment"]!.DeepClone(), ["modelName"] = "gpt-4.1-mini" } }),
        ["outputMode"] = "extractiveData", ["retrievalReasoningEffort"] = new JsonObject { ["kind"] = "low" } });
    var connection = await api.Json(HttpMethod.Put, $"{Management}{config["projectResourceId"]}/connections/synthetic-iq-search",
        $"{Management}/.default", new JsonObject { ["properties"] = new JsonObject { ["category"] = "CognitiveSearch",
            ["authType"] = "AAD", ["target"] = config["searchEndpoint"]!.DeepClone(), ["isSharedToAll"] = false,
            ["metadata"] = new JsonObject { ["ApiType"] = "Azure", ["ResourceId"] = config["searchResourceId"]!.DeepClone() } } },
        query: new Dictionary<string, string> { ["api-version"] = "2025-04-01-preview" });
    return new JsonObject { ["status"] = "PASS", ["index"] = resources["index"]!.DeepClone(),
        ["permissionFields"] = System.Text.Json.JsonSerializer.SerializeToNode(permissionFields),
        ["ingestion"] = "success", ["nativeSourcePermissionSync"] = datasource["indexerPermissionOptions"]!.DeepClone(),
        ["connectionId"] = connection["id"]?.DeepClone() };
}

async Task<JsonObject> Retrieve(NativeApi api, JsonObject config, string? queryToken)
{
    var headers = queryToken is null ? null : new Dictionary<string, string> { ["x-ms-query-source-authorization"] = queryToken };
    return await api.Json(HttpMethod.Post, config["searchEndpoint"]!.ToString() + $"/knowledgebases/{BaseName}/retrieve",
        "https://search.azure.com/.default", RetrievalBody(), headers,
        new Dictionary<string, string> { ["api-version"] = SearchVersion });
}

async Task<JsonObject> TestIqAccess(NativeApi api, JsonObject config)
{
    var positive = await Retrieve(api, config, await AzCli.Token("https://search.azure.com/.default"));
    var noIdentity = await Retrieve(api, config, null);
    if (ContainsMarker(noIdentity) || noIdentity["references"] is JsonArray { Count: > 0 })
        throw new InvalidOperationException("SECURITY FAILURE: omitted source identity exposed restricted content.");
    var result = new JsonObject { ["authorizedCaller"] = ContainsMarker(positive) ? "PASS" : "BLOCKED",
        ["authorizedReferences"] = positive["references"]?.AsArray().Count ?? 0,
        ["missingSourceIdentityDenied"] = "PASS", ["twoRealUsers"] = "BLOCKED" };
    var directory = Environment.GetEnvironmentVariable("SECOND_AZURE_CONFIG_DIR")
        ?? Environment.GetEnvironmentVariable("SECONDARY_AZURE_CONFIG_DIR");
    if (string.IsNullOrEmpty(directory))
    {
        result["twoRealUsersEvidence"] = "SECOND_AZURE_CONFIG_DIR absent; no secondary CLI user created.";
        return result;
    }
    var identity = await AzCli.RunWithEnvironment(directory, "ad", "signed-in-user", "show");
    var secondaryId = JsonNode.Parse(identity)?["id"]?.ToString();
    if (secondaryId == config["principalId"]?.ToString())
        throw new InvalidOperationException("Secondary sign-in is the same principal; cannot prove two-user denial.");
    var storageToken = await AzCli.TokenWithEnvironment(directory, "https://storage.azure.com/.default");
    using var denied = await api.Request(HttpMethod.Get, config["storageEndpoint"]!.ToString()
        + "/synthetic-restricted/restricted.txt", "https://storage.azure.com/.default",
        headersOverride: new Dictionary<string, string> { ["Authorization"] = "Bearer " + storageToken, ["x-ms-version"] = "2023-11-03" },
        allow: [HttpStatusCode.Forbidden]);
    if (denied.StatusCode != HttpStatusCode.Forbidden)
        throw new InvalidOperationException($"Secondary source read HTTP {(int)denied.StatusCode}; expected genuine 403.");
    var queryToken = await AzCli.TokenWithEnvironment(directory, "https://search.azure.com/.default");
    var negative = await Retrieve(api, config, queryToken);
    if (ContainsMarker(negative) || negative["references"] is JsonArray { Count: > 0 })
        throw new InvalidOperationException("SECURITY FAILURE: real unauthorized caller retrieved restricted content.");
    result["twoRealUsers"] = "PASS";
    result["twoRealUsersEvidence"] = "Distinct signed-in user; source read 403; KB marker absent.";
    return result;
}

static JsonObject FabricConnectionBody(string workspace, string artifact) => new() { ["properties"] = new JsonObject {
    ["category"] = "CustomKeys", ["authType"] = "CustomKeys",
    ["credentials"] = new JsonObject { ["keys"] = new JsonObject {
        ["workspace_id"] = Guid.Parse(workspace).ToString(), ["artifact_id"] = Guid.Parse(artifact).ToString() } } } };

static JsonObject FabricDefinition(string model, string connectionId) => new() {
    ["kind"] = "prompt", ["model"] = model, ["instructions"] = "Use Fabric for synthetic sales. Never invent results.",
    ["tools"] = new JsonArray(new JsonObject { ["type"] = "fabric_dataagent_preview",
        ["fabric_dataagent_preview"] = new JsonObject { ["project_connections"] =
            new JsonArray(new JsonObject { ["project_connection_id"] = connectionId }) } })
};

async Task<JsonObject> FabricSmoke(NativeApi api, JsonObject config)
{
    var probes = new JsonObject();
    foreach (var (name, url, scope) in new[] {
        ("fabricCapacities", "https://api.fabric.microsoft.com/v1/capacities", "https://api.fabric.microsoft.com/.default"),
        ("powerBiCapacities", "https://api.powerbi.com/v1.0/myorg/capacities", "https://analysis.windows.net/powerbi/api/.default"),
        ("armFabricCapacities", "https://management.azure.com/subscriptions/1217c203-6c76-47b4-b61c-9bba62dc8e02/providers/Microsoft.Fabric/capacities?api-version=2023-11-01", "https://management.azure.com/.default")
    })
    {
        try { var payload = await api.Json(HttpMethod.Get, url, scope); probes[name] = new JsonObject { ["status"] = "PASS", ["count"] = payload["value"]?.AsArray().Count ?? 0 }; }
        catch (Exception error) { probes[name] = new JsonObject { ["status"] = "BLOCKED", ["evidence"] = SafeError(error) }; }
    }
    await Save("fabric-preflight.json", probes);
    var workspace = Environment.GetEnvironmentVariable("FABRIC_WORKSPACE_ID");
    var artifact = Environment.GetEnvironmentVariable("FABRIC_DATA_AGENT_ID");
    if (string.IsNullOrEmpty(workspace) || string.IsNullOrEmpty(artifact)
        || Environment.GetEnvironmentVariable("FABRIC_SYNTHETIC_ONLY") != "1")
        throw new InvalidOperationException("Fabric designated synthetic workspace/data agent IDs and FABRIC_SYNTHETIC_ONLY=1 absent; capacity/license probes saved in .azure/fabric-preflight.json.");
    workspace = Guid.Parse(workspace).ToString();
    artifact = Guid.Parse(artifact).ToString();
    var item = await api.Json(HttpMethod.Get,
        $"https://api.fabric.microsoft.com/v1/workspaces/{workspace}/items/{artifact}", "https://api.fabric.microsoft.com/.default");
    if (!(item["displayName"]?.ToString() ?? "").StartsWith("synthetic-governed-iq", StringComparison.Ordinal))
        throw new InvalidOperationException("Designated Fabric item isn't labeled synthetic-governed-iq; no content queried.");
    var connection = await api.Json(HttpMethod.Put, $"{Management}{config["projectResourceId"]}/connections/synthetic-fabric-iq",
        $"{Management}/.default", FabricConnectionBody(workspace, artifact),
        query: new Dictionary<string, string> { ["api-version"] = "2025-04-01-preview" });
    var projectToken = await AzCli.Token("https://ai.azure.com/.default");
    var project = new NativeProject(config["projectEndpoint"]!.ToString(), projectToken);
    var agent = await project.Send(HttpMethod.Post, "agents/synthetic-fabric-iq/versions",
        new JsonObject { ["definition"] = FabricDefinition(config["deployment"]!.ToString(), connection["id"]!.ToString()) });
    var response = await OpenAI(config["endpoint"]!.ToString(), "responses", new JsonObject {
        ["tool_choice"] = "required", ["input"] = "Using the synthetic sales data, what is total revenue? Return the total."
    }, "synthetic-fabric-iq");
    var text = ResponseText(response);
    var calls = (response["output"]?.AsArray() ?? new JsonArray()).Where(item => item?["type"]?.ToString().Contains("fabric", StringComparison.OrdinalIgnoreCase) == true).ToArray();
    if (response["status"]?.ToString() != "completed" || !text.Contains("400", StringComparison.Ordinal)
        || calls.Length == 0 || calls.Any(item => item?["status"]?.ToString() is "failed" or "incomplete" or "cancelled" || item?["error"] is not null))
        throw new InvalidOperationException("Actual Fabric tool query didn't return expected synthetic revenue with a successful native tool execution.");
    return new JsonObject { ["status"] = "PASS", ["agent"] = "synthetic-fabric-iq",
        ["version"] = agent["version"]?.DeepClone(), ["connectionId"] = connection["id"]?.DeepClone(),
        ["toolCalls"] = new JsonArray(calls.Select(item => JsonValue.Create(item?["type"]?.ToString())).ToArray()), ["expectedRevenue"] = 400 };
}

async Task<JsonObject> OpenAI(string endpoint, string route, JsonObject body, string agent)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/openai/v1/" + route);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AzCli.Token("https://ai.azure.com/.default"));
    request.Headers.TryAddWithoutValidation("agent-name", agent);
    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
    using var response = await http.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Responses API HTTP {(int)response.StatusCode}; {ErrorCode(text)}");
    return JsonNode.Parse(text)!.AsObject();
}

static string ResponseText(JsonObject response) => string.Concat((response["output"]?.AsArray() ?? new JsonArray())
    .SelectMany(item => item?["content"]?.AsArray() ?? new JsonArray())
    .Where(item => item?["type"]?.ToString() == "output_text").Select(item => item?["text"]?.ToString() ?? ""));

static string SafeError(Exception error)
{
    if (error is InvalidOperationException or ArgumentException) return error.Message;
    var codes = Regex.Matches(error.ToString(), "AADSTS\\d+").Select(m => m.Value).Distinct().Order().ToArray();
    if (codes.Length > 0) return $"{error.GetType().Name}; Entra codes={string.Join(",", codes)}; token not issued";
    return $"{error.GetType().Name}; service body omitted";
}

static string ErrorCode(string body)
{
    try { return JsonNode.Parse(body)?["error"]?["code"]?.ToString() ?? "service error"; }
    catch { return "service error"; }
}

async Task Save(string name, JsonObject report)
{
    Directory.CreateDirectory(state);
    await File.WriteAllTextAsync(Path.Combine(state, name), report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
}

void OfflineTests()
{
    var config = new JsonObject {
        ["endpoint"] = "https://synthetic.openai.azure.com", ["deployment"] = "gpt-4.1-mini",
        ["embeddingDeployment"] = "text-embedding-3-small", ["storageResourceId"] = "/subscriptions/s/resourceGroups/rg-foundry-governed-iq-demo/providers/Microsoft.Storage/storageAccounts/synthetic",
        ["storageEndpoint"] = "https://synthetic.blob.core.windows.net", ["searchEndpoint"] = "https://synthetic.search.windows.net",
        ["searchResourceId"] = "/subscriptions/s/resourceGroups/rg-foundry-governed-iq-demo/providers/Microsoft.Search/searchServices/s",
        ["projectResourceId"] = "/subscriptions/s/resourceGroups/rg-foundry-governed-iq-demo/providers/Microsoft.CognitiveServices/accounts/a/projects/p",
        ["projectEndpoint"] = "https://synthetic.services.ai.azure.com/api/projects/p",
        ["containerResourceId"] = "/subscriptions/s/resourceGroups/rg-foundry-governed-iq-demo/providers/Microsoft.Storage/storageAccounts/s/blobServices/default/containers/c",
        ["tenantId"] = "a1d6c791-d37f-46f2-860a-105cc2a00fe3", ["principalId"] = "2953e18f-c581-471a-b474-35522c43acab"
    };
    var source = SourceDefinition(config);
    if (source["azureBlobParameters"]?["ingestionParameters"]?["ingestionPermissionOptions"]?.ToJsonString() != "[\"rbacScope\"]"
        || source.ToJsonString().Contains("apiKey", StringComparison.OrdinalIgnoreCase)
        || source["azureBlobParameters"]?["isADLSGen2"]?.GetValue<bool>() != false)
        throw new InvalidDataException("Native Blob source permission/keyless contract failed.");
    var query = RetrievalBody().ToJsonString();
    if (query.Contains("$filter", StringComparison.Ordinal) || query.Contains("\"filter\"", StringComparison.Ordinal))
        throw new InvalidDataException("Query may not contain an application-generated permission filter.");
    var valid = FabricConnectionBody("b0cd853b-1c15-4bce-a0f1-71461a93f2c9", "62c8bb78-478a-41d2-ade7-0f5493369ecb");
    if (!valid["properties"]!["credentials"]!["keys"]!.ToJsonString().Contains("workspace_id", StringComparison.Ordinal)
        || valid.ToJsonString().Contains("secret", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Fabric connection must retain GUIDs only.");
    ExpectFailure(() => FabricConnectionBody("not-a-guid", "not-a-guid"), "Guid");
    var leak = new JsonObject { ["response"] = Marker };
    if (!ContainsMarker(leak)) throw new InvalidDataException("Authorized retrieval fixture check failed.");
    ExpectFailure(() => CheckUnauthorizedLeak(leak), "SECURITY FAILURE");
    var path = Path.Combine(lab, "data", "restricted.txt");
    if (!File.ReadAllText(path).Contains("SYNTHETIC WORKSHOP DATA", StringComparison.Ordinal))
        throw new InvalidDataException("Restricted fixture is not explicitly synthetic.");
    Console.WriteLine("PASS: native RBAC source, no-filter retrieval, two-user denial and Fabric GUID contracts.");
}

static void CheckUnauthorizedLeak(JsonNode data)
{
    if (ContainsMarker(data)) throw new InvalidOperationException("SECURITY FAILURE: omitted source identity exposed restricted content.");
}

static void ExpectFailure(Action action, string message)
{
    try { action(); }
    catch (Exception error) when (error.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new InvalidDataException($"Expected failure containing '{message}'.");
}

sealed class NativeApi
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    public async Task<HttpResponseMessage> Request(HttpMethod method, string url, string scope, byte[]? body = null,
        IDictionary<string, string>? headers = null, IDictionary<string, string>? query = null,
        IDictionary<string, string>? headersOverride = null, HttpStatusCode[]? allow = null)
    {
        if (query is not null)
        {
            var builder = new UriBuilder(url);
            var items = System.Web.HttpUtility.ParseQueryString(builder.Query);
            foreach (var pair in query) items[pair.Key] = pair.Value;
            builder.Query = items.ToString();
            url = builder.Uri.ToString();
        }
        var token = headersOverride is not null && headersOverride.TryGetValue("Authorization", out var authorization)
            ? authorization.Replace("Bearer ", "", StringComparison.Ordinal) : await AzCli.Token(scope);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            else request.Headers.TryAddWithoutValidation(key, value);
        foreach (var (key, value) in headersOverride ?? new Dictionary<string, string>())
            if (!key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) request.Headers.TryAddWithoutValidation(key, value);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (headers?.TryGetValue("Content-Type", out var contentType) == true)
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }
        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode && !(allow?.Contains(response.StatusCode) ?? false))
        {
            var status = response.StatusCode;
            var requestId = response.Headers.TryGetValues("x-ms-request-id", out var ids) ? ids.FirstOrDefault() : "unavailable";
            var code = NativeErrors.ErrorCode(await response.Content.ReadAsStringAsync());
            response.Dispose();
            request.Dispose();
            throw new InvalidOperationException($"{method} {url}: HTTP {(int)status}, code={code}, requestId={requestId}");
        }
        return response;
    }
    public async Task<JsonObject> Json(HttpMethod method, string url, string scope, JsonObject? payload = null,
        IDictionary<string, string>? headers = null, IDictionary<string, string>? query = null)
    {
        var bytes = payload is null ? null : Encoding.UTF8.GetBytes(payload.ToJsonString());
        using var response = await Request(method, url, scope, bytes, headers, query);
        var content = await response.Content.ReadAsStringAsync();
        return string.IsNullOrEmpty(content) ? new JsonObject() : JsonNode.Parse(content)!.AsObject();
    }
}

sealed class NativeProject(string endpoint, string token)
{
    private readonly Uri _endpoint = new(endpoint.TrimEnd('/') + "/");
    public async Task<JsonObject> Send(HttpMethod method, string path, JsonObject body)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var builder = new UriBuilder(new Uri(_endpoint, path.TrimStart('/')));
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        query["api-version"] = "v1";
        builder.Query = query.ToString();
        using var request = new HttpRequestMessage(method, builder.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Native agent API HTTP {(int)response.StatusCode}: {NativeErrors.ErrorCode(text)}");
        return JsonNode.Parse(text)!.AsObject();
    }
}

static class NativeErrors
{
    public static string ErrorCode(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["code"]?.ToString() ?? "service error"; }
        catch { return "service error"; }
    }
}

static class AzCli
{
    public static async Task<string> Token(string scope)
        => (await Run("account", "get-access-token", "--scope", scope, "--query", "accessToken", "-o", "tsv")).Trim();
    public static async Task<string> TokenWithEnvironment(string directory, string scope)
    {
        var result = await RunWithEnvironment(directory, "account", "get-access-token", "--scope", scope, "--query", "accessToken", "-o", "tsv");
        return result.Trim();
    }
    public static Task<string> Run(params string[] args) => RunWithEnvironment(null, args);
    public static async Task<string> RunWithEnvironment(string? directory, params string[] args)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (directory is not null) start.Environment["AZURE_CONFIG_DIR"] = directory;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Azure CLI.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Azure CLI failed ({process.ExitCode}): {stderr.Trim()}");
        return stdout;
    }
}
