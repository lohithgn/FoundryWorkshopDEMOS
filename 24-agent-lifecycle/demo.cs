#!/usr/bin/env dotnet
#:package Azure.Identity@1.21.0

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;

public sealed class RuntimeError(string message) : Exception(message);

public static class Lifecycle
{
    public const string ArmVersion = "2025-10-01-preview";
    public const string DataVersion = "2025-11-15-preview";
    public static readonly string[] Questions = ["What is the workshop price?", "How much does the workshop cost?"];
    public static readonly JsonObject SecurityTags = new()
    {
        ["SecurityControl"] = "Ignore",
        ["SecurityExemption"] = "StorageAccountKeyAccess"
    };

    public static JsonObject ApplicationBody(string name) => new()
    {
        ["properties"] = new JsonObject
        {
            ["tags"] = SecurityTags.DeepClone(),
            ["displayName"] = "Workshop lifecycle application",
            ["agents"] = new JsonArray(new JsonObject { ["agentName"] = name })
        }
    };

    public static JsonObject DeploymentBody(string name, string version) => new()
    {
        ["properties"] = new JsonObject
        {
            ["tags"] = SecurityTags.DeepClone(),
            ["displayName"] = "Workshop managed deployment",
            ["deploymentType"] = "Managed",
            ["protocols"] = new JsonArray(new JsonObject { ["protocol"] = "Responses", ["version"] = "1.0" }),
            ["agents"] = new JsonArray(new JsonObject { ["agentName"] = name, ["agentVersion"] = version })
        }
    };

    public static string ResponseText(JsonNode? response)
    {
        var pieces = new List<string>();
        if (response?["output"] is JsonArray output)
            foreach (var item in output)
                if (item?["content"] is JsonArray content)
                    foreach (var block in content)
                        if ((string?)block?["type"] == "output_text")
                            pieces.Add((string?)block?["text"] ?? "");
        return string.Concat(pieces).Trim();
    }

    public static bool IsExpectedDeployment(JsonNode? deployment, string name, string version)
    {
        var props = deployment?["properties"];
        var agents = props?["agents"] as JsonArray;
        return (string?)props?["state"] == "Running" && agents?.Count == 1
            && (string?)agents[0]?["agentName"] == name
            && string.Equals(agents[0]?["agentVersion"]?.ToString(), version, StringComparison.Ordinal);
    }

    public static JsonObject ApplicationDeploymentBody(string name, string? version = null) =>
        version is null ? ApplicationBody(name) : DeploymentBody(name, version);

    public interface IService
    {
        Task<string> CreateVersionAsync(string name, string deployment, string instructions);
        Task<JsonNode> PublishAsync(string id, string name);
        Task<JsonNode> SelectVersionAsync(string id, string name, string version, int timeoutSeconds = 300);
        Task InvokeAsync(string endpoint, string version, string expected, JsonObject report);
        Task<JsonNode> ActivityLogAsync(string subscription, string deploymentId, string startedAt);
    }

    public sealed class AzureService(HttpClient http, TokenCredential credential, string projectEndpoint) : IService
    {
        private readonly HttpClient _http = http;
        private readonly TokenCredential _credential = credential;
        private readonly string _projectEndpoint = projectEndpoint.TrimEnd('/');

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string scope, JsonNode? body = null, int timeoutSeconds = 60)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                _credential.GetToken(new TokenRequestContext([scope]), CancellationToken.None).Token);
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is < 200 or >= 300)
            {
                var text = await response.Content.ReadAsStringAsync();
                response.Dispose();
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {text}", null, response.StatusCode);
            }
            return response;
        }

        private async Task<JsonNode> JsonRequestAsync(HttpMethod method, string url, string scope, JsonNode? body = null, int timeoutSeconds = 60)
        {
            using var response = await SendAsync(method, url, scope, body, timeoutSeconds);
            var text = await response.Content.ReadAsStringAsync();
            return string.IsNullOrEmpty(text) ? new JsonObject() : JsonNode.Parse(text)!;
        }

        private async Task<JsonNode> ArmAsync(HttpMethod method, string resourceId, JsonNode? body = null, string? query = null)
        {
            try
            {
                return await JsonRequestAsync(method, "https://management.azure.com" + resourceId +
                    (query ?? $"?api-version={ArmVersion}"), "https://management.azure.com/.default", body);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is not null)
            {
                var prefix = $"HTTP {(int)ex.StatusCode}: ";
                var details = ex.Message.StartsWith(prefix, StringComparison.Ordinal) ? ex.Message[prefix.Length..] : ex.Message;
                throw new RuntimeError($"ARM {method} failed ({(int)ex.StatusCode}): {details}");
            }
        }

        public async Task<string> CreateVersionAsync(string name, string deployment, string instructions)
        {
            var body = new JsonObject
            {
                ["definition"] = new JsonObject
                {
                    ["kind"] = "prompt", ["model"] = deployment, ["instructions"] = instructions
                }
            };
            var result = await JsonRequestAsync(HttpMethod.Post,
                $"{_projectEndpoint}/agents/{Uri.EscapeDataString(name)}/versions?api-version={DataVersion}",
                "https://ai.azure.com/.default", body);
            var version = (string?)result["version"];
            return !string.IsNullOrEmpty(version) ? version :
                throw new RuntimeError("Agent version response did not contain a version.");
        }

        public async Task<JsonNode> PublishAsync(string id, string name)
        {
            await ArmAsync(HttpMethod.Put, id, ApplicationBody(name));
            var deadline = Stopwatch.GetTimestamp() + 300L * Stopwatch.Frequency;
            while (true)
            {
                var application = await ArmAsync(HttpMethod.Get, id);
                var state = (string?)application["properties"]?["provisioningState"];
                if (state is "Failed" or "Canceled")
                    throw new RuntimeError($"Application provisioning failed: {application.ToJsonString()}");
                if (state is null or "Succeeded") return application;
                if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("Application provisioning timed out");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        public async Task<JsonNode> SelectVersionAsync(string id, string name, string version, int timeoutSeconds = 300)
        {
            await ArmAsync(HttpMethod.Put, id, DeploymentBody(name, version));
            var deadline = Stopwatch.GetTimestamp() + (long)timeoutSeconds * Stopwatch.Frequency;
            while (true)
            {
                var deployment = await ArmAsync(HttpMethod.Get, id);
                var state = (string?)deployment["properties"]?["state"];
                if (state is "Failed" or "Stopped" or "Deleted")
                    throw new RuntimeError($"Deployment not running: {deployment.ToJsonString()}");
                if (IsExpectedDeployment(deployment, name, version)) return deployment;
                if (Stopwatch.GetTimestamp() >= deadline)
                    throw new TimeoutException($"Version {version} did not become Running: {deployment.ToJsonString()}");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        public async Task InvokeAsync(string endpoint, string version, string expected, JsonObject report)
        {
            foreach (var question in Questions)
            {
                var requestId = Guid.NewGuid().ToString("N");
                var start = Stopwatch.GetTimestamp();
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "?api-version=" + DataVersion);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                    _credential.GetToken(new TokenRequestContext(["https://ai.azure.com/.default"]), CancellationToken.None).Token);
                request.Headers.Add("x-ms-client-request-id", requestId);
                request.Content = new StringContent(new JsonObject { ["input"] = question }.ToJsonString(),
                    Encoding.UTF8, "application/json");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var errorText = await response.Content.ReadAsStringAsync();
                    response.Dispose();
                    throw new RuntimeError($"Application invocation failed ({(int)response.StatusCode}): {errorText}");
                }
                using (response)
                {
                    var data = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                    var text = ResponseText(data);
                    var record = new JsonObject
                    {
                        ["question"] = question, ["configured_version"] = version,
                        ["response_id"] = data["id"]?.DeepClone(), ["request_id"] = requestId,
                        ["server_request_id"] = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() :
                            response.Headers.TryGetValues("apim-request-id", out ids) ? ids.FirstOrDefault() : null,
                        ["seconds"] = Math.Round((double)(Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency, 3),
                        ["output"] = text, ["status"] = data["status"]?.DeepClone(),
                        ["usage"] = data["usage"]?.DeepClone(), ["correct"] = text == expected
                    };
                    (report["requests"] as JsonArray)!.Add((JsonNode?)record);
                    Console.WriteLine(record.ToJsonString());
                    if ((string?)data["status"] != "completed" || string.IsNullOrEmpty(text))
                        throw new RuntimeError($"Incomplete application response: {data["error"]}");
                    if (text != expected)
                        throw new RuntimeError($"Endpoint behavior mismatch for selected version {version}: {JsonValue.Create(text)!.ToJsonString()}");
                }
            }
        }

        public Task<JsonNode> ActivityLogAsync(string subscription, string deploymentId, string startedAt) =>
            ArmAsync(HttpMethod.Get, $"/subscriptions/{subscription}/providers/microsoft.insights/eventtypes/management/values",
                query: "?api-version=2015-04-01&$filter=" + Uri.EscapeDataString(
                    $"eventTimestamp ge '{startedAt}' and resourceUri eq '{deploymentId}'"));
    }

    public static async Task RunLifecycleAsync(IService service, string projectResourceId, string projectEndpoint,
        string deployment, JsonObject report)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "workshop-" + suffix;
        var appName = "workshop-app-" + suffix;
        var applicationId = $"{projectResourceId}/applications/{appName}";
        var deploymentId = $"{applicationId}/agentDeployments/active";
        var endpoint = $"{projectEndpoint.TrimEnd('/')}/applications/{appName}/protocols/openai/responses";
        report["application_id"] = applicationId;
        report["deployment_id"] = deploymentId;
        report["endpoint"] = endpoint;
        report["requests"] = new JsonArray();
        var good = await service.CreateVersionAsync(name, deployment, "For every input reply with exactly: USD 25");
        report["agent_name"] = name;
        report["good_version"] = good;
        report["application"] = await service.PublishAsync(applicationId, name);
        report["initial_deployment"] = await service.SelectVersionAsync(deploymentId, name, good);
        try
        {
            await service.InvokeAsync(endpoint, good, "USD 25", report);
            var changed = await service.CreateVersionAsync(name, deployment, "For every input reply with exactly: USD 40");
            report["changed_version"] = changed;
            report["changed_deployment"] = await service.SelectVersionAsync(deploymentId, name, changed);
            await service.InvokeAsync(endpoint, changed, "USD 40", report);
        }
        finally
        {
            report["rollback_deployment"] = await service.SelectVersionAsync(deploymentId, name, good);
            await service.InvokeAsync(endpoint, good, "USD 25", report);
            report["rollback_verified"] = true;
        }
        var subscription = projectResourceId.Split('/')[2];
        report["activity_log"] = await service.ActivityLogAsync(subscription, deploymentId, (string)report["started_at"]!);
        var eventCount = (report["activity_log"]?["value"] as JsonArray)?.Count ?? 0;
        Console.WriteLine($"Activity Log records (eventually consistent): {eventCount}");
        Console.WriteLine("Rollback verified against the same questions. Published application remains available.");
    }

    public static bool ValidateProjectResourceId(string? value, out string account, out string project)
    {
        var match = Regex.Match(value ?? "",
            @"\A/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft\.CognitiveServices/accounts/([^/]+)/projects/([^/]+)\z");
        account = match.Success ? match.Groups[1].Value : "";
        project = match.Success ? match.Groups[2].Value : "";
        return match.Success;
    }
}

public static class LifecycleApp
{
    public static Task WriteReportAsync(string path, JsonObject report) =>
        File.WriteAllTextAsync(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

    public static async Task<int> MainAsync(string[] args)
    {
        args = ExpandEquals(args);
        string? endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        string? resourceId = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_RESOURCE_ID");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                Console.WriteLine("Publish, version, monitor and roll back an Azure Agent Application.\n--project-endpoint URL --project-resource-id ARM_ID --deployment NAME");
                return 0;
            }
            if (args[i] is "--project-endpoint" or "--project-resource-id" or "--deployment")
            {
                if (++i >= args.Length) return UsageError($"argument {args[i - 1]}: expected one argument");
                if (args[i - 1] == "--project-endpoint") endpoint = args[i];
                else if (args[i - 1] == "--project-resource-id") resourceId = args[i];
                else deployment = args[i];
            }
            else return UsageError($"unrecognized argument: {args[i]}");
        }
        var statePath = Path.Combine(Directory.GetCurrentDirectory(), ".azure", "deployment.json");
        if (string.IsNullOrEmpty(resourceId) && File.Exists(statePath))
            resourceId = (string?)JsonNode.Parse(await File.ReadAllTextAsync(statePath))?["outputs"]?["projectResourceId"]?["value"];
        if (!Lifecycle.ValidateProjectResourceId(resourceId, out var account, out var project))
            return UsageError("Set FOUNDRY_PROJECT_RESOURCE_ID to the Bicep projectResourceId");
        var expectedEndpoint = $"https://{account}.services.ai.azure.com/api/projects/{project}";
        if (endpoint != expectedEndpoint)
            return UsageError("FOUNDRY_PROJECT_ENDPOINT must match the ARM projectResourceId (public Azure only)");

        var report = new JsonObject
        {
            ["started_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["rollback_verified"] = false
        };
        try
        {
            var credential = new DefaultAzureCredential();
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            await Lifecycle.RunLifecycleAsync(new Lifecycle.AzureService(http, credential, endpoint!),
                resourceId!, endpoint!, deployment, report);
            return 0;
        }
        catch (Exception ex)
        {
            report["error"] = new JsonObject { ["type"] = ex.GetType().Name, ["message"] = ex.Message };
            throw;
        }
        finally
        {
            await WriteReportAsync("lifecycle-report.json", report);
        }
    }

    private static string[] ExpandEquals(string[] args) => args.SelectMany(argument =>
    {
        var equals = argument.StartsWith("--", StringComparison.Ordinal) ? argument.IndexOf('=') : -1;
        return equals > 0 ? new[] { argument[..equals], argument[(equals + 1)..] } : new[] { argument };
    }).ToArray();

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { return await LifecycleApp.MainAsync(args); }
        catch (Exception ex) { Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}"); return 1; }
    }
}
