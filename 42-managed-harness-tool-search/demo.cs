#:property TargetFramework=net10.0
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

const string LearnMcp = "https://learn.microsoft.com/api/mcp";
var lab = Environment.CurrentDirectory;
var state = Path.Combine(lab, ".azure");
if (!File.Exists(Path.Combine(lab, "infra", "main.bicep")))
    throw new DirectoryNotFoundException("Run this file from 42-managed-harness-tool-search.");
if (args.Contains("--self-test")) { OfflineTests(); return; }
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Verify a managed Copilot harness, immutable native skill, and Toolbox search.\nUsage: dotnet run --file demo.cs -- [--preflight]");
    return;
}
var preflight = args.Contains("--preflight");
Directory.CreateDirectory(state);
var report = new JsonObject { ["status"] = "BLOCKED", ["harness_status"] = "NOT_RUN",
    ["tool_search_status"] = "NOT_RUN", ["blockers"] = new JsonArray() };
try
{
    var config = Settings(state);
    var token = await AzCli.Token("https://ai.azure.com/.default");
    var project = new NativeProject(config["projectEndpoint"]!.ToString(), token);
    if (preflight)
    {
        var deployments = await project.Get("deployments");
        if (!(deployments["data"]?.AsArray() ?? deployments["value"]?.AsArray() ?? new JsonArray())
            .Any(d => d?["name"]?.ToString() == config["deployment"]?.ToString()))
            throw new InvalidOperationException("The configured native harness model deployment is missing.");
        Console.WriteLine("PASS preflight: Entra identity, project access, current native API.");
        return;
    }

    try
    {
        await ToolboxSearch(project, config, token, report);
        report["tool_search_status"] = "PASS";
    }
    catch (Exception error)
    {
        report["tool_search_status"] = "BLOCKED";
        report["blockers"]!.AsArray().Add(Blocker("tool_search", error));
    }
    try
    {
        await ManagedHarness(project, config, report);
        report["harness_status"] = "PASS";
    }
    catch (Exception error)
    {
        report["harness_status"] = "BLOCKED";
        report["blockers"]!.AsArray().Add(Blocker("harness", error));
    }
    report["status"] = report["blockers"]!.AsArray().Count == 0 ? "PASS" : "BLOCKED";
}
catch (Exception error)
{
    report["blockers"]!.AsArray().Add(Blocker("preflight", error));
}
await Save(report);
Console.WriteLine(new JsonObject { ["status"] = report["status"]!.DeepClone(), ["harness"] = report["harness_status"]!.DeepClone(),
    ["tool_search"] = report["tool_search_status"]!.DeepClone(), ["blockers"] = report["blockers"]!.DeepClone() }
    .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
if (report["status"]?.ToString() != "PASS") Environment.ExitCode = 1;

static JsonObject Settings(string state)
{
    var outputs = File.Exists(Path.Combine(state, "deployment.json"))
        ? JsonNode.Parse(File.ReadAllText(Path.Combine(state, "deployment.json")))?["outputs"]?.AsObject() ?? new JsonObject()
        : new JsonObject();
    var mapping = new Dictionary<string, string> {
        ["endpoint"] = "AZURE_OPENAI_ENDPOINT", ["projectEndpoint"] = "FOUNDRY_PROJECT_ENDPOINT",
        ["deployment"] = "AZURE_OPENAI_DEPLOYMENT", ["projectResourceId"] = "FOUNDRY_PROJECT_RESOURCE_ID"
    };
    var result = new JsonObject();
    foreach (var (key, env) in mapping)
        result[key] = Environment.GetEnvironmentVariable(env) is { Length: > 0 } value ? value : outputs[key]?["value"]?.ToString();
    foreach (var key in mapping.Keys)
        if (string.IsNullOrWhiteSpace(result[key]?.ToString()))
            throw new InvalidOperationException($"BLOCKED: missing {key}; deploy with dotnet run --file deploy_lab.cs -- --infra-only");
    return result;
}

static byte[] SkillPackage(string name = "workshop-brief")
{
    var source = Path.Combine(Environment.CurrentDirectory, "skills", "workshop-brief", "SKILL.md");
    var skill = File.ReadAllText(source).Replace("name: workshop-brief", "name: " + name, StringComparison.Ordinal);
    using var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
    using (var writer = new StreamWriter(zip.CreateEntry("SKILL.md").Open(), new UTF8Encoding(false)))
        writer.Write(skill);
    return stream.ToArray();
}

static void VerifyDefinition(JsonObject definition, string skill, string version)
{
    if (definition["harness"]?["type"]?.ToString() != "microsoft_copilot_preview")
        throw new InvalidOperationException("Persisted agent does not use the native Microsoft Copilot harness.");
    if (!(definition["skills"]?.AsArray() ?? new JsonArray()).Any(item =>
        item?["name"]?.ToString() == skill && item?["version"]?.ToString() == version))
        throw new InvalidOperationException("Persisted agent is missing its immutable native skill reference.");
}

static void VerifyMetaTools(IEnumerable<string> names)
{
    if (!names.ToHashSet(StringComparer.Ordinal).SetEquals(["tool_search", "call_tool"]))
        throw new InvalidOperationException($"Native meta-tools must hide unpinned tools; received {string.Join(", ", names.Order())}");
}

static JsonObject FindTool(JsonNode data)
{
    if (data is JsonObject obj)
    {
        var name = obj["name"]?.ToString() ?? obj["tool_name"]?.ToString() ?? "";
        if (name.Contains("microsoft_docs_search", StringComparison.Ordinal)) return obj;
        foreach (var child in obj.Select(pair => pair.Value))
            if (child is not null)
                try { return FindTool(child); } catch (InvalidOperationException) { }
    }
    else if (data is JsonArray array)
        foreach (var child in array)
            if (child is not null)
                try { return FindTool(child); } catch (InvalidOperationException) { }
    throw new InvalidOperationException("Native search did not discover the read-only Learn docs search tool.");
}

async Task ToolboxSearch(NativeProject project, JsonObject config, string token, JsonObject report)
{
    var suffix = Guid.NewGuid().ToString("N")[..10];
    var name = "native-search-" + suffix;
    try
    {
        var body = new JsonObject { ["description"] = "Read-only official Learn connector with native deferred discovery.",
            ["tools"] = new JsonArray(
                new JsonObject { ["type"] = "mcp", ["server_label"] = "learn", ["server_url"] = LearnMcp,
                    ["require_approval"] = "never" },
                new JsonObject { ["type"] = "tool_search" }) };
        var toolbox = await project.Send(HttpMethod.Post, $"toolboxes/{name}/versions", body);
        var version = toolbox["version"]?.ToString() ?? throw new InvalidDataException("Native toolbox response has no immutable version.");
        report["toolbox"] = toolbox.DeepClone();
        await Save(report);
        var readback = await project.Get($"toolboxes/{name}/versions/{Uri.EscapeDataString(version)}");
        if (!(readback["tools"]?.AsArray() ?? new JsonArray()).Any(t => t?["type"]?.ToString() == "toolbox_search"))
            throw new InvalidOperationException("Service read-back lacks the native toolbox_search definition.");
        var mcpUrl = config["projectEndpoint"]!.ToString().TrimEnd('/') +
            $"/toolboxes/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(version)}/mcp?api-version=v1";
        await using var mcp = new StreamableMcp(mcpUrl, token);
        var listing = await mcp.InitializeAndList();
        var tools = listing["tools"]?.AsArray() ?? new JsonArray();
        VerifyMetaTools(tools.Select(t => t?["name"]?.ToString() ?? ""));
        report["initial_tools"] = listing.DeepClone();
        var searchSchema = tools.First(t => t?["name"]?.ToString() == "tool_search")?["inputSchema"]?.AsObject()
            ?? throw new InvalidDataException("Native tool_search has no input schema.");
        report["search_schema"] = searchSchema.DeepClone();
        var properties = searchSchema["properties"]?.AsObject() ?? new JsonObject();
        var queryKey = properties.ContainsKey("query") ? "query" : properties.ContainsKey("search_query") ? "search_query" : null;
        if (queryKey is null) throw new InvalidDataException($"Unsupported native search schema: {searchSchema}");
        var result = await mcp.Call("tool_search", new JsonObject { [queryKey] = "microsoft_docs_search official Foundry Copilot harness documentation" });
        report["tool_search"] = result.DeepClone();
        var tool = FindTool(result);
        var callSchema = tools.First(t => t?["name"]?.ToString() == "call_tool")?["inputSchema"]?.AsObject()
            ?? throw new InvalidDataException("Native call_tool has no input schema.");
        var callProps = callSchema["properties"]?.AsObject() ?? new JsonObject();
        var nameKey = callProps.ContainsKey("tool_name") ? "tool_name" : callProps.ContainsKey("name") ? "name" : null;
        var argsKey = callProps.ContainsKey("arguments") ? "arguments" : callProps.ContainsKey("tool_arguments") ? "tool_arguments" : null;
        if (nameKey is null || argsKey is null) throw new InvalidDataException($"Unsupported native call_tool schema: {callSchema}");
        var discoveredName = tool["name"]?.ToString() ?? tool["tool_name"]?.ToString()
            ?? throw new InvalidDataException("Discovered tool is missing its name.");
        var called = await mcp.Call("call_tool", new JsonObject { [nameKey] = discoveredName,
            [argsKey] = new JsonObject { ["query"] = "Microsoft Foundry Copilot harness" } });
        if (called["isError"]?.GetValue<bool>() == true || called["content"] is not JsonArray { Count: > 0 })
            throw new InvalidOperationException("Native call_tool did not return official documentation.");
        report["call_tool"] = called.DeepClone();
        report["tool_search_status"] = "PASS";
    }
    finally
    {
        try { await project.Send(HttpMethod.Delete, $"toolboxes/{name}"); report["toolbox_deleted"] = true; }
        catch (Exception error) { report["toolbox_cleanup_error"] = error.Message; throw; }
        await Save(report);
    }
}

async Task ManagedHarness(NativeProject project, JsonObject config, JsonObject report)
{
    var suffix = Guid.NewGuid().ToString("N")[..10];
    var agentName = "native-harness-" + suffix;
    var skillName = "workshop-brief-" + suffix;
    var skillCreated = false;
    var agentCreated = false;
    string? roleId = null;
    string? conversation = null;
    try
    {
        var multipart = new MultipartFormDataContent();
        var zip = new ByteArrayContent(SkillPackage(skillName));
        zip.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        multipart.Add(zip, "files", "skill.zip");
        var skill = await project.SendMultipart(HttpMethod.Post, $"skills/{Uri.EscapeDataString(skillName)}/versions", multipart);
        skillCreated = true;
        var version = skill["version"]?.ToString() ?? throw new InvalidDataException("Native immutable skill version is absent.");
        var definition = new JsonObject {
            ["kind"] = "prompt", ["model"] = config["deployment"]!.ToString(),
            ["instructions"] = "Use the workshop-brief skill for a synthetic workshop brief about native tool discovery.",
            ["harness"] = new JsonObject { ["type"] = "microsoft_copilot_preview" },
            ["skills"] = new JsonArray(new JsonObject { ["name"] = skillName, ["version"] = version }),
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "microsoft_copilot_toolset_preview",
                ["default_config"] = new JsonObject { ["enabled"] = false },
                ["configs"] = new JsonArray(new JsonObject { ["name"] = "filesystem_read", ["enabled"] = true }) })
        };
        var created = await project.Send(HttpMethod.Post, $"agents/{Uri.EscapeDataString(agentName)}/versions",
            new JsonObject { ["definition"] = definition });
        agentCreated = true;
        var agentVersion = created["version"]?.ToString() ?? throw new InvalidDataException("Native agent version is missing.");
        var persisted = await project.Get($"agents/{Uri.EscapeDataString(agentName)}/versions/{Uri.EscapeDataString(agentVersion)}");
        var storedDefinition = persisted["definition"]?.AsObject() ?? throw new InvalidDataException("Agent definition read-back is missing.");
        VerifyDefinition(storedDefinition, skillName, version);
        report["agent"] = persisted.DeepClone();
        var principal = persisted["instance_identity"]?["principal_id"]?.ToString();
        if (string.IsNullOrEmpty(principal))
            principal = (await project.Get($"agents/{Uri.EscapeDataString(agentName)}"))["instance_identity"]?["principal_id"]?.ToString();
        if (string.IsNullOrEmpty(principal)) throw new InvalidOperationException("Native harness agent has no instance identity.");
        var role = JsonNode.Parse(await AzCli.Run("role", "assignment", "create", "--assignee-object-id", principal,
            "--assignee-principal-type", "ServicePrincipal", "--role", "53ca6127-db72-4b80-b1b0-d745d6d5456d",
            "--scope", config["projectResourceId"]!.ToString(), "-o", "json"))!.AsObject();
        roleId = role["id"]?.ToString();
        report["agent_identity"] = principal;
        var convo = await OpenAIRequest(config["endpoint"]!.ToString(), HttpMethod.Post, "conversations", new JsonObject());
        conversation = convo["id"]?.ToString() ?? throw new InvalidDataException("Native conversation ID missing.");
        var response = await OpenAIRequest(config["endpoint"]!.ToString(), HttpMethod.Post, "responses", new JsonObject {
            ["conversation"] = conversation, ["input"] = "Create a synthetic workshop brief about native tool discovery using the workshop-brief skill."
        }, agentName);
        report["response"] = response.DeepClone();
        if (response["status"]?.ToString() != "completed" || !ResponseText(response).Contains("NATIVE-SKILL-42", StringComparison.Ordinal))
            throw new InvalidOperationException("Native harness response did not demonstrate the pinned skill.");
        report["harness_status"] = "PASS";
    }
    finally
    {
        if (conversation is not null)
            await OpenAIRequest(config["endpoint"]!.ToString(), HttpMethod.Delete, $"conversations/{Uri.EscapeDataString(conversation)}", null);
        if (agentCreated)
            await project.Send(HttpMethod.Delete, $"agents/{Uri.EscapeDataString(agentName)}");
        if (skillCreated)
            await project.Send(HttpMethod.Delete, $"skills/{Uri.EscapeDataString(skillName)}");
        if (roleId is not null)
            await AzCli.Run("role", "assignment", "delete", "--ids", roleId, "-o", "none");
    }
}

static async Task<JsonObject> OpenAIRequest(string endpoint, HttpMethod method, string route, JsonObject? payload, string? agent = null)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
    using var request = new HttpRequestMessage(method, endpoint.TrimEnd('/') + "/openai/v1/" + route);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AzCli.Token("https://ai.azure.com/.default"));
    if (agent is not null) request.Headers.TryAddWithoutValidation("agent-name", agent);
    if (payload is not null) request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Native Responses API {method} {route}: HTTP {(int)response.StatusCode}, {ErrorCode(body)}");
    return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
}

static string ResponseText(JsonObject response) => string.Concat((response["output"]?.AsArray() ?? new JsonArray())
    .SelectMany(item => item?["content"]?.AsArray() ?? new JsonArray())
    .Where(item => item?["type"]?.ToString() == "output_text").Select(item => item?["text"]?.ToString() ?? ""));

static JsonObject Blocker(string feature, Exception error) => new() { ["feature"] = feature, ["type"] = error.GetType().Name, ["message"] = error.Message };

static string ErrorCode(string body)
{
    try { return JsonNode.Parse(body)?["error"]?["code"]?.ToString() ?? "service error"; }
    catch { return "service error"; }
}

static async Task Save(JsonObject report)
{
    var state = Path.Combine(Environment.CurrentDirectory, ".azure");
    Directory.CreateDirectory(state);
    var path = Path.Combine(state, "live-report.json");
    var temporary = Path.Combine(state, "live-report.pending");
    await File.WriteAllTextAsync(temporary, report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    File.Move(temporary, path, true);
}

void OfflineTests()
{
    var definition = new JsonObject { ["harness"] = new JsonObject { ["type"] = "microsoft_copilot_preview" },
        ["skills"] = new JsonArray(new JsonObject { ["name"] = "brief", ["version"] = "1" }) };
    VerifyDefinition(definition, "brief", "1");
    ExpectFailure(() => VerifyDefinition(new JsonObject { ["skills"] = new JsonArray() }, "brief", "1"), "native Microsoft Copilot");
    ExpectFailure(() => VerifyDefinition(new JsonObject { ["harness"] = definition["harness"]!.DeepClone(),
        ["skills"] = new JsonArray(new JsonObject { ["name"] = "brief", ["version"] = "2" }) }, "brief", "1"), "immutable native skill");
    VerifyMetaTools(["tool_search", "call_tool"]);
    ExpectFailure(() => VerifyMetaTools(["tool_search", "call_tool", "learn.microsoft_docs_search"]), "hide unpinned");
    ExpectFailure(() => VerifyMetaTools(["call_tool"]), "hide unpinned");
    var tool = FindTool(new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "learn.microsoft_docs_search" }) });
    if (tool["name"]?.ToString() != "learn.microsoft_docs_search") throw new InvalidDataException("Nested tool discovery failed.");
    ExpectFailure(() => FindTool(new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "delete_resource" }) }), "did not discover");
    var zipBytes = SkillPackage("brief-123");
    using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
    using (var reader = new StreamReader(zip.GetEntry("SKILL.md")!.Open()))
    {
        var skill = reader.ReadToEnd();
        if (zip.Entries.Count != 1 || !skill.Contains("name: brief-123", StringComparison.Ordinal)
            || !skill.Contains("NATIVE-SKILL-42", StringComparison.Ordinal))
            throw new InvalidDataException("Native immutable skill package contract failed.");
    }
    Console.WriteLine("PASS: harness, pinned skill, tool-discovery and restricted meta-tool offline contracts.");
}

static void ExpectFailure(Action action, string message)
{
    try { action(); }
    catch (Exception error) when (error.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new InvalidDataException($"Expected failure containing '{message}'.");
}

sealed class NativeProject(string endpoint, string token)
{
    private readonly Uri _endpoint = new(endpoint.TrimEnd('/') + "/");
    public Task<JsonObject> Get(string path) => Send(HttpMethod.Get, path);
    public async Task<JsonObject> Send(HttpMethod method, string path, JsonObject? payload = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        using var request = new HttpRequestMessage(method, AddVersion(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload is not null) request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Native project API {method} {path}: HTTP {(int)response.StatusCode}, {ErrorCode(body)}");
        return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
    }
    public async Task<JsonObject> SendMultipart(HttpMethod method, string path, MultipartFormDataContent multipart)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        using var request = new HttpRequestMessage(method, AddVersion(path)) { Content = multipart };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Native project API {method} {path}: HTTP {(int)response.StatusCode}, {ErrorCode(body)}");
        return JsonNode.Parse(body)!.AsObject();
    }
    private Uri AddVersion(string path)
    {
        var builder = new UriBuilder(new Uri(_endpoint, path.TrimStart('/')));
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        query["api-version"] = "v1";
        builder.Query = query.ToString();
        return builder.Uri;
    }
    private static string ErrorCode(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["code"]?.ToString() ?? "service error"; }
        catch { return "service error"; }
    }
}

sealed class StreamableMcp(string url, string token) : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private string? _session;
    private int _id;
    public async Task<JsonObject> InitializeAndList()
    {
        var init = await Send("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26",
            ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "lab42-file-app", ["version"] = "1.0" } });
        if (init["protocolVersion"] is null) throw new InvalidDataException("MCP server did not complete initialization.");
        await Notify("notifications/initialized", new JsonObject());
        return await Send("tools/list", new JsonObject());
    }
    public Task<JsonObject> Call(string name, JsonObject arguments)
        => Send("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments });
    private async Task Notify(string method, JsonObject parameters)
    {
        using var request = CreateRequest(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });
        using var response = await _http.SendAsync(request);
        if (response.StatusCode is not (System.Net.HttpStatusCode.Accepted or System.Net.HttpStatusCode.OK))
            throw new InvalidOperationException($"MCP notification failed with HTTP {(int)response.StatusCode}.");
        SetSession(response);
    }
    private async Task<JsonObject> Send(string method, JsonObject parameters)
    {
        var id = ++_id;
        using var request = CreateRequest(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        SetSession(response);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"MCP {method} failed with HTTP {(int)response.StatusCode}.");
        var json = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n').FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal))?[5..].Trim())
            .LastOrDefault(value => !string.IsNullOrEmpty(value)) ?? body;
        var envelope = JsonNode.Parse(json)!.AsObject();
        if (envelope["error"] is JsonNode error) throw new InvalidOperationException($"Native MCP {method} error: {error["message"]}");
        return envelope["result"]?.AsObject() ?? new JsonObject();
    }
    private HttpRequestMessage CreateRequest(JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-03-26");
        if (_session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }
    private void SetSession(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) _session = values.FirstOrDefault();
    }
    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}

static class AzCli
{
    public static async Task<string> Token(string scope)
        => (await Run("account", "get-access-token", "--scope", scope, "--query", "accessToken", "-o", "tsv")).Trim();
    public static async Task<string> Run(params string[] args)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Azure CLI.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Azure CLI failed ({process.ExitCode}): {stderr.Trim()}");
        return stdout;
    }
}
