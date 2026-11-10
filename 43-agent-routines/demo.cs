#:property TargetFramework=net10.0
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const int WaitSeconds = 360;
var lab = Environment.CurrentDirectory;
var state = Path.Combine(lab, ".azure");
if (!File.Exists(Path.Combine(lab, "infra", "main.bicep")))
    throw new DirectoryNotFoundException("Run this file from 43-agent-routines.");
if (ValidateArguments(args, ["--self-test", "--help", "-h", "--preflight"], ["--dispatch-identity"]) is { } argumentError)
{
    Console.Error.WriteLine($"ERROR: {argumentError}");
    Environment.ExitCode = 2;
    return;
}
if (args.Contains("--self-test"))
{
    OfflineTests();
    return;
}
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Dispatch and verify a native one-time routine.\nUsage: dotnet run --file demo.cs -- [--preflight] [--dispatch-identity creator|agent]");
    return;
}
var preflight = args.Contains("--preflight");
var dispatchIdentity = Option(args, "--dispatch-identity") ?? "creator";
if (dispatchIdentity is not ("creator" or "agent"))
{
    Console.Error.WriteLine("ERROR: --dispatch-identity must be creator or agent; service default is not permitted.");
    Environment.ExitCode = 2;
    return;
}
Directory.CreateDirectory(state);
var report = new JsonObject { ["status"] = "BLOCKED", ["timer_status"] = "NOT_RUN",
    ["blockers"] = new JsonArray(), ["dispatch_identity"] = dispatchIdentity };
try
{
    var config = Settings(state);
    if (preflight)
    {
        var credential = await AzCli.Token("https://ai.azure.com/.default");
        var project = new NativeProject(config["projectEndpoint"]!.ToString(), credential);
        var deployments = await project.Get("deployments");
        if (!(deployments["data"]?.AsArray() ?? deployments["value"]?.AsArray() ?? new JsonArray())
            .Any(d => d?["name"]?.ToString() == config["deployment"]?.ToString()))
            throw new InvalidOperationException("The configured native routine model deployment is missing.");
        await project.Get("routines");
        Console.WriteLine("PASS preflight: native routines endpoint and Entra authorization.");
        return;
    }
    var readiness = await AzCli.Run("resource", "show", "--ids", config["projectResourceId"]!.ToString(),
        "--query", "{provisioningState:properties.provisioningState,identity:identity}", "-o", "json");
    report["arm_project_readback"] = JsonNode.Parse(readiness);
    await Save(report);
    if (report["arm_project_readback"]?["provisioningState"]?.ToString() != "Succeeded")
        throw new InvalidOperationException("BLOCKED: ARM project has not completed provisioning; no routine submitted.");
    var token = await AzCli.Token("https://ai.azure.com/.default");
    var projectClient = new NativeProject(config["projectEndpoint"]!.ToString(), token);
    await Demonstrate(projectClient, config, report, dispatchIdentity);
    report["status"] = "PASS";
}
catch (Exception error)
{
    report["blockers"]!.AsArray().Add(new JsonObject { ["type"] = error.GetType().Name, ["message"] = error.Message });
    report["timer_status"] = preflight ? "NOT_RUN" : "BLOCKED";
}
await Save(report);
Console.WriteLine(new JsonObject { ["status"] = report["status"]!.DeepClone(), ["timer"] = report["timer_status"]!.DeepClone(),
    ["routine_deleted"] = report["routine_deleted"]?.DeepClone(), ["blockers"] = report["blockers"]!.DeepClone() }
    .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
if (report["status"]?.ToString() != "PASS") Environment.ExitCode = 1;

static string? Option(string[] values, string option)
{
    var index = Array.IndexOf(values, option);
    if (index < 0) return null;
    if (index + 1 >= values.Length) throw new ArgumentException($"{option} requires a value.");
    return values[index + 1];
}

static string? ValidateArguments(string[] values, string[] switches, string[] options)
{
    for (var index = 0; index < values.Length; index++)
    {
        if (switches.Contains(values[index], StringComparer.Ordinal)) continue;
        if (!options.Contains(values[index], StringComparer.Ordinal)) return $"Unknown option: {values[index]}";
        if (index + 1 >= values.Length || values[index + 1].StartsWith("--", StringComparison.Ordinal))
            return $"{values[index]} requires a value.";
        index++;
    }
    return null;
}

static JsonObject Settings(string state)
{
    var saved = File.Exists(Path.Combine(state, "deployment.json"))
        ? JsonNode.Parse(File.ReadAllText(Path.Combine(state, "deployment.json")))?["outputs"]?.AsObject() ?? new JsonObject()
        : new JsonObject();
    var mappings = new Dictionary<string, string> {
        ["endpoint"] = "AZURE_OPENAI_ENDPOINT", ["projectEndpoint"] = "FOUNDRY_PROJECT_ENDPOINT",
        ["deployment"] = "AZURE_OPENAI_DEPLOYMENT", ["projectResourceId"] = "FOUNDRY_PROJECT_RESOURCE_ID"
    };
    var result = new JsonObject();
    foreach (var (key, env) in mappings)
        result[key] = Environment.GetEnvironmentVariable(env) is { Length: > 0 } value
            ? value : saved[key]?["value"]?.ToString();
    foreach (var key in mappings.Keys)
        if (string.IsNullOrWhiteSpace(result[key]?.ToString()))
            throw new InvalidOperationException($"BLOCKED: missing {key}; deploy infra/main.bicep and save its outputs in .azure/deployment.json or set the documented environment variables.");
    return result;
}

static JsonObject RoutineBody(string agent, string conversation, DateTimeOffset timer, string identity)
{
    if (identity is not ("creator" or "agent")) throw new ArgumentException("Dispatch identity must explicitly be creator or agent.");
    return new JsonObject {
        ["description"] = "Bounded synthetic one-time workshop timer; deleted on exit.",
        ["enabled"] = false,
        ["authorization"] = new JsonObject { ["identity"] = identity },
        ["triggers"] = new JsonObject { ["once"] = new JsonObject {
            ["type"] = "timer", ["at"] = timer.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") } },
        ["action"] = new JsonObject { ["type"] = "invoke_agent_responses_api", ["agent_name"] = agent,
            ["input"] = "Return the unique synthetic timer marker.", ["conversation"] = conversation }
    };
}

static void VerifyRoutine(JsonObject routine, string name, bool enabled, string identity = "creator")
{
    if (routine["name"]?.ToString() != name || routine["enabled"]?.GetValue<bool>() != enabled)
        throw new InvalidOperationException("Native routine name/enabled read-back mismatch.");
    if (routine["authorization"]?["identity"]?.ToString() != identity)
        throw new InvalidOperationException($"Native routine did not persist the explicitly selected {identity} dispatch identity.");
    var triggers = routine["triggers"]?.AsObject().Select(p => p.Value).ToArray() ?? [];
    if (triggers.Length != 1 || triggers[0]?["type"]?.ToString() != "timer")
        throw new InvalidOperationException("Expected exactly one native one-shot timer trigger.");
}

static void VerifyRun(JsonObject run)
{
    if (run["phase"]?.ToString() != "completed") throw new InvalidOperationException($"Native timer failed: {run}");
    if (run["trigger_type"]?.ToString() != "timer" || run["attempt_source"]?.ToString() != "timer_delivery")
        throw new InvalidOperationException("Run history does not prove an actual service timer delivery.");
    if (string.IsNullOrEmpty(run["response_id"]?.ToString()))
        throw new InvalidOperationException("Completed routine run lacks its linked native response identifier.");
}

static void VerifyResponse(JsonObject run, JsonObject response, string text, string marker)
{
    if (response["id"]?.ToString() != run["response_id"]?.ToString()
        || response["status"]?.ToString() != "completed")
        throw new InvalidOperationException("Retrieved response is not the completed response linked by routine history.");
    var conversation = response["conversation"]?["id"]?.ToString();
    if (!string.IsNullOrEmpty(run["conversation_id"]?.ToString()) && conversation is not null
        && conversation != run["conversation_id"]!.ToString())
        throw new InvalidOperationException("Routine history and response conversation differ.");
    if (text.Trim() != marker) throw new InvalidOperationException("Native routine response does not contain the exact unique synthetic marker.");
}

async Task Demonstrate(NativeProject project, JsonObject config, JsonObject report, string identity)
{
    var suffix = Guid.NewGuid().ToString("N")[..12];
    var agentName = "routine-agent-" + suffix;
    var routineName = "one-time-" + suffix;
    var marker = "NATIVE-TIMER-43-" + suffix;
    var agentAttempted = false;
    var routineAttempted = false;
    string? conversation = null;
    report["routine_name"] = routineName;
    report["marker"] = marker;
    report["timer_status"] = "NOT_RUN";
    report["dispatch_identity"] = identity;
    try
    {
        agentAttempted = true;
        var agent = await project.Send(HttpMethod.Post, $"agents/{Uri.EscapeDataString(agentName)}/versions",
            new JsonObject { ["definition"] = new JsonObject { ["kind"] = "prompt", ["model"] = config["deployment"]!.ToString(),
                ["instructions"] = $"Reply to every input with exactly this literal text and nothing else: {marker}" } });
        report["agent"] = agent.DeepClone();
        await project.Send(HttpMethod.Patch, $"agents/{Uri.EscapeDataString(agentName)}", new JsonObject {
            ["agent_endpoint"] = new JsonObject { ["authorization_schemes"] = new JsonArray(new JsonObject { ["type"] = "Entra" }),
                ["protocol_configuration"] = new JsonObject { ["responses"] = new JsonObject() },
                ["version_selector"] = new JsonObject { ["version_selection_rules"] = new JsonArray(
                    new JsonObject { ["agent_version"] = agent["version"]?.DeepClone(), ["traffic_percentage"] = 100 }) } }
        });
        var principal = agent["instance_identity"]?["principal_id"]?.ToString();
        if (!string.IsNullOrEmpty(principal))
            report["agent_role"] = JsonNode.Parse(await AzCli.Run("role", "assignment", "create",
                "--assignee-object-id", principal, "--assignee-principal-type", "ServicePrincipal",
                "--role", "53ca6127-db72-4b80-b1b0-d745d6d5456d", "--scope", config["projectResourceId"]!.ToString(), "-o", "json"));
        conversation = await CreateConversation(config["endpoint"]!.ToString());
        var body = RoutineBody(agentName, conversation, DateTimeOffset.UtcNow.AddSeconds(90), identity);
        report["submitted_routine_body"] = body.DeepClone();
        await Save(report);
        routineAttempted = true;
        var created = await project.Send(HttpMethod.Put, $"routines/{Uri.EscapeDataString(routineName)}", body);
        created["name"] ??= routineName;
        report["created_routine"] = created.DeepClone();
        VerifyRoutine(created, routineName, false, identity);
        report["lifecycle"] = new JsonArray();
        foreach (var enabled in new[] { false, true, false, true })
        {
            await project.Send(HttpMethod.Post, $"routines/{routineName}/{(enabled ? "enable" : "disable")}", new JsonObject());
            var persisted = await project.Get($"routines/{Uri.EscapeDataString(routineName)}");
            VerifyRoutine(persisted, routineName, enabled, identity);
            report["lifecycle"]!.AsArray().Add(persisted);
            await Save(report);
        }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(WaitSeconds);
        JsonObject? final = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var history = await project.Get($"routines/{Uri.EscapeDataString(routineName)}/runs?limit=5");
            report["run_history"] = history["value"]?.DeepClone() ?? history["runs"]?.DeepClone() ?? new JsonArray();
            await Save(report);
            final = (report["run_history"]!.AsArray()).Select(n => n!.AsObject())
                .FirstOrDefault(n => n["phase"]?.ToString() is "completed" or "failed");
            if (final is not null) break;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, (deadline - DateTimeOffset.UtcNow).TotalSeconds)));
        }
        if (final is null) throw new TimeoutException($"Native one-time timer produced no terminal run within {WaitSeconds}s.");
        VerifyRun(final);
        var response = await RetrieveResponse(config["endpoint"]!.ToString(), agentName, final["response_id"]!.ToString());
        var text = ResponseText(response);
        report["response"] = response.DeepClone();
        VerifyResponse(final, response, text, marker);
        report["native_response_trace"] = response["output"]?.DeepClone();
        report["timer_status"] = "PASS";
    }
    finally
    {
        var cleanupErrors = new JsonArray();
        if (routineAttempted)
        {
            try { await project.Send(HttpMethod.Post, $"routines/{routineName}/disable", new JsonObject()); }
            catch { }
            try
            {
                await project.Send(HttpMethod.Delete, $"routines/{Uri.EscapeDataString(routineName)}");
                report["routine_deleted"] = true;
            }
            catch (Exception error) { cleanupErrors.Add("delete: " + error.Message); }
        }
        if (conversation is not null)
            try
            {
                await DeleteConversation(config["endpoint"]!.ToString(), conversation);
                report["conversation_deleted"] = true;
            }
            catch (Exception error) { cleanupErrors.Add("conversation delete: " + error.Message); }
        if (agentAttempted)
            try
            {
                await project.Send(HttpMethod.Delete, $"agents/{Uri.EscapeDataString(agentName)}");
                report["agent_deleted"] = true;
            }
            catch (Exception error) { cleanupErrors.Add("agent delete: " + error.Message); }
        if (report["agent_role"]?["id"] is JsonNode roleId)
            try
            {
                await AzCli.Run("role", "assignment", "delete", "--ids", roleId.ToString(), "-o", "none");
                report["agent_role_deleted"] = true;
            }
            catch (Exception error) { cleanupErrors.Add("agent role delete: " + error.Message); }
        report["cleanup_errors"] = cleanupErrors;
        await Save(report);
        if (cleanupErrors.Count > 0) throw new InvalidOperationException($"BLOCKED cleanup requires attention: {cleanupErrors}");
    }
}

static async Task<string> CreateConversation(string endpoint)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    var token = await AzCli.Token("https://ai.azure.com/.default");
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/openai/v1/conversations");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    request.Content = JsonContent(new JsonObject());
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Conversation create HTTP {(int)response.StatusCode}: {SafeBody(body)}");
    return JsonNode.Parse(body)!["id"]?.ToString() ?? throw new InvalidDataException("Conversation response has no ID.");
}

static async Task<JsonObject> RetrieveResponse(string endpoint, string agent, string responseId)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.TrimEnd('/') + $"/openai/v1/responses/{Uri.EscapeDataString(responseId)}");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AzCli.Token("https://ai.azure.com/.default"));
    request.Headers.TryAddWithoutValidation("agent-name", agent);
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Response retrieve HTTP {(int)response.StatusCode}: {SafeBody(body)}");
    return JsonNode.Parse(body)!.AsObject();
}

static async Task DeleteConversation(string endpoint, string conversation)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint.TrimEnd('/') + $"/openai/v1/conversations/{Uri.EscapeDataString(conversation)}");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AzCli.Token("https://ai.azure.com/.default"));
    using var response = await http.SendAsync(request);
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Conversation delete HTTP {(int)response.StatusCode}.");
}

static string ResponseText(JsonObject response) => string.Concat((response["output"]?.AsArray() ?? new JsonArray())
    .SelectMany(item => item?["content"]?.AsArray() ?? new JsonArray())
    .Where(content => content?["type"]?.ToString() == "output_text").Select(content => content?["text"]?.ToString() ?? ""));

static string SafeBody(string body)
{
    try { return JsonNode.Parse(body)?["error"]?["code"]?.ToString() ?? "service error"; }
    catch { return "service error"; }
}

static StringContent JsonContent(JsonObject value) => new(value.ToJsonString(), Encoding.UTF8, "application/json");

static async Task Save(JsonObject report)
{
    var state = Path.Combine(Environment.CurrentDirectory, ".azure");
    Directory.CreateDirectory(state);
    var path = Path.Combine(state, "live-report.json");
    var temp = Path.Combine(state, "live-report.pending");
    await File.WriteAllTextAsync(temp, report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    File.Move(temp, path, true);
}

void OfflineTests()
{
    var instant = new DateTimeOffset(2026, 11, 3, 11, 0, 0, TimeSpan.Zero);
    var routine = RoutineBody("agent", "conv", instant, "creator");
    routine["name"] = "once";
    VerifyRoutine(routine, "once", false);
    var agent = RoutineBody("agent", "conv", instant, "agent");
    agent["name"] = "once";
    VerifyRoutine(agent, "once", false, "agent");
    ExpectFailure(() => RoutineBody("agent", "conv", instant, "automatic"), "creator or agent");
    var wrong = routine.DeepClone().AsObject();
    wrong["authorization"]!["identity"] = "agent";
    ExpectFailure(() => VerifyRoutine(wrong, "once", false), "creator dispatch identity");
    ExpectFailure(() => VerifyRoutine(new JsonObject { ["name"] = "once", ["enabled"] = false,
        ["authorization"] = new JsonObject { ["identity"] = "creator" },
        ["triggers"] = new JsonObject { ["once"] = new JsonObject { ["type"] = "schedule" } } }, "once", false), "one-shot timer");
    var run = new JsonObject { ["phase"] = "completed", ["trigger_type"] = "timer", ["attempt_source"] = "timer_delivery",
        ["response_id"] = "resp", ["conversation_id"] = "conv" };
    VerifyRun(run);
    VerifyResponse(run, new JsonObject { ["id"] = "resp", ["status"] = "completed",
        ["conversation"] = new JsonObject { ["id"] = "conv" } }, "marker", "marker");
    ExpectFailure(() => VerifyRun(new JsonObject { ["phase"] = "completed", ["trigger_type"] = "timer",
        ["attempt_source"] = "manual_dispatch", ["response_id"] = "resp" }), "timer delivery");
    ExpectFailure(() => VerifyResponse(run, new JsonObject { ["id"] = "other", ["status"] = "completed" }, "marker", "marker"), "linked");
    ExpectFailure(() => VerifyResponse(run, new JsonObject { ["id"] = "resp", ["status"] = "completed" }, "wrong", "marker"), "unique synthetic marker");
    Console.WriteLine("PASS: native routine identity, timer, run-history, response-link and failure contracts.");
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
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var uri = new Uri(_endpoint, path.TrimStart('/'));
        var builder = new UriBuilder(uri);
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        query["api-version"] = "v1";
        builder.Query = query.ToString();
        using var request = new HttpRequestMessage(method, builder.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload is not null) request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string code;
            try { code = JsonNode.Parse(body)?["error"]?["code"]?.ToString() ?? "service error"; }
            catch { code = "service error"; }
            throw new InvalidOperationException($"Native project API {method} {path}: HTTP {(int)response.StatusCode}, {code}");
        }
        return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
    }
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
