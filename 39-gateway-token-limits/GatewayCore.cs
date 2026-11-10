using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class GatewayDemo
{
    private static readonly string Lab = Directory.GetCurrentDirectory();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string ApiVersion = "2024-05-01";
    private sealed record Result(int Status, JsonElement Body, Dictionary<string, string> Headers, string Text);

    private static JsonElement ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static JsonElement Outputs()
    {
        var path = Path.Combine(Lab, ".azure", "deployment.json");
        return File.Exists(path) ? ReadJson(path).GetProperty("outputs") : JsonDocument.Parse("{}").RootElement.Clone();
    }
    private static string Output(JsonElement outputs, string key) =>
        outputs.TryGetProperty(key, out var item) && item.TryGetProperty("value", out var value) ? value.GetString() ?? "" : "";
    private static string Endpoint()
    {
        var endpoint = Output(Outputs(), "gatewayEndpoint");
        if (endpoint.Length == 0) endpoint = Environment.GetEnvironmentVariable("AI_GATEWAY_ENDPOINT") ?? "";
        if (!endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Missing HTTPS AI_GATEWAY_ENDPOINT; deploy this lab first.");
        return endpoint.TrimEnd('/');
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
    private static HttpClient NewClient(string? token = null)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    private static async Task<Result> Send(HttpClient client, HttpMethod method, string url, object? body = null, int timeout = 60)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var response = await client.SendAsync(request, cancellation.Token);
        var text = await response.Content.ReadAsStringAsync(cancellation.Token);
        JsonElement json;
        try { json = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { json = JsonSerializer.SerializeToElement(new { non_json_body = text[..Math.Min(text.Length, 4000)] }); }
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(item => item.Key, item => string.Join(",", item.Value), StringComparer.OrdinalIgnoreCase);
        return new Result((int)response.StatusCode, json, headers, text);
    }
    private static void Success(Result result, string action)
    {
        if (result.Status is < 200 or >= 300) throw new HttpRequestException($"{action} failed with HTTP {result.Status}: {result.Body}");
    }
    private static async Task<Result> SendWithStartupRetries(
        Func<Task<Result>> send, bool firstRequest, Func<TimeSpan, Task> wait, List<object> retries)
    {
        Result response = null!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            response = await send();
            if (!(firstRequest && response.Status == 404 && attempt < 2)) break;
            retries.Add(new { status = response.Status, reason = "APIM route/new counter-policy propagation" });
            await wait(TimeSpan.FromSeconds(15));
        }
        return response;
    }

    private static bool CounterRunMatches(Result response, string runId) =>
        response.Headers.TryGetValue("X-Workshop-Counter-Run", out var value) && value == runId;
    private static void EnsureCounterRun(Result response, string runId, string evidence)
    {
        if (!CounterRunMatches(response, runId))
            throw new InvalidOperationException($"Gateway has not adopted this run's real counter policy: {evidence}");
    }

    private static bool Intervention(string kind, int status, IReadOnlyDictionary<string, string> headers) =>
        status == (kind == "rate" ? 429 : 403)
        && headers.TryGetValue("X-Workshop-Policy", out var policy) && policy == "workshop-token-limit"
        && headers.TryGetValue("X-Workshop-Reason", out var reason)
        && reason == (kind == "rate" ? "OpenAITokenLimitExceeded" : "OpenAITokenQuotaExceeded");
    private static bool ModelSuccess(int status, JsonElement body) =>
        status == 200 && Try(body, "usage", out var usage) && Try(usage, "total_tokens", out var tokens)
        && tokens.TryGetInt32(out var count) && count > 0 && Try(body, "choices", out var choices)
        && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
        && Try(choices[0], "message", out var message) && Try(message, "content", out var content)
        && content.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(content.GetString());
    private static bool Try(JsonElement item, string key, out JsonElement value)
    {
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out value)) return true;
        value = default;
        return false;
    }

    private static XElement ParsePolicy(string text)
    {
        text = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (!text.StartsWith('<'))
        {
            using var json = JsonDocument.Parse(text);
            text = json.RootElement.GetProperty("properties").GetProperty("value").GetString()!;
        }
        return XElement.Parse(text);
    }

    private static string GatewayId(JsonElement outputs) => Output(outputs, "gatewayResourceId");
    private static Dictionary<string, string> Projects(JsonElement outputs) => new()
    {
        ["project-a"] = Output(outputs, "projectResourceId"),
        ["project-b"] = Output(outputs, "secondProjectResourceId")
    };
    private static string PolicyUrl(string gateway, string route) =>
        $"https://management.azure.com{gateway}/apis/{route}/policies/policy?api-version={ApiVersion}";

    private static async Task<List<(string Route, string ProjectId, string Kind, XElement Policy)>> LoadPolicies(
        HttpClient arm, string? runId = null, bool verify = true)
    {
        var outputs = Outputs();
        var gateway = GatewayId(outputs);
        var projects = Projects(outputs);
        if (gateway.Length == 0 || projects.Values.Any(string.IsNullOrEmpty))
            throw new InvalidOperationException("Missing gateway/project ARM outputs; deploy this lab first.");
        var result = new List<(string, string, string, XElement)>();
        foreach (var (project, projectId) in projects)
        foreach (var kind in new[] { "rate", "quota" })
        {
            var route = $"{project}-{kind}";
            var response = await Send(arm, HttpMethod.Get, PolicyUrl(gateway, route));
            Success(response, $"ARM {route} policy read");
            var policy = ParsePolicy(response.Text);
            var limit = policy.Element("inbound")?.Element("llm-token-limit")
                ?? throw new InvalidOperationException($"ARM policy has no llm-token-limit: {route}");
            var exact = kind == "rate"
                ? limit.Attribute("tokens-per-minute")?.Value == "100"
                : limit.Attribute("token-quota")?.Value == "100" && limit.Attribute("token-quota-period")?.Value == "Hourly";
            var baseKey = $"{projectId}-{kind}";
            var key = limit.Attribute("counter-key")?.Value ?? "";
            var keyValid = runId is not null ? key == $"{baseKey}-run-{runId}"
                : Regex.IsMatch(key, "^" + Regex.Escape(baseKey) + "(?:-run-[0-9a-f]{32})?$");
            if (verify && (!exact || !keyValid || limit.Attribute("id")?.Value != "workshop-token-limit"))
                throw new InvalidOperationException($"ARM project-scoped counter or evidence policy ID differs: {route}");
            result.Add((route, projectId, kind, policy));
        }
        return result;
    }

    private static XElement Rotate(XElement policy, string baseKey, string runId)
    {
        var limit = policy.Element("inbound")?.Element("llm-token-limit")
            ?? throw new InvalidOperationException("Cannot rotate a missing llm-token-limit policy.");
        limit.SetAttributeValue("counter-key", $"{baseKey}-run-{runId}");
        foreach (var section in new[] { "outbound", "on-error" })
        {
            var parent = policy.Element(section) ?? throw new InvalidOperationException($"Policy has no {section} section.");
            parent.Elements("set-header").Where(node => (string?)node.Attribute("name") == "X-Workshop-Counter-Run").Remove();
            parent.Add(new XElement("set-header", new XAttribute("name", "X-Workshop-Counter-Run"),
                new XAttribute("exists-action", "override"), new XElement("value", runId)));
        }
        return policy;
    }

    private static async Task<string> RotateCounters(HttpClient arm)
    {
        var policies = await LoadPolicies(arm);
        var gateway = GatewayId(Outputs());
        var runId = Guid.NewGuid().ToString("N");
        foreach (var (route, projectId, kind, _) in policies)
        {
            var policy = (await Send(arm, HttpMethod.Get, PolicyUrl(gateway, route)));
            Success(policy, $"ARM {route} policy read before rotation");
            var xml = Rotate(ParsePolicy(policy.Text), $"{projectId}-{kind}", runId);
            var response = await Send(arm, HttpMethod.Put, PolicyUrl(gateway, route),
                new { properties = new { format = "rawxml", value = xml.ToString(SaveOptions.DisableFormatting) } });
            Success(response, $"ARM {route} counter rotation");
        }
        Directory.CreateDirectory(Path.Combine(Lab, ".azure"));
        await File.WriteAllTextAsync(Path.Combine(Lab, ".azure", "counter-run.json"),
            JsonSerializer.Serialize(new { run_id = runId, limits = new { TPM = 100, Hourly = 100 } }, JsonOptions));
        await Task.Delay(TimeSpan.FromSeconds(20));
        return runId;
    }

    private static async Task<JsonElement> RunLive()
    {
        var endpoint = Endpoint();
        using var arm = NewClient(Token("https://management.azure.com/"));
        var runId = await RotateCounters(arm);
        var verified = await LoadPolicies(arm, runId);
        using var session = NewClient(Token("https://cognitiveservices.azure.com/"));
        var evidence = new Dictionary<string, object?>
        {
            ["gateway"] = endpoint, ["policy"] = "llm-token-limit", ["run_id"] = runId,
            ["arm_verified_policies"] = verified.Select(item => new {
                route = item.Route, project_resource_id = item.ProjectId,
                limits = item.Kind == "rate" ? (object)new { tokens_per_minute = "100" } : new { token_quota = "100", token_quota_period = "Hourly" },
                counter_key = (string?)item.Policy.Element("inbound")?.Element("llm-token-limit")?.Attribute("counter-key")
            }).ToArray(),
            ["requests"] = new List<object>()
        };
        var requests = (List<object>)evidence["requests"]!;
        async Task<Result> Request(string project, string kind, string prompt)
        {
            if (requests.Count > 0) await Task.Delay(TimeSpan.FromSeconds(7));
            var firstRequest = requests.Count == 0;
            var retries = new List<object>();
            var response = await SendWithStartupRetries(
                () => Send(session, HttpMethod.Post, $"{endpoint}/{project}-{kind}/chat/completions",
                    new { messages = new[] { new { role = "user", content = prompt } }, max_tokens = 16, temperature = 0 }),
                firstRequest, delay => Task.Delay(delay), retries);
            if (retries.Count > 0)
            {
                if (!evidence.TryGetValue("startup_retries", out var startup))
                    evidence["startup_retries"] = startup = new List<object>();
                ((List<object>)startup!).AddRange(retries);
            }
            JsonElement safeHeaders = JsonSerializer.SerializeToElement(response.Headers
                .Where(pair => new[] { "X-Workshop-Policy", "X-Workshop-Reason", "Retry-After", "X-Workshop-Counter-Run",
                    "X-Tokens-Consumed", "X-Rate-Remaining", "X-Quota-Remaining" }.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value));
            requests.Add(new { project, kind, status = response.Status, headers = safeHeaders, body = response.Body });
            EnsureCounterRun(response, runId, JsonSerializer.Serialize(evidence));
            return response;
        }

        foreach (var kind in new[] { "rate", "quota" })
        {
            var response = await Request("project-a", kind, "Say hello workshop.");
            if (!ModelSuccess(response.Status, response.Body))
                throw new InvalidOperationException($"Initial allowed request failed; counters may need reset: {JsonSerializer.Serialize(evidence)}");
            response = await Request("project-a", kind, "Synthetic inventory: " + string.Concat(Enumerable.Repeat("orchid shelf alpha ", 100)) + ". Reply hello.");
            if (!ModelSuccess(response.Status, response.Body))
                throw new InvalidOperationException($"Budget-consuming model request failed: {JsonSerializer.Serialize(evidence)}");
            response = await Request("project-a", kind, "Say hello.");
            if (!Intervention(kind, response.Status, response.Headers))
                throw new InvalidOperationException($"No APIM {kind} intervention: {JsonSerializer.Serialize(evidence)}");
            response = await Request("project-b", kind, "Say hello other project.");
            if (!ModelSuccess(response.Status, response.Body))
                throw new InvalidOperationException($"Other project did not remain available: {JsonSerializer.Serialize(evidence)}");
        }
        evidence["status"] = "PASS";
        return JsonSerializer.SerializeToElement(evidence);
    }

    private static XElement TemplatePolicy(string template, string limit) =>
        XElement.Parse(template.Replace("__TENANT__", "tenant").Replace("__PRINCIPAL__", "principal")
            .Replace("__COUNTER__", "/project-id-rate").Replace("__LIMIT__", limit));

    private static bool Test(string name, Func<bool> test)
    {
        var passed = test();
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
        return passed;
    }

    private static Result StubResult(int status, string? runId = "current-run") =>
        new(status, default, runId is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Workshop-Counter-Run"] = runId }, "");

    private static async Task<(Result Response, int Requests, List<TimeSpan> Delays, List<object> Retries)> SimulateRequests(
        bool firstRequest, params Result[] responses)
    {
        var index = 0;
        var delays = new List<TimeSpan>();
        var retries = new List<object>();
        var response = await SendWithStartupRetries(
            () => Task.FromResult(responses[index++]), firstRequest,
            delay => { delays.Add(delay); return Task.CompletedTask; }, retries);
        return (response, index, delays, retries);
    }

    private static async Task<bool> RetryBehaviorTests()
    {
        var recovered = await SimulateRequests(true,
            StubResult(404, null), StubResult(404, null), StubResult(200));
        var exhausted = await SimulateRequests(true,
            StubResult(404, null), StubResult(404, null), StubResult(404, null));
        var rate = await SimulateRequests(true, StubResult(429), StubResult(200));
        var quota = await SimulateRequests(true, StubResult(403), StubResult(200));
        var otherFailure = await SimulateRequests(true, StubResult(500), StubResult(200));
        var later404 = await SimulateRequests(false, StubResult(404));
        var mismatched = await SimulateRequests(true, StubResult(200, "stale-run"), StubResult(200));
        var missingHeader = await SimulateRequests(true,
            StubResult(404, null), StubResult(404, null), StubResult(404, null));
        var missingRunRejected = false;
        try { EnsureCounterRun(missingHeader.Response, "current-run", "{}"); }
        catch (InvalidOperationException) { missingRunRejected = true; }
        var mismatchedRunRejected = false;
        try { EnsureCounterRun(mismatched.Response, "current-run", "{}"); }
        catch (InvalidOperationException) { mismatchedRunRejected = true; }
        var checks = new (string Name, bool Passed)[]
        {
            ("only initial 404 gets exactly two 15-second retry slots",
                recovered.Requests == 3 && recovered.Delays.Count == 2
                && recovered.Delays.All(delay => delay == TimeSpan.FromSeconds(15)) && recovered.Retries.Count == 2
                && recovered.Response.Status == 200 && CounterRunMatches(recovered.Response, "current-run")),
            ("final initial 404 fails after two retries",
                exhausted.Requests == 3 && exhausted.Delays.Count == 2
                && exhausted.Delays.All(delay => delay == TimeSpan.FromSeconds(15)) && exhausted.Retries.Count == 2
                && exhausted.Response.Status == 404 && !CounterRunMatches(exhausted.Response, "current-run")
                && !ModelSuccess(exhausted.Response.Status, exhausted.Response.Body)),
            ("rate/quota and other initial failures are never retried",
                rate.Requests == 1 && rate.Delays.Count == 0 && rate.Retries.Count == 0 && rate.Response.Status == 429
                && quota.Requests == 1 && quota.Delays.Count == 0 && quota.Retries.Count == 0 && quota.Response.Status == 403
                && otherFailure.Requests == 1 && otherFailure.Delays.Count == 0 && otherFailure.Retries.Count == 0 && otherFailure.Response.Status == 500),
            ("non-initial 404 is not retried",
                later404.Requests == 1 && later404.Delays.Count == 0 && later404.Retries.Count == 0
                && later404.Response.Status == 404 && CounterRunMatches(later404.Response, "current-run")
                && !ModelSuccess(later404.Response.Status, later404.Response.Body)),
            ("mismatched counter run fails without retry",
                mismatched.Requests == 1 && mismatched.Delays.Count == 0 && mismatched.Retries.Count == 0
                && !CounterRunMatches(mismatched.Response, "current-run") && mismatchedRunRejected),
            ("missing counter header fails after final initial 404",
                missingHeader.Requests == 3 && missingHeader.Delays.Count == 2 && missingRunRejected)
        };
        var passed = checks.Count(check => Test(check.Name, () => check.Passed));
        Console.WriteLine($"{passed}/{checks.Length} startup retry offline checks passed.");
        return passed == checks.Length;
    }

    private static async Task<bool> SelfTest()
    {
        var rate = new Dictionary<string, string> { ["X-Workshop-Policy"] = "workshop-token-limit", ["X-Workshop-Reason"] = "OpenAITokenLimitExceeded" };
        var quota = new Dictionary<string, string> { ["X-Workshop-Policy"] = "workshop-token-limit", ["X-Workshop-Reason"] = "OpenAITokenQuotaExceeded" };
        using var usage = JsonDocument.Parse("""{"usage":{"total_tokens":18},"choices":[{"message":{"content":"hello"}}]}""");
        using var noUsage = JsonDocument.Parse("""{"choices":[{"message":{"content":"hello"}}]}""");
        using var noChoices = JsonDocument.Parse("""{"usage":{"total_tokens":18},"choices":[]}""");
        var policy = XElement.Parse("""<policies><inbound><llm-token-limit tokens-per-minute="100" counter-key="old" /></inbound><outbound/><on-error/></policies>""");
        var before = policy.Element("inbound")!.Element("llm-token-limit")!.Attribute("tokens-per-minute")!.Value;
        Rotate(policy, "/project-a-rate", "one");
        Rotate(policy, "/project-a-rate", "two");
        var headersValid = new[] { "outbound", "on-error" }.All(section =>
            policy.Element(section)!.Elements("set-header").Count() == 1
            && policy.Element(section)!.Element("set-header")!.Element("value")!.Value == "two");
        var source = File.ReadAllText(Path.Combine(Lab, "infra", "gateway-policy.xml"));
        var ratePolicy = TemplatePolicy(source, "tokens-per-minute=\"100\" remaining-tokens-header-name=\"X-Rate-Remaining\"");
        var quotaPolicy = TemplatePolicy(source, "token-quota=\"100\" token-quota-period=\"Hourly\" remaining-quota-tokens-header-name=\"X-Quota-Remaining\"");
        var rl = ratePolicy.Element("inbound")!.Element("llm-token-limit")!;
        var ql = quotaPolicy.Element("inbound")!.Element("llm-token-limit")!;
        var parsedBom = ParsePolicy("\uFEFF<policies><inbound /></policies>").Name.LocalName == "policies";
        var parsedJson = ParsePolicy("""{"properties":{"value":"<policies />"}}""").Name.LocalName == "policies";
        var checks = new (string Name, Func<bool> Run)[]
        {
            ("native rate intervention headers", () => Intervention("rate", 429, rate)),
            ("native hourly quota intervention headers", () => Intervention("quota", 403, quota)),
            ("backend 429 is not gateway evidence", () => !Intervention("rate", 429, new Dictionary<string, string> { ["Retry-After"] = "60" })),
            ("permission 403 is not quota evidence", () => !Intervention("quota", 403, new Dictionary<string, string>())),
            ("HTTP status and reason must match", () => !Intervention("quota", 429, rate)),
            ("policy ID must match", () => !Intervention("rate", 429, new Dictionary<string, string> { ["X-Workshop-Policy"] = "other", ["X-Workshop-Reason"] = "OpenAITokenLimitExceeded" })),
            ("backend token usage is required", () => !ModelSuccess(200, noUsage.RootElement)),
            ("successful response includes usage and content", () => ModelSuccess(200, usage.RootElement)),
            ("empty choices cannot pass", () => !ModelSuccess(200, noChoices.RootElement)),
            ("ARM XML with BOM parses", () => parsedBom),
            ("documented ARM JSON policy parses", () => parsedJson),
            ("counter rotation retains exact limits", () => before == policy.Element("inbound")!.Element("llm-token-limit")!.Attribute("tokens-per-minute")!.Value),
            ("counter rotation uses latest isolated run", () => policy.Element("inbound")!.Element("llm-token-limit")!.Attribute("counter-key")!.Value == "/project-a-rate-run-two"),
            ("counter run evidence headers are idempotent", () => headersValid),
            ("real rate policy template", () => rl.Attribute("tokens-per-minute")?.Value == "100"
                && rl.Attribute("id")?.Value == "workshop-token-limit" && rl.Attribute("estimate-prompt-tokens")?.Value == "false"
                && ratePolicy.Element("inbound")!.Element("authentication-managed-identity") is not null
                && ratePolicy.Element("inbound")!.Element("validate-azure-ad-token")?.Element("audiences")?.Element("audience")?.Value == "https://cognitiveservices.azure.com"),
            ("real hourly policy template", () => ql.Attribute("token-quota")?.Value == "100"
                && ql.Attribute("token-quota-period")?.Value == "Hourly" && ql.Attribute("id")?.Value == "workshop-token-limit"
                && quotaPolicy.Element("on-error")!.Elements("set-header").Any(node => (string?)node.Attribute("name") == "X-Workshop-Reason"))
        };
        var passed = checks.Count(check => Test(check.Name, check.Run));
        Console.WriteLine($"{passed}/{checks.Length} gateway token-limit offline checks passed.");
        var retryChecks = await RetryBehaviorTests();
        return passed == checks.Length && retryChecks;
    }

    public static async Task<int> MainAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file demo.cs [-- --self-test]\nRuns real APIM token-limit counter, enforcement and project-isolation checks.");
            return 0;
        }
        if (args.Contains("--self-test"))
        {
            if (!await SelfTest()) return 1;
            Console.WriteLine("Gateway token-limit offline checks passed.");
            return 0;
        }
        JsonElement? result = null;
        string? error = null;
        try
        {
            result = await RunLive();
            Directory.CreateDirectory(Path.Combine(Lab, ".azure"));
            await File.WriteAllTextAsync(Path.Combine(Lab, ".azure", "gateway-evidence.json"), JsonSerializer.Serialize(result.Value, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(result.Value, JsonOptions));
            return 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "BLOCKED", stage = "gateway-token-limit-live", error }, JsonOptions));
            return 1;
        }
        finally
        {
            Directory.CreateDirectory(Path.Combine(Lab, ".azure"));
            await File.WriteAllTextAsync(Path.Combine(Lab, ".azure", "live-report.json"),
                JsonSerializer.Serialize(new { implemented = true, live = result.HasValue ? "PASS" : "BLOCKED",
                    run_id = result.HasValue && Try(result.Value, "run_id", out var run) ? run.GetString() : null,
                    counter_reset = "privileged per-run APIM counters; unchanged 100 TPM/100 hourly tokens", error }, JsonOptions));
        }
    }
}
