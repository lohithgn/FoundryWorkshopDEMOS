using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class GuardrailDemo
{
    private static readonly string Root = AppContext.BaseDirectory;
    private static readonly string Lab = Directory.GetCurrentDirectory();
    private const string Marker = "WORKSHOP_RESTRICTED_ORCHID";
    private const string Policy = "workshop-native-guardrail";
    private const string Blocklist = "workshop-synthetic-marker";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record HttpResult(int Status, JsonElement Body, Dictionary<string, string> Headers);

    private static JsonElement ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static string Output(JsonElement outputs, string key) =>
        outputs.TryGetProperty(key, out var item) && item.TryGetProperty("value", out var value)
            ? value.GetString() ?? "" : "";
    private static string PreferSavedOutput(string saved, string environment) => saved.Length > 0 ? saved : environment;

    private static JsonElement Outputs()
    {
        var path = Path.Combine(Lab, ".azure", "deployment.json");
        return File.Exists(path) ? ReadJson(path).GetProperty("outputs") : JsonDocument.Parse("{}").RootElement.Clone();
    }

    private static string Token(string resource)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "account", "get-access-token", "--resource", resource, "-o", "json" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Azure CLI.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Azure CLI token acquisition failed: {stderr.Trim()}");
        return JsonDocument.Parse(stdout).RootElement.GetProperty("accessToken").GetString()!;
    }

    private static HttpClient Client(string token)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<HttpResult> Send(HttpClient client, HttpMethod method, string url, object? body = null, int timeoutSeconds = 60)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var response = await client.SendAsync(request, cts.Token);
        var text = await response.Content.ReadAsStringAsync(cts.Token);
        JsonElement json;
        try { json = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { json = JsonSerializer.SerializeToElement(new { non_json_body = text[..Math.Min(text.Length, 4000)] }); }
        return new HttpResult((int)response.StatusCode, json,
            response.Headers.Concat(response.Content.Headers).ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase));
    }

    private static void EnsureSuccess(HttpResult result, string action)
    {
        if (result.Status is < 200 or >= 300) throw new HttpRequestException($"{action} failed with HTTP {result.Status}: {result.Body}");
    }

    private static bool NativeIntervention(int status, JsonElement body)
    {
        if (status != 400 || !Try(body, "error", out var error) || !Is(error, "code", "content_filter")
            || !Try(error, "innererror", out var inner) || !Is(inner, "code", "ResponsibleAIPolicyViolation")
            || !Try(inner, "content_filter_result", out var result) || !Try(result, "custom_blocklists", out var lists))
            return false;
        if (lists.ValueKind == JsonValueKind.Array)
            return lists.EnumerateArray().Any(item => IsTrue(item, "filtered") && Is(item, "id", Blocklist));
        if (lists.ValueKind == JsonValueKind.Object)
            return IsTrue(lists, "filtered") && Try(lists, "details", out var details)
                && details.ValueKind == JsonValueKind.Array
                && details.EnumerateArray().Any(item => IsTrue(item, "filtered") && Is(item, "id", Blocklist));
        return false;
    }

    private static bool AgentIntervention(int status, JsonElement body)
    {
        if (status != 400 || !Is(body, "code", "content_filter") || !Try(body, "innererror", out var inner)
            || !Is(inner, "code", "ContentFiltered") || !Try(body, "content_filters", out var filters)
            || filters.ValueKind != JsonValueKind.Array) return false;
        return filters.EnumerateArray().Any(item =>
            IsTrue(item, "blocked") && Is(item, "source_type", "prompt")
            && Try(item, "content_filter_results", out var results)
            && Try(results, "custom_blocklists", out var custom) && custom.ValueKind == JsonValueKind.Array
            && custom.EnumerateArray().Any(entry => IsTrue(entry, "filtered") && Is(entry, "id", Blocklist)));
    }

    private static bool UsableReply(int status, JsonElement body)
    {
        if (status != 200 || !Try(body, "choices", out var choices) || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0) return false;
        var first = choices[0];
        return Is(first, "finish_reason", "stop") && Try(first, "message", out var message)
            && Try(message, "content", out var content) && content.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(content.GetString());
    }

    private static string ResponseText(JsonElement body)
    {
        if (Try(body, "output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
            return direct.GetString() ?? "";
        if (!Try(body, "output", out var output) || output.ValueKind != JsonValueKind.Array) return "";
        return string.Join(" ", output.EnumerateArray().Where(item => Try(item, "content", out _))
            .SelectMany(item => item.GetProperty("content").ValueKind == JsonValueKind.Array
                ? item.GetProperty("content").EnumerateArray().Where(part => Try(part, "text", out _)).Select(part => part.GetProperty("text").GetString() ?? "")
                : Enumerable.Empty<string>()));
    }

    private static bool Try(JsonElement item, string key, out JsonElement value)
    {
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out value)) return true;
        value = default;
        return false;
    }
    private static bool Is(JsonElement item, string key, string expected) =>
        Try(item, key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;
    private static bool IsTrue(JsonElement item, string key) => Try(item, key, out var value) && value.ValueKind == JsonValueKind.True;

    private static async Task<HttpResult> PostCompletion(HttpClient client, string endpoint, string deployment, string prompt)
    {
        var url = $"{endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version=2024-10-21";
        return await Send(client, HttpMethod.Post, url, new
        {
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = 24, temperature = 0
        });
    }

    private static bool HasFilterError(JsonElement body) =>
        Try(body, "choices", out var choices) && choices.ValueKind == JsonValueKind.Array
        && choices.EnumerateArray().Any(choice => Try(choice, "content_filter_result", out var result)
            && Try(result, "error", out var error) && Is(error, "code", "content_filter_error"));

    private static async Task<HttpResult> RequestReady(HttpClient client, string endpoint, string deployment, string prompt, int waitSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (true)
        {
            var result = await PostCompletion(client, endpoint, deployment, prompt);
            if (result.Status != 200 || !HasFilterError(result.Body)) return result;
            var remaining = (deadline - DateTime.UtcNow).TotalSeconds;
            if (remaining <= 0) throw new InvalidOperationException("The service repeatedly reported content_filter_error; filtering was not verified.");
            Console.Error.WriteLine("Service reported content_filter_error; retrying the synthetic probe.");
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(25, remaining)));
        }
    }

    private static async Task<(HttpResult Result, List<object> Attempts)> RequestRestricted(HttpClient client, string endpoint, string deployment, string prompt)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var attempts = new List<object>();
        while (true)
        {
            var left = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalSeconds);
            var result = await RequestReady(client, endpoint, deployment, prompt, Math.Min(120, left));
            var blocked = NativeIntervention(result.Status, result.Body);
            attempts.Add(new { status = result.Status, intervention = blocked });
            if (blocked || !UsableReply(result.Status, result.Body)) return (result, attempts);
            var remaining = (deadline - DateTime.UtcNow).TotalSeconds;
            if (remaining <= 0) return (result, attempts);
            Console.Error.WriteLine("Synthetic marker was not blocked; waiting for the native policy to take effect.");
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(25, remaining)));
        }
    }

    private static async Task<JsonElement> RunModel()
    {
        var outputs = Outputs();
        var endpoint = PreferSavedOutput(Output(outputs, "endpoint"), Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "");
        var deployment = PreferSavedOutput(Output(outputs, "deployment"), Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "");
        var account = Output(outputs, "accountResourceId");
        if (endpoint.Length == 0 || deployment.Length == 0 || account.Length == 0)
            throw new InvalidOperationException("Deploy first: endpoint, deployment and accountResourceId are required.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var modelUri) || modelUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT must be an absolute HTTPS endpoint.");
        using var arm = Client(Token("https://management.azure.com/"));
        var resource = await Send(arm, HttpMethod.Get, $"https://management.azure.com{account}/deployments/{Uri.EscapeDataString(deployment)}?api-version=2025-06-01");
        EnsureSuccess(resource, "ARM deployment read");
        var attached = resource.Body.GetProperty("properties").TryGetProperty("raiPolicyName", out var policy) ? policy.GetString() : null;
        if (attached != Policy) throw new InvalidOperationException($"Native guardrail is not attached: {attached ?? "null"}");

        using var inference = Client(Token("https://cognitiveservices.azure.com/"));
        var evidence = new Dictionary<string, object?> { ["native_policy"] = attached, ["synthetic_only"] = true };
        var probes = new List<object>();
        evidence["requests"] = probes;
        foreach (var (name, prompt) in new[]
        {
            ("allowed", "Reply with exactly: hello workshop"),
            ("restricted", $"The synthetic workshop inventory label is {Marker}. Say hello.")
        })
        {
            HttpResult result;
            List<object> attempts;
            if (name == "restricted")
            {
                await Task.Delay(TimeSpan.FromSeconds(25));
                (result, attempts) = await RequestRestricted(inference, endpoint, deployment, prompt);
            }
            else
            {
                result = await RequestReady(inference, endpoint, deployment, prompt);
                attempts = [];
            }
            probes.Add(new { @case = name, status = result.Status, body = result.Body, propagation_attempts = attempts });
            if (name == "allowed" && !UsableReply(result.Status, result.Body))
                throw new InvalidOperationException($"Allowed request failed: {JsonSerializer.Serialize(evidence)}");
            if (name == "restricted" && !NativeIntervention(result.Status, result.Body))
                throw new InvalidOperationException($"No native custom-blocklist intervention: {JsonSerializer.Serialize(evidence)}");
        }
        evidence["status"] = "PASS";
        return JsonSerializer.SerializeToElement(evidence);
    }

    private static async Task<JsonElement> RunAgent()
    {
        var outputs = Outputs();
        var endpoint = Output(outputs, "projectEndpoint");
        if (endpoint.Length == 0) endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT") ?? "";
        var deployment = Output(outputs, "agentDeployment");
        var account = Output(outputs, "accountResourceId");
        if (endpoint.Length == 0 || deployment.Length == 0 || account.Length == 0)
            throw new InvalidOperationException("Native agent projectEndpoint/agentDeployment/accountResourceId outputs are required.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var projectUri) || projectUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT must be an absolute HTTPS endpoint.");
        var config = Outputs();
        var modelEndpoint = PreferSavedOutput(Output(config, "endpoint"), Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "");
        using var inference = Client(Token("https://cognitiveservices.azure.com/"));
        var control = await RequestReady(inference, modelEndpoint, deployment, $"Inventory marker {Marker}. Say hello.");
        if (!UsableReply(control.Status, control.Body))
            throw new InvalidOperationException($"Unguarded model control failed: HTTP {control.Status} {control.Body}");

        using var project = Client(Token("https://ai.azure.com/"));
        var policyReference = $"{account}/raiPolicies/{Policy}";
        var name = "native-guardrails-" + Guid.NewGuid().ToString("N")[..12];
        var evidence = new Dictionary<string, object?> { ["stage"] = "native-agent", ["status"] = "BLOCKED", ["probes"] = new List<object>() };
        var probes = (List<object>)evidence["probes"]!;
        var probe = new Dictionary<string, object?> { ["agent_name"] = name, ["policy_reference"] = policyReference };
        probes.Add(probe);
        var created = false;
        string version = "";
        try
        {
            var create = await Send(project, HttpMethod.Post, $"{endpoint.TrimEnd('/')}/agents/{Uri.EscapeDataString(name)}/versions?api-version=v1",
                new { definition = new { kind = "prompt", model = deployment, instructions = "Answer benign synthetic workshop questions briefly.", rai_config = new { rai_policy_name = policyReference } } });
            EnsureSuccess(create, "Native PromptAgentDefinition creation");
            created = true;
            version = create.Body.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" :
                create.Body.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array && versions.GetArrayLength() > 0
                    ? versions[0].GetProperty("version").GetString() ?? "" : "";
            if (version.Length == 0) throw new InvalidOperationException($"Agent create response omitted its version: {create.Body}");
            probe["agent_version"] = version;
            var get = await Send(project, HttpMethod.Get, $"{endpoint.TrimEnd('/')}/agents/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(version)}?api-version=v1");
            EnsureSuccess(get, "Native agent version readback");
            var persisted = get.Body.GetProperty("definition");
            probe["persisted_definition"] = persisted;
            if (!Try(persisted, "rai_config", out var rai) || !Is(rai, "rai_policy_name", policyReference))
                throw new InvalidOperationException("Agent did not persist its native RAI attachment.");
            await Task.Delay(TimeSpan.FromSeconds(15));
            var allowed = await Send(project, HttpMethod.Post, $"{endpoint.TrimEnd('/')}/openai/v1/responses?api-version=v1",
                new { input = "Say hello workshop.", max_output_tokens = 64, agent_reference = new { type = "agent_reference", name, version } });
            probe["allowed"] = allowed.Body;
            if (allowed.Status != 200 || !Is(allowed.Body, "status", "completed") || string.IsNullOrWhiteSpace(ResponseText(allowed.Body)))
                throw new InvalidOperationException($"Native agent allowed request failed: HTTP {allowed.Status} {allowed.Body}");
            await Task.Delay(TimeSpan.FromSeconds(15));
            var restricted = await Send(project, HttpMethod.Post, $"{endpoint.TrimEnd('/')}/openai/v1/responses?api-version=v1",
                new { input = $"Inventory marker {Marker}. Say hello.", max_output_tokens = 64, agent_reference = new { type = "agent_reference", name, version } });
            probe["intervention"] = new { status = restricted.Status, body = restricted.Body };
            if (!AgentIntervention(restricted.Status, restricted.Body))
                throw new InvalidOperationException($"Native agent evidence gate failed: HTTP {restricted.Status} {restricted.Body}");
            evidence["status"] = "PASS";
            return JsonSerializer.SerializeToElement(evidence);
        }
        catch (Exception ex)
        {
            probe["error"] = ex.Message;
            return JsonSerializer.SerializeToElement(evidence);
        }
        finally
        {
            if (created)
            {
                var deletion = await Send(project, HttpMethod.Delete,
                    $"{endpoint.TrimEnd('/')}/agents/{Uri.EscapeDataString(name)}?api-version=v1");
                if (deletion.Status is < 200 or >= 300 and not 404)
                    Console.Error.WriteLine($"Temporary agent cleanup failed with HTTP {deletion.Status}.");
            }
        }
    }

    private static bool Test(string name, Func<bool> test)
    {
        var passed = test();
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
        return passed;
    }

    private static bool NativeShapeTests()
    {
        using var doc = JsonDocument.Parse("""{"error":{"code":"content_filter","innererror":{"code":"ResponsibleAIPolicyViolation","content_filter_result":{"custom_blocklists":{"filtered":true,"details":[{"filtered":true,"id":"workshop-synthetic-marker"}]}}}}}""");
        using var list = JsonDocument.Parse("""{"error":{"code":"content_filter","innererror":{"code":"ResponsibleAIPolicyViolation","content_filter_result":{"custom_blocklists":[{"filtered":true,"id":"workshop-synthetic-marker"}]}}}}""");
        using var generic = JsonDocument.Parse("""{"error":{"code":"content_filter"}}""");
        using var wrong = JsonDocument.Parse("""{"error":{"code":"content_filter","innererror":{"code":"ResponsibleAIPolicyViolation","content_filter_result":{"custom_blocklists":[{"filtered":true,"id":"other"}]}}}}""");
        using var missing = JsonDocument.Parse("""{"error":{"code":"content_filter","innererror":{"code":"ResponsibleAIPolicyViolation","content_filter_result":{"custom_blocklists":{"filtered":true,"details":[]}}}}}""");
        using var unfiltered = JsonDocument.Parse("""{"error":{"code":"content_filter","innererror":{"code":"ResponsibleAIPolicyViolation","content_filter_result":{"custom_blocklists":[{"filtered":false,"id":"workshop-synthetic-marker"}]}}}}""");
        using var agent = JsonDocument.Parse("""{"code":"content_filter","innererror":{"code":"ContentFiltered"},"content_filters":[{"blocked":true,"source_type":"prompt","content_filter_results":{"custom_blocklists":[{"filtered":true,"id":"workshop-synthetic-marker"}]}}]}""");
        using var agentUnblocked = JsonDocument.Parse("""{"code":"content_filter","innererror":{"code":"ContentFiltered"},"content_filters":[{"blocked":false,"source_type":"prompt","content_filter_results":{"custom_blocklists":[{"filtered":true,"id":"workshop-synthetic-marker"}]}}]}""");
        using var control = JsonDocument.Parse("""{"choices":[{"finish_reason":"stop","message":{"content":"hello"}}]}""");
        using var noReply = JsonDocument.Parse("""{"choices":[{"finish_reason":"stop","message":{"content":null}}]}""");
        using var filterError = JsonDocument.Parse("""{"choices":[{"content_filter_result":{"error":{"code":"content_filter_error"}}}]}""");
        using var otherError = JsonDocument.Parse("""{"choices":[{"content_filter_result":{"error":{"code":"other_error"}}}]}""");
        using var completedResponse = JsonDocument.Parse("""{"output":[{"type":"message","content":[{"type":"output_text","text":"hello"}]}]}""");
        var checks = new (string Name, Func<bool> Run)[]
        {
            ("legacy native custom-blocklist shape", () => NativeIntervention(400, doc.RootElement)),
            ("current native list shape", () => NativeIntervention(400, list.RootElement)),
            ("wrong status rejected", () => !NativeIntervention(403, doc.RootElement)),
            ("generic content filter rejected", () => !NativeIntervention(400, generic.RootElement)),
            ("missing exact blocklist evidence rejected", () => !NativeIntervention(400, missing.RootElement)),
            ("wrong blocklist rejected", () => !NativeIntervention(400, wrong.RootElement)),
            ("unfiltered marker rejected", () => !NativeIntervention(400, unfiltered.RootElement)),
            ("native agent intervention shape", () => AgentIntervention(400, agent.RootElement)),
            ("model gate cannot satisfy agent gate", () => !AgentIntervention(400, doc.RootElement)),
            ("agent must report blocked prompt", () => !AgentIntervention(400, agentUnblocked.RootElement)),
            ("successful unfiltered reply gate", () => UsableReply(200, control.RootElement)),
            ("missing reply text rejected", () => !UsableReply(200, noReply.RootElement)),
            ("filter retry recognizes only content_filter_error", () => HasFilterError(filterError.RootElement) && !HasFilterError(otherError.RootElement)),
            ("response output text extraction", () => ResponseText(completedResponse.RootElement) == "hello"),
            ("saved root output overrides environment", () => PreferSavedOutput("root-endpoint", "wrong-environment") == "root-endpoint"),
            ("environment supplies missing output", () => PreferSavedOutput("", "environment") == "environment")
        };
        var passed = checks.Count(check => Test(check.Name, check.Run));
        Console.WriteLine($"{passed}/{checks.Length} native guardrail offline checks passed.");
        return passed == checks.Length;
    }

    public static async Task<int> MainAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file demo.cs [-- --self-test]\nRuns native Azure guardrail model and agent evidence gates; --self-test is offline.");
            return 0;
        }
        if (args.Contains("--self-test"))
        {
            if (!NativeShapeTests()) return 1;
            Console.WriteLine("Native guardrail offline checks passed.");
            return 0;
        }
        var stages = new Dictionary<string, string> { ["model"] = "BLOCKED", ["agent"] = "NOT_ATTEMPTED" };
        try
        {
            var baseline = await RunModel();
            stages["model"] = "PASS";
            Directory.CreateDirectory(Path.Combine(Lab, ".azure"));
            await File.WriteAllTextAsync(Path.Combine(Lab, ".azure", "model-evidence.json"), JsonSerializer.Serialize(baseline, JsonOptions));
            stages["agent"] = "BLOCKED";
            var agent = await RunAgent();
            stages["agent"] = agent.GetProperty("status").GetString() ?? "BLOCKED";
            await File.WriteAllTextAsync(Path.Combine(Lab, ".azure", "agent-evidence.json"), JsonSerializer.Serialize(agent, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(new { status = stages["agent"], stages, agent }, JsonOptions));
            return stages["agent"] == "PASS" ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "BLOCKED", stages, stage = "native-agent-live", error = ex.Message }, JsonOptions));
            return 1;
        }
        finally
        {
            Directory.CreateDirectory(Path.Combine(Lab, ".azure"));
            await File.WriteAllTextAsync(Path.Combine(Lab, ".azure", "live-report.json"),
                JsonSerializer.Serialize(new { implemented = true, live = stages.Values.All(x => x == "PASS") ? "PASS" : "BLOCKED",
                    stages, agent_boundary = "user-input", tool_boundary = "NOT_CONFIGURED" }, JsonOptions));
        }
    }
}
