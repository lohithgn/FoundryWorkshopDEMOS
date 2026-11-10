#:property TargetFramework=net10.0
#:package Azure.Core@1.63.0
#:package Azure.Monitor.OpenTelemetry.Exporter@1.10.0
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const string Arm = "https://management.azure.com";
const string PolicyDefinition = "/providers/Microsoft.Authorization/policyDefinitions/71ef260a-8f18-47b7-abcb-62d0673d94dc";
var lab = Environment.CurrentDirectory;
var state = Path.Combine(lab, ".azure");
if (!File.Exists(Path.Combine(lab, "infra", "main.bicep")))
    throw new DirectoryNotFoundException("Run this file from 46-fleet-governance.");
var data = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(lab, "data", "fleet.json")))!.AsObject();
if (ValidateArguments(args, ["--self-test", "--help", "-h"], ["--timeout"]) is { } argumentError)
{
    Console.Error.WriteLine($"ERROR: {argumentError}");
    Environment.ExitCode = 2;
    return;
}
if (args.Contains("--self-test")) { OfflineTests(); return; }
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Native two-project fleet inventory, telemetry correlation and isolated policy.\nUsage: dotnet run --file demo.cs -- [--timeout 600]");
    return;
}
int timeout;
try { timeout = ParseTimeout(args); }
catch (ArgumentException error)
{
    Console.Error.WriteLine($"ERROR: {error.Message}");
    Environment.ExitCode = 2;
    return;
}
Directory.CreateDirectory(state);
JsonObject config;
try { config = Configuration(state); }
catch (Exception error)
{
    Console.Error.WriteLine($"BLOCKED: {error.Message}");
    Environment.ExitCode = 2;
    return;
}
var report = new JsonObject {
    ["lab"] = 46, ["resourceGroup"] = config["resourceGroup"]!.DeepClone(),
    ["controlPlanePortal"] = new JsonObject { ["status"] = "NOT_VERIFIED",
        ["evidence"] = "API inventory is not complete Operate/Assets/Compliance portal verification." },
    ["defenderPurview"] = new JsonObject { ["status"] = "OPTIONAL_NOT_CONFIGURED",
        ["evidence"] = "No tenant security onboarding or fabricated security alerts." }
};
var api = new NativeApi();
var agents = new List<JsonObject>();
JsonObject? external = null;
try
{
    try { agents = await SetupAgents(config); report["nativeAgents"] = new JsonObject { ["status"] = "PASS", ["agents"] = ToJson(agents) }; }
    catch (Exception error) { report["nativeAgents"] = Blocked(error); }
    try { external = await RegisterExternal(config); report["externalRegistration"] = external.DeepClone(); }
    catch (Exception error) { report["externalRegistration"] = Blocked(error); }
    try
    {
        var inventory = await FleetInventory(config);
        if (inventory.Count != 2 || inventory.Any(p => (p["agents"]?.AsArray().Count ?? 0) == 0))
            throw new InvalidOperationException("Expected native inventory with agents in both Foundry projects.");
        report["crossProjectInventory"] = new JsonObject { ["status"] = "PASS", ["projects"] = ToJson(inventory) };
    }
    catch (Exception error) { report["crossProjectInventory"] = Blocked(error); }
    try
    {
        if (agents.Count != 2) throw new InvalidOperationException("Two native prompt agents unavailable for real invocations.");
        var telemetry = await TelemetrySmoke(api, config, agents, external, timeout);
        report["telemetryCorrelation"] = telemetry;
        report["nativeInvocations"] = new JsonObject { ["status"] = telemetry["nativeInvocationStatus"]!.DeepClone(),
            ["invocations"] = telemetry["invocations"]!.DeepClone() };
    }
    catch (Exception error) { report["telemetryCorrelation"] = Blocked(error); report["nativeInvocations"] = Blocked(error); }
    try { report["isolatedAzurePolicy"] = await PolicyCheck(api, config, timeout); }
    catch (Exception error) { report["isolatedAzurePolicy"] = Blocked(error); }
}
catch (Exception error) { report["error"] = SafeError(error); }
var required = new[] { "nativeAgents", "externalRegistration", "crossProjectInventory", "nativeInvocations", "telemetryCorrelation", "isolatedAzurePolicy" };
var fullPass = required.All(key => report[key]?["status"]?.ToString() == "PASS");
report["status"] = fullPass ? "PASS" : "BLOCKED";
await Save("live-report.json", report);
Console.WriteLine(report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
if (!fullPass) Environment.ExitCode = 2;

static int ParseTimeout(string[] args)
{
    var index = Array.IndexOf(args, "--timeout");
    if (index < 0) return 600;
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value) || value <= 0)
        throw new ArgumentException("--timeout must be a positive integer.");
    return value;
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

static JsonObject Configuration(string state)
{
    var recorded = File.Exists(Path.Combine(state, "deployment.json"))
        ? JsonNode.Parse(File.ReadAllText(Path.Combine(state, "deployment.json")))!.AsObject() : new JsonObject();
    var outputs = recorded["outputs"]?.AsObject() ?? new JsonObject();
    var mappings = new Dictionary<string, string> {
        ["endpoint"] = "AZURE_OPENAI_ENDPOINT", ["deployment"] = "AZURE_OPENAI_DEPLOYMENT",
        ["projectEndpoint"] = "FOUNDRY_PROJECT_ENDPOINT", ["projectResourceId"] = "FOUNDRY_PROJECT_RESOURCE_ID",
        ["secondProjectEndpoint"] = "FOUNDRY_SECOND_PROJECT_ENDPOINT", ["secondProjectResourceId"] = "FOUNDRY_SECOND_PROJECT_RESOURCE_ID",
        ["applicationInsightsResourceId"] = "APPLICATION_INSIGHTS_RESOURCE_ID", ["workspaceId"] = "LOG_ANALYTICS_WORKSPACE_ID",
        ["policyAssignmentId"] = "FLEET_POLICY_ASSIGNMENT_ID", ["tenantId"] = "AZURE_TENANT_ID"
    };
    var values = new JsonObject();
    foreach (var (key, env) in mappings)
        values[key] = Environment.GetEnvironmentVariable(env) is { Length: > 0 } value ? value : outputs[key]?["value"]?.ToString();
    var missing = mappings.Keys.Where(key => string.IsNullOrWhiteSpace(values[key]?.ToString())).Select(key => mappings[key]).ToArray();
    if (missing.Length > 0) throw new InvalidOperationException("Missing deployment outputs: " + string.Join(", ", missing));
    var group = recorded["resourceGroup"]?.ToString() ?? "rg-foundry-fleet-governance-demo";
    if (!Regex.IsMatch(group, "^[a-zA-Z0-9_-]{1,90}$") || !group.EndsWith("-fleet-governance-demo", StringComparison.Ordinal))
        throw new ArgumentException("Only a recorded synthetic fleet resource group may be mutated.");
    foreach (var key in new[] { "projectResourceId", "secondProjectResourceId", "applicationInsightsResourceId", "policyAssignmentId" })
        if (!values[key]!.ToString().Contains($"/resourceGroups/{group}/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only designated synthetic resource-group resources may be mutated.");
    foreach (var key in new[] { "endpoint", "projectEndpoint", "secondProjectEndpoint" })
    {
        if (!values[key]!.ToString().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{key} must be HTTPS.");
        values[key] = values[key]!.ToString().TrimEnd('/');
    }
    values["resourceGroup"] = group;
    return values;
}

static JsonObject Blocked(Exception error) => new() { ["status"] = "BLOCKED", ["evidence"] = SafeError(error) };

static string SafeError(Exception error)
{
    if (error is InvalidOperationException or ArgumentException) return error.Message;
    if (error.ToString().Contains("Remote end closed connection without response", StringComparison.Ordinal))
        return $"{error.GetType().Name}; remote end closed connection without HTTP response";
    var codes = Regex.Matches(error.ToString(), "AADSTS\\d+").Select(m => m.Value).Distinct().Order().ToArray();
    if (codes.Length > 0)
        return $"{error.GetType().Name}; Entra codes={string.Join(",", codes)}{(codes.Contains("AADSTS500014") ? "; tenant resource service principal is disabled" : "")}; token not issued";
    return $"{error.GetType().Name}; HTTP status unavailable; request ID unavailable; body omitted";
}

async Task<List<JsonObject>> FleetInventory(JsonObject config)
{
    var results = new List<JsonObject>();
    foreach (var (endpoint, resourceId) in new[] {
        (config["projectEndpoint"]!.ToString(), config["projectResourceId"]!.ToString()),
        (config["secondProjectEndpoint"]!.ToString(), config["secondProjectResourceId"]!.ToString())
    })
    {
        var project = new NativeProject(endpoint);
        var agentsPayload = await project.Get("agents");
        var connectionsPayload = await project.Get("connections");
        var agents = Items(agentsPayload).Select(agent => new JsonObject {
            ["name"] = agent["name"]?.DeepClone(), ["id"] = agent["id"]?.DeepClone(),
            ["kind"] = agent["versions"]?["latest"]?["definition"]?["kind"]?.DeepClone()
                ?? agent["latest_version"]?["definition"]?["kind"]?.DeepClone(),
            ["telemetryId"] = agent["versions"]?["latest"]?["definition"]?["otel_agent_id"]?.DeepClone()
                ?? agent["latest_version"]?["definition"]?["otel_agent_id"]?.DeepClone()
                ?? agent["id"]?.DeepClone()
        }).ToArray();
        var connections = Items(connectionsPayload).Select(connection => new JsonObject {
            ["name"] = connection["name"]?.DeepClone(), ["id"] = connection["id"]?.DeepClone(),
            ["type"] = connection["type"]?.DeepClone()
        }).ToArray();
        results.Add(new JsonObject { ["projectResourceId"] = resourceId, ["projectEndpoint"] = endpoint,
            ["agents"] = ToJson(agents), ["connections"] = ToJson(connections) });
    }
    return results;
}

async Task<List<JsonObject>> SetupAgents(JsonObject config)
{
    var saved = new List<JsonObject>();
    var names = data["agents"]!.AsArray();
    var endpoints = new[] {
        (config["projectEndpoint"]!.ToString(), config["projectResourceId"]!.ToString()),
        (config["secondProjectEndpoint"]!.ToString(), config["secondProjectResourceId"]!.ToString())
    };
    for (var index = 0; index < endpoints.Length; index++)
    {
        var (endpoint, resourceId) = endpoints[index];
        var project = new NativeProject(endpoint);
        var name = names[index]!.ToString();
        var list = Items(await project.Get("agents"));
        var existing = list.FirstOrDefault(agent => agent["name"]?.ToString() == name);
        var version = existing?["versions"]?["latest"]?["version"]?.ToString()
            ?? existing?["latest_version"]?["version"]?.ToString();
        JsonObject agentVersion;
        if (existing is null)
            agentVersion = await project.Send(HttpMethod.Post, $"agents/{Uri.EscapeDataString(name)}/versions",
                new JsonObject { ["definition"] = new JsonObject { ["kind"] = "prompt",
                    ["model"] = config["deployment"]!.DeepClone(),
                    ["instructions"] = "SYNTHETIC WORKSHOP DATA. The fictional Helios fleet maintenance interval is 36 months. Answer briefly." },
                    ["description"] = "Synthetic fleet governance workshop prompt agent.",
                    ["metadata"] = new JsonObject { ["workshop"] = "46", ["data"] = "synthetic" } });
        else
            agentVersion = await project.Get($"agents/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(version!)}");
        if (agentVersion["definition"]?["model"]?.ToString() != config["deployment"]?.ToString())
            throw new InvalidOperationException("Native agent model readback mismatch.");
        saved.Add(new JsonObject { ["name"] = name, ["version"] = agentVersion["version"]?.DeepClone(),
            ["id"] = existing?["id"]?.DeepClone() ?? agentVersion["id"]?.DeepClone(),
            ["versionId"] = agentVersion["id"]?.DeepClone(), ["projectEndpoint"] = endpoint, ["projectResourceId"] = resourceId });
    }
    await Save("agents.json", new JsonObject { ["agents"] = ToJson(saved) });
    return saved;
}

async Task<JsonObject> RegisterExternal(JsonObject config)
{
    var project = new NativeProject(config["projectEndpoint"]!.ToString());
    var name = data["externalAgent"]!.ToString();
    var existing = Items(await project.Get("agents")).FirstOrDefault(agent => agent["name"]?.ToString() == name);
    var registered = existing?["versions"]?["latest"] as JsonObject
        ?? existing?["latest_version"] as JsonObject;
    if (registered is null)
        registered = await project.Send(HttpMethod.Post, $"agents/{Uri.EscapeDataString(name)}/versions",
            new JsonObject { ["definition"] = new JsonObject { ["kind"] = "external",
                ["otel_agent_id"] = data["externalTelemetryId"]!.DeepClone() },
                ["description"] = "Synthetic external .NET runtime; registration is not runtime hosting." });
    var persisted = await project.Get($"agents/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(registered["version"]!.ToString())}");
    if (persisted["definition"]?["otel_agent_id"]?.ToString() != data["externalTelemetryId"]?.ToString())
        throw new InvalidDataException("Native external telemetry ID readback mismatch.");
    var result = new JsonObject { ["status"] = "PASS", ["name"] = name,
        ["version"] = persisted["version"]?.DeepClone(), ["otelAgentId"] = persisted["definition"]?["otel_agent_id"]?.DeepClone(),
        ["projectResourceId"] = config["projectResourceId"]!.DeepClone() };
    await Save("external-agent.json", result);
    return result;
}

static string ExternalRuntime(string question)
    => question.Contains("Helios", StringComparison.Ordinal) ? "36 months"
        : "This synthetic runtime only answers fictional Helios fleet questions.";

static List<JsonObject> Items(JsonObject payload) =>
    (payload["data"]?.AsArray() ?? payload["value"]?.AsArray() ?? payload["items"]?.AsArray() ?? new JsonArray())
    .Where(node => node is JsonObject).Select(node => node!.AsObject()).ToList();

static JsonArray ToJson(IEnumerable<JsonObject> objects) => new(objects.Select(obj => (JsonNode)obj.DeepClone()).ToArray());

static List<JsonObject> Correlate(JsonArray inventory, JsonArray rows, string runId)
{
    var expected = new Dictionary<(string project, string agent), string?>();
    foreach (var project in inventory)
        foreach (var agent in project?["agents"]?.AsArray() ?? new JsonArray())
            expected[(project!["projectResourceId"]!.ToString(), agent!["name"]!.ToString())] = agent["telemetryId"]?.ToString();
    var counts = new Dictionary<(string project, string agent), int>();
    foreach (var row in rows)
    {
        if (row?["RunId"]?.ToString() != runId) continue;
        var key = (row["ProjectId"]!.ToString(), row["AgentName"]!.ToString());
        if (expected.TryGetValue(key, out var telemetryId) && !string.IsNullOrEmpty(telemetryId)
            && row["OtelAgentId"]?.ToString() != telemetryId) continue;
        counts[key] = counts.GetValueOrDefault(key) + (int)(Number(row["Traces"]) ?? 0);
    }
    return inventory.SelectMany(project => (project?["agents"]?.AsArray() ?? new JsonArray()).Select(agent => {
        var key = (project!["projectResourceId"]!.ToString(), agent!["name"]!.ToString());
        return new JsonObject { ["projectResourceId"] = key.Item1, ["agent"] = key.Item2,
            ["kind"] = agent["kind"]?.DeepClone(), ["telemetryId"] = agent["telemetryId"]?.DeepClone(),
            ["traceCount"] = counts.GetValueOrDefault(key) };
    })).ToList();
}

static string TelemetryQuery(string runId)
{
    var id = Guid.Parse(runId).ToString();
    return $$"""
union isfuzzy=true AppDependencies, AppRequests, AppTraces
| where TimeGenerated > ago(1h)
| extend RunId=tostring(Properties["workshop.run_id"]),
         ProjectId=tostring(Properties["workshop.project_id"]),
         AgentName=tostring(Properties["gen_ai.agent.name"]),
         OtelAgentId=tostring(Properties["gen_ai.agent.id"])
| where RunId == "{{id}}"
| summarize Traces=count() by RunId, ProjectId, AgentName, OtelAgentId
""";
}

async Task<JsonObject> TelemetrySmoke(NativeApi api, JsonObject config, List<JsonObject> agents, JsonObject? external, int timeout)
{
    var insights = await api.Json(HttpMethod.Get, Arm + config["applicationInsightsResourceId"]!.ToString(),
        Arm + "/.default", query: new Dictionary<string, string> { ["api-version"] = "2020-02-02" });
    if (insights["properties"]?["DisableLocalAuth"]?.GetValue<bool>() != true)
        throw new InvalidOperationException("Application Insights must enforce Entra ingestion.");
    var connectionString = insights["properties"]?["ConnectionString"]?.ToString()
        ?? throw new InvalidDataException("Application Insights connection string is missing.");
    var credential = new AzureCliCredential(new AzureCliCredentialOptions { TenantId = config["tenantId"]!.ToString() });
    var runId = Guid.NewGuid().ToString();
    var invocations = new JsonArray();
    using var provider = Sdk.CreateTracerProviderBuilder()
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("synthetic-fleet-46"))
        .AddSource("workshop.fleet")
        .AddAzureMonitorTraceExporter(options => {
            options.ConnectionString = connectionString;
            options.DisableOfflineStorage = true;
        }, credential)
        .Build();
    var source = new ActivitySource("workshop.fleet");
    try
    {
        if (external is not null)
        {
            using var activity = source.StartActivity("invoke_agent " + external["name"], ActivityKind.Server);
            activity?.SetTag("gen_ai.agent.id", external["otelAgentId"]?.ToString());
            activity?.SetTag("gen_ai.agent.name", external["name"]?.ToString());
            activity?.SetTag("workshop.project_id", external["projectResourceId"]?.ToString());
            activity?.SetTag("workshop.run_id", runId);
            activity?.SetTag("gen_ai.operation.name", "invoke_agent");
            if (ExternalRuntime(data["question"]!.ToString()) != data["expectedAnswer"]!.ToString())
                throw new InvalidOperationException("External synthetic runtime returned an unexpected answer.");
            invocations.Add(new JsonObject { ["agent"] = external["name"]!.DeepClone(), ["status"] = "completed",
                ["runtime"] = "actual local .NET function, not hosted by registration" });
        }
        foreach (var agent in agents)
        {
            try
            {
                using var activity = source.StartActivity("invoke_agent " + agent["name"], ActivityKind.Client);
                activity?.SetTag("gen_ai.agent.id", agent["id"]?.ToString());
                activity?.SetTag("gen_ai.agent.name", agent["name"]?.ToString());
                activity?.SetTag("workshop.project_id", agent["projectResourceId"]?.ToString());
                activity?.SetTag("workshop.run_id", runId);
                activity?.SetTag("gen_ai.operation.name", "invoke_agent");
                var response = await OpenAI(agent["projectEndpoint"]!.ToString(), "responses",
                    new JsonObject { ["input"] = data["question"]!.DeepClone(), ["max_output_tokens"] = 128 }, agent["name"]!.ToString());
                var text = ResponseText(response);
                if (response["status"]?.ToString() != "completed" || !text.Contains(data["expectedAnswer"]!.ToString(), StringComparison.Ordinal))
                    throw new InvalidOperationException($"Real invocation didn't return expected answer: {agent["name"]}");
                invocations.Add(new JsonObject { ["agent"] = agent["name"]!.DeepClone(),
                    ["responseId"] = response["id"]?.DeepClone(), ["status"] = response["status"]?.DeepClone() });
            }
            catch (Exception error)
            {
                invocations.Add(new JsonObject { ["agent"] = agent["name"]!.DeepClone(), ["status"] = "failed", ["evidence"] = SafeError(error) });
            }
        }
        provider.ForceFlush();
    }
    finally
    {
        await Save("invocations.json", new JsonObject { ["runId"] = runId, ["invocations"] = invocations.DeepClone() });
    }
    var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
    var rows = new JsonArray();
    while (true)
    {
        try
        {
            var result = await api.Json(HttpMethod.Post, $"https://api.loganalytics.azure.com/v1/workspaces/{config["workspaceId"]}/query",
                "https://api.loganalytics.io/.default", new JsonObject { ["query"] = TelemetryQuery(runId) });
            rows = ConvertTables(result["tables"]?.AsArray() ?? new JsonArray());
            if (rows.Select(r => (r?["ProjectId"]?.ToString(), r?["AgentName"]?.ToString())).Distinct().Count() >= invocations.Count)
                break;
        }
        catch (InvalidOperationException error) when (error.Message.Contains("BadArgumentError", StringComparison.Ordinal)
            && DateTimeOffset.UtcNow < deadline) { await Task.Delay(TimeSpan.FromSeconds(15)); continue; }
        if (DateTimeOffset.UtcNow >= deadline)
            throw new TimeoutException($"Azure Monitor correlation timeout: observed {rows.Count} rows for {invocations.Count} real invocations; runId={runId}");
        await Task.Delay(TimeSpan.FromSeconds(15));
    }
    var inventory = await FleetInventory(config);
    var correlated = Correlate(ToJson(inventory), rows, runId);
    var expectedPairs = agents.Select(agent => (agent["projectResourceId"]!.ToString(), agent["name"]!.ToString())).ToHashSet();
    if (external is not null) expectedPairs.Add((external["projectResourceId"]!.ToString(), external["name"]!.ToString()));
    var observed = correlated.Where(row => (int?)row["traceCount"] > 0)
        .Select(row => (row["projectResourceId"]!.ToString(), row["agent"]!.ToString())).ToHashSet();
    if (!expectedPairs.IsSubsetOf(observed))
        throw new InvalidOperationException("Native inventory couldn't correlate every invocation to ingested Azure telemetry.");
    var nativeSuccess = invocations.Where(item => agents.Any(agent => agent["name"]?.ToString() == item?["agent"]?.ToString()))
        .All(item => item?["status"]?.ToString() == "completed");
    return new JsonObject { ["status"] = "PASS", ["runId"] = runId, ["invocations"] = invocations.DeepClone(),
        ["nativeInvocationStatus"] = nativeSuccess ? "PASS" : "BLOCKED", ["correlatedFleet"] = ToJson(correlated),
        ["rows"] = rows.DeepClone(), ["boundary"] = "Actual client/external runtime spans; not invented Foundry server telemetry." };
}

static JsonArray ConvertTables(JsonArray tables)
{
    var rows = new JsonArray();
    foreach (var table in tables)
    {
        var columns = (table?["columns"]?.AsArray() ?? new JsonArray()).Select(col => col?["name"]?.ToString()).ToArray();
        foreach (var row in table?["rows"]?.AsArray() ?? new JsonArray())
        {
            var result = new JsonObject();
            for (var index = 0; index < columns.Length && index < row!.AsArray().Count; index++)
                result[columns[index]!] = row[index]?.DeepClone();
            rows.Add(result);
        }
    }
    return rows;
}

async Task<JsonObject> PolicyCheck(NativeApi api, JsonObject config, int timeout)
{
    var assignment = await api.Json(HttpMethod.Get, Arm + config["policyAssignmentId"]!.ToString(), Arm + "/.default",
        query: new Dictionary<string, string> { ["api-version"] = "2024-04-01" });
    var properties = assignment["properties"]!;
    if (properties["policyDefinitionId"]?.ToString() != PolicyDefinition
        || properties["parameters"]?["effect"]?["value"]?.ToString() != "Audit")
        throw new InvalidDataException("Expected isolated built-in Audit policy; no broad enforcement permitted.");
    var assignmentId = config["policyAssignmentId"]!.ToString();
    var scope = assignmentId.Split("/providers/Microsoft.Authorization/")[0];
    var expectedScope = config["projectResourceId"]!.ToString().Split("/providers/")[0];
    if (!string.Equals(scope, expectedScope, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Policy assignment is not scoped to the isolated resource group.");
    await api.Json(HttpMethod.Post, Arm + scope + "/providers/Microsoft.PolicyInsights/policyStates/latest/triggerEvaluation",
        Arm + "/.default", query: new Dictionary<string, string> { ["api-version"] = "2019-10-01" });
    var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
    while (true)
    {
        var payload = await api.Json(HttpMethod.Post,
            Arm + scope + "/providers/Microsoft.PolicyInsights/policyStates/latest/queryResults", Arm + "/.default",
            query: new Dictionary<string, string> { ["api-version"] = "2019-10-01",
                ["$filter"] = $"PolicyAssignmentId eq '{assignmentId}'" });
        var accountId = config["projectResourceId"]!.ToString().Split("/projects/")[0];
        var selected = Items(payload).Where(item => string.Equals(item["resourceId"]?.ToString(), accountId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length > 0)
        {
            if (selected.Any(item => item["complianceState"]?.ToString() != "Compliant"))
                throw new InvalidOperationException("Native Azure Policy evaluated isolated Foundry account as noncompliant.");
            return new JsonObject { ["status"] = "PASS", ["assignmentId"] = assignment["id"]?.DeepClone(),
                ["scope"] = scope, ["effect"] = "Audit", ["complianceState"] = "Compliant" };
        }
        if (DateTimeOffset.UtcNow >= deadline)
            throw new TimeoutException("Audit assignment exists but Policy Insights has no evaluated account state yet.");
        await Task.Delay(TimeSpan.FromSeconds(15));
    }
}

static double? Number(JsonNode? n) => double.TryParse(n?.ToString(), out var number) ? number : null;

async Task<JsonObject> OpenAI(string endpoint, string route, JsonObject payload, string agent)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/openai/v1/" + route);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AzCli.Token("https://ai.azure.com/.default"));
    request.Headers.TryAddWithoutValidation("agent-name", agent);
    request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    using var response = await http.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Responses API HTTP {(int)response.StatusCode}; {NativeErrors.ErrorCode(text)}");
    return JsonNode.Parse(text)!.AsObject();
}

static string ResponseText(JsonObject response) => string.Concat((response["output"]?.AsArray() ?? new JsonArray())
    .SelectMany(item => item?["content"]?.AsArray() ?? new JsonArray())
    .Where(item => item?["type"]?.ToString() == "output_text").Select(item => item?["text"]?.ToString() ?? ""));

async Task Save(string filename, JsonObject value)
{
    Directory.CreateDirectory(state);
    await File.WriteAllTextAsync(Path.Combine(state, filename),
        value.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
}

void OfflineTests()
{
    if (data["projects"]!.AsArray().Count != 2 || data["agents"]!.AsArray().Count != 2
        || data["label"]?.ToString() != "SYNTHETIC WORKSHOP DATA")
        throw new InvalidDataException("Fleet fixture must retain two synthetic projects and agents.");
    if (ExternalRuntime(data["question"]!.ToString()) != data["expectedAnswer"]!.ToString()
        || ExternalRuntime("Other business data?") == data["expectedAnswer"]!.ToString())
        throw new InvalidDataException("External runtime must answer only the fictional Helios question.");
    var run = Guid.NewGuid().ToString();
    var inventory = new JsonArray(
        new JsonObject { ["projectResourceId"] = "p1", ["agents"] = new JsonArray(new JsonObject { ["name"] = "a", ["kind"] = "prompt" }) },
        new JsonObject { ["projectResourceId"] = "p2", ["agents"] = new JsonArray(new JsonObject { ["name"] = "b", ["kind"] = "prompt" }) });
    var rows = new JsonArray(
        new JsonObject { ["ProjectId"] = "p1", ["AgentName"] = "a", ["RunId"] = run, ["Traces"] = 1 },
        new JsonObject { ["ProjectId"] = "p2", ["AgentName"] = "b", ["RunId"] = run, ["Traces"] = 2 },
        new JsonObject { ["ProjectId"] = "p1", ["AgentName"] = "a", ["RunId"] = "other", ["Traces"] = 99 });
    var counts = Correlate(inventory, rows, run).Select(row => (int)row["traceCount"]!).ToArray();
    if (!counts.SequenceEqual([1, 2])) throw new InvalidDataException("Fleet telemetry correlation must isolate project and run.");
    var guarded = new JsonArray(new JsonObject { ["projectResourceId"] = "p1", ["agents"] = new JsonArray(
        new JsonObject { ["name"] = "a", ["kind"] = "external", ["telemetryId"] = "registered.id" }) });
    var wrongId = Correlate(guarded, new JsonArray(new JsonObject { ["ProjectId"] = "p1", ["AgentName"] = "a",
        ["RunId"] = run, ["OtelAgentId"] = "unregistered.id", ["Traces"] = 99 }), run);
    if ((int)wrongId[0]["traceCount"]! != 0) throw new InvalidDataException("Unregistered telemetry ID must not correlate.");
    var query = TelemetryQuery(run);
    if (!query.Contains("gen_ai.agent.id", StringComparison.Ordinal) || !query.Contains("workshop.project_id", StringComparison.Ordinal)
        || !query.Contains(run, StringComparison.Ordinal)) throw new InvalidDataException("Telemetry query contract missing correlation keys.");
    ExpectFailure(() => TelemetryQuery("\"; union business_content"), "Guid");
    ExpectFailure(() => Configuration(Path.Combine(lab, ".missing-state")), "Missing deployment outputs");
    var bicep = File.ReadAllText(Path.Combine(lab, "infra", "main.bicep"));
    if (!bicep.Contains(PolicyDefinition, StringComparison.Ordinal) || !bicep.Contains("effect: { value: 'Audit' }", StringComparison.Ordinal)
        || bicep.Contains("effect: { value: 'Deny' }", StringComparison.Ordinal))
        throw new InvalidDataException("Policy must remain isolated built-in Audit, not Deny.");
    if (!bicep.Contains("DisableLocalAuth: true", StringComparison.Ordinal)
        || !File.ReadAllText(Path.Combine(lab, "infra", "main.bicep")).Contains("dependsOn: [operations]", StringComparison.Ordinal))
        throw new InvalidDataException("Entra-only telemetry and serialized child deployment invariant changed.");
    Console.WriteLine("PASS: synthetic external runtime, two-project telemetry correlation, UUID guard and isolated Audit policy.");
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
    public async Task<JsonObject> Json(HttpMethod method, string url, string scope, JsonObject? body = null,
        IDictionary<string, string>? query = null)
    {
        if (query is not null)
        {
            var builder = new UriBuilder(url);
            var values = System.Web.HttpUtility.ParseQueryString(builder.Query);
            foreach (var pair in query) values[pair.Key] = pair.Value;
            builder.Query = values.ToString();
            url = builder.Uri.ToString();
        }
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AzCli.Token(scope));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var requestId = response.Headers.TryGetValues("x-ms-request-id", out var ids) ? ids.FirstOrDefault() : "unavailable";
            throw new InvalidOperationException($"{method} {url}: HTTP {(int)response.StatusCode}, code={NativeErrors.ErrorCode(text)}, requestId={requestId}");
        }
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
    }
}

sealed class NativeProject(string endpoint)
{
    private readonly Uri _endpoint = new(endpoint.TrimEnd('/') + "/");
    public Task<JsonObject> Get(string path) => Send(HttpMethod.Get, path);
    public async Task<JsonObject> Send(HttpMethod method, string path, JsonObject? payload = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var builder = new UriBuilder(new Uri(_endpoint, path.TrimStart('/')));
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        query["api-version"] = "v1";
        builder.Query = query.ToString();
        using var request = new HttpRequestMessage(method, builder.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AzCli.Token("https://ai.azure.com/.default"));
        if (payload is not null) request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Native project API {method} {path}: HTTP {(int)response.StatusCode}, code={NativeErrors.ErrorCode(text)}");
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
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
