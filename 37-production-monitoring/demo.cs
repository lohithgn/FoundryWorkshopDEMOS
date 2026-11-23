#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:package Azure.AI.Projects@3.0.0-beta.6
#:package Azure.AI.Projects.Agents@3.0.0-beta.6
#:package Azure.AI.Extensions.OpenAI@3.0.0-beta.6
#:package Azure.Identity@1.21.0
#:include MonitoringCore.cs

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.AI.Projects.Evaluation;
using Azure.Identity;

await MonitoringDemo.RunAsync(args);

public static class MonitoringDemo
{
    static readonly string State = Path.Combine(Environment.CurrentDirectory, ".azure");
    static readonly string[] Names = ProductionMonitoring.MonitoringCore.Names;
    const string Question = "What is the workshop price per attendee?";
    const string TokenScope = "https://ai.azure.com/.default";
    static readonly AzureCliCredential Credential = new();

    static string PathFor(string name) => Path.Combine(State, name);

    static JsonElement ReadJson(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) throw new InvalidOperationException($"Required state file {name} is missing; deploy this lab first.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    static void Save(string name, object data)
    {
        Directory.CreateDirectory(State);
        var path = PathFor(name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temp, path, true);
    }

    static string Output(string name)
    {
        var outputs = ReadJson("deployment.json");
        var root = outputs.TryGetProperty("outputs", out var wrapped) ? wrapped : outputs;
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Object ? value.GetProperty("value").GetString()! : value.GetString()!;
    }

    static JsonElement Config() => ReadJson("agents.json");

    static AIProjectClient Project() => new(new Uri(Output("projectEndpoint")), Credential);

    static async Task<JsonElement> RequestAsync(HttpMethod method, string url, object? body = null, CancellationToken ct = default)
    {
        var token = await Credential.GetTokenAsync(new Azure.Core.TokenRequestContext([TokenScope]), ct);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"Foundry API returned an error: {error}");
        return document.RootElement.Clone();
    }

    static async Task<JsonElement> OpenAiAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default) =>
        await RequestAsync(method, Output("projectEndpoint").TrimEnd('/') + "/openai/v1" + path, body, ct);

    static async Task<JsonElement> InvokeAsync(string agent, string phase, CancellationToken ct)
    {
        var details = (await new AgentAdministrationClient(new Uri(Output("projectEndpoint")), Credential)
            .GetAgentAsync(agent, ct)).Value;
        var version = details.AgentEndpoint?.VersionSelector?.VersionSelectionRules?.FirstOrDefault()?.AgentVersion
            ?? throw new InvalidOperationException($"Agent {agent} has no selected version.");
        var started = Stopwatch.StartNew();
        var response = await OpenAiAsync(HttpMethod.Post, "/responses", new
        {
            input = Question,
            max_output_tokens = 128,
            store = true,
            metadata = new { lab = "37", phase, run = Guid.NewGuid().ToString("N") },
            agent_reference = new { name = agent }
        }, ct);
        started.Stop();
        if (response.GetProperty("status").GetString() != "completed"
            || !response.TryGetProperty("output_text", out var answerValue)
            || string.IsNullOrEmpty(answerValue.GetString()))
            throw new InvalidOperationException($"Incomplete deployed response: {response}");
        var answer = answerValue.GetString()!.Trim();
        var expectedPass = !(phase == "regression" && agent == Names[0]);
        var expectedAnswer = expectedPass ? "USD 25" : "USD 40";
        if (answer != expectedAnswer)
            throw new InvalidOperationException($"Deployed endpoint did not demonstrate {phase}: {agent} answered {answer}");
        var record = JsonSerializer.SerializeToElement(new
        {
            agent, phase, configured_version = version,
            response_id = response.GetProperty("id").GetString(),
            answer, seconds = Math.Round(started.Elapsed.TotalSeconds, 3),
            usage = response.TryGetProperty("usage", out var usage) ? usage.Clone() : (JsonElement?)null,
            expected_pass = expectedPass
        });
        Console.WriteLine(record);
        return record;
    }

    static async Task<JsonElement> CreateEvaluationAsync(string name, CancellationToken ct)
    {
        return await OpenAiAsync(HttpMethod.Post, "/evals", new
        {
            name = $"Live price correctness - {name}",
            data_source_config = new { type = "azure_ai_source", scenario = "responses" },
            testing_criteria = new[] { new { type = "string_check", name = "price_correct", input = "{{sample.output_text}}", operation = "eq", reference = "USD 25" } }
        }, ct);
    }

    static async Task<ProjectsAgentVersion> CreatePromptVersionAsync(
        AgentAdministrationClient agents, string agentName, string model, string instructions, CancellationToken ct)
    {
        var definition = new DeclarativeAgentDefinition(model) { Instructions = instructions };
        var creation = new ProjectsAgentVersionCreationOptions(definition);
        creation.Metadata["enableVnextExperience"] = "true";
        return await agents.CreateAgentVersionAsync(agentName, creation, cancellationToken: ct);
    }

    static async Task<JsonElement> RouteAsync(AgentAdministrationClient agents, string name, string version, CancellationToken ct)
    {
        var route = new AgentEndpointConfiguration
        {
            VersionSelector = new([new FixedRatioVersionSelectionRule(agentVersion: version, trafficPercentage: 100)]),
            ProtocolConfiguration = new() { Responses = new() }
        };
        var patched = (await agents.PatchAgentAsync(name, new PatchAgentOptions { AgentEndpoint = route }, ct)).Value;
        var selected = patched.AgentEndpoint?.VersionSelector?.VersionSelectionRules?.FirstOrDefault()?.AgentVersion;
        if (selected != version) throw new InvalidOperationException($"Cloud route read-back did not select {name} version {version}: {selected}");
        return JsonSerializer.SerializeToElement(new { name = patched.Name, version = selected });
    }

    public static async Task<JsonElement> SetupAsync(CancellationToken ct)
    {
        var config = new Dictionary<string, object>();
        if (File.Exists(PathFor("agents.json")))
            foreach (var existing in Config().GetProperty("agents").EnumerateObject())
                config[existing.Name] = existing.Value.Clone();
        var project = Project();
        var rules = project.EvaluationRules;
        var agents = new AgentAdministrationClient(new Uri(Output("projectEndpoint")), Credential);
        foreach (var name in Names)
        {
            if (config.TryGetValue(name, out var savedValue))
            {
                var saved = (JsonElement)savedValue;
                await rules.GetAsync(saved.GetProperty("rule_id").GetString()!, ct);
                continue;
            }
            var good = await CreatePromptVersionAsync(agents, name, Output("deployment"),
                "For every input, respond with exactly these six characters: USD 25", ct);
            ProjectsAgentVersion? bad = null;
            if (name == Names[0])
                bad = await CreatePromptVersionAsync(agents, name, Output("deployment"),
                    "For every input, respond with exactly these six characters: USD 40", ct);
            var eval = await CreateEvaluationAsync(name, ct);
            var evalId = eval.GetProperty("id").GetString()!;
            var ruleId = name + "-live-quality";
            var action = new ContinuousEvaluationRuleAction(evalId) { MaxHourlyRuns = 30, SamplingRate = 100 };
            var rule = new EvaluationRule(action, EvaluationRuleEventType.ResponseCompleted, true)
            {
                DisplayName = $"Live price correctness for {name}",
                Description = "Evaluate actual completed responses; bounded to 30 runs/hour.",
                Filter = new EvaluationRuleFilter(name)
            };
            var result = (await rules.CreateOrUpdateAsync(ruleId, rule, FoundryFeaturesOptInKeys.EvaluationsV1Preview, ct)).Value;
            var goodRoute = await RouteAsync(agents, name, good.Version, ct);
            config[name] = new
            {
                good_version = good.Version,
                bad_version = bad?.Version,
                eval_id = evalId,
                rule_id = ruleId,
                rule = new { id = result.Id, enabled = result.Enabled }
            };
            Save("agents.json", new { agents = config });
        }
        return JsonSerializer.SerializeToElement(new { agents = config });
    }

    public static async Task<JsonElement> SetModeAsync(string mode, CancellationToken ct)
    {
        ProductionMonitoring.MonitoringCore.ValidateMode(mode);
        var config = Config().GetProperty("agents");
        var agents = new AgentAdministrationClient(new Uri(Output("projectEndpoint")), Credential);
        var routes = new Dictionary<string, JsonElement>();
        foreach (var name in Names)
        {
            var entry = config.GetProperty(name);
            var version = mode == "regression" && name == Names[0]
                ? entry.GetProperty("bad_version").GetString()!
                : entry.GetProperty("good_version").GetString()!;
            routes[name] = await RouteAsync(agents, name, version, ct);
        }
        return JsonSerializer.SerializeToElement(new { mode, routes });
    }

    public static async Task<List<JsonElement>> TrafficAsync(string mode, CancellationToken ct)
    {
        await SetModeAsync(mode, ct);
        var records = new List<JsonElement>();
        foreach (var name in Names) records.Add(await InvokeAsync(name, mode, ct));
        return records;
    }

    public static async Task<List<JsonElement>> EvaluationResultsAsync(CancellationToken ct)
    {
        var config = Config().GetProperty("agents");
        var records = new List<JsonElement>();
        foreach (var name in Names)
        {
            var evalId = config.GetProperty(name).GetProperty("eval_id").GetString()!;
            var page = await OpenAiAsync(HttpMethod.Get, $"/evals/{Uri.EscapeDataString(evalId)}/runs?limit=30&order=desc", ct: ct);
            if (!page.TryGetProperty("data", out var runs)) continue;
            foreach (var run in runs.EnumerateArray())
            {
                if (run.TryGetProperty("metadata", out var metadata)
                    && metadata.TryGetProperty("trigger_type", out var trigger)
                    && trigger.GetString() == "oneoff") continue;
                var status = run.GetProperty("status").GetString();
                if (status is "failed" or "canceled") throw new InvalidOperationException($"Native continuous evaluation failed: {run}");
                if (status != "completed") continue;
                var runId = run.GetProperty("id").GetString()!;
                var items = await OpenAiAsync(HttpMethod.Get,
                    $"/evals/{Uri.EscapeDataString(evalId)}/runs/{Uri.EscapeDataString(runId)}/output_items?limit=100", ct: ct);
                if (!items.TryGetProperty("data", out var outputs)) continue;
                foreach (var item in outputs.EnumerateArray())
                {
                    if (item.GetProperty("status").GetString() != "completed")
                        throw new InvalidOperationException($"Native evaluator output did not complete successfully: {item}");
                    records.Add(JsonSerializer.SerializeToElement(new
                    {
                        agent = name,
                        run_id = runId,
                        report_url = run.TryGetProperty("report_url", out var report) ? report.GetString() : null,
                        item = item.Clone()
                    }));
                }
            }
        }
        return records;
    }

    static JsonElement? MatchEvaluation(JsonElement record, IEnumerable<JsonElement> evaluated) =>
        ProductionMonitoring.MonitoringCore.MatchEvaluation(record, evaluated);

    static async Task<List<JsonElement>> WaitForEvaluationsAsync(List<JsonElement> records, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var evaluated = await EvaluationResultsAsync(ct);
            var matches = records.Select(record => MatchEvaluation(record, evaluated)).ToArray();
            if (matches.All(x => x.HasValue)) return matches.Select(x => x!.Value).ToList();
            if (deadline.Elapsed >= TimeSpan.FromMinutes(20))
            {
                Save("evaluation-diagnostics.json", new { records, evaluated });
                throw new TimeoutException("Native live-traffic evaluations did not complete for every response.");
            }
            Console.WriteLine($"Native evaluation evidence: {matches.Count(x => x.HasValue)}/{matches.Length}");
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    public static async Task<JsonElement> TelemetryAsync(IReadOnlyList<string>? responseIds, CancellationToken ct)
    {
        var selected = "";
        if (responseIds is not null)
        {
            ProductionMonitoring.MonitoringCore.ValidateResponseIds(responseIds);
            selected = "| where tostring(Properties['gen_ai.response.id']) in (" +
                string.Join(", ", responseIds.Select(id => JsonSerializer.Serialize(id))) + ")\n";
        }
        var workspace = Output("workspaceId");
        var query = """
            union isfuzzy=true AppRequests, AppDependencies, AppTraces
            | where TimeGenerated > ago(2h)
            | where tostring(Properties) contains "workshop-pricer" or Name contains "workshop-pricer"
            """ + "\n" + selected + """
            | project TimeGenerated, OperationId, Name=column_ifexists("Name", ""),
                      Properties, DurationMs=column_ifexists("DurationMs", real(null))
            | take 100
            """;
        var token = await Credential.GetTokenAsync(new Azure.Core.TokenRequestContext(["https://api.loganalytics.io/.default"]), ct);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await client.PostAsync($"https://api.loganalytics.azure.com/v1/workspaces/{workspace}/query",
            new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException($"Telemetry query returned an error: {error}");
        return document.RootElement.Clone();
    }

    static async Task<JsonElement> LiveTestAsync(CancellationToken ct)
    {
        var report = new Dictionary<string, object?> { ["started_at"] = DateTimeOffset.UtcNow, ["passed"] = false, ["requests"] = new List<JsonElement>() };
        var requests = (List<JsonElement>)report["requests"]!;
        try
        {
            foreach (var mode in new[] { "baseline", "regression" })
            {
                var phase = await TrafficAsync(mode, ct);
                requests.AddRange(phase);
                Save("live-report.json", report);
                report[mode + "_evaluations"] = await WaitForEvaluationsAsync(phase, ct);
            }
        }
        finally
        {
            await SetModeAsync("recovery", ct);
            Save("live-report.json", report);
        }
        var recovery = await TrafficAsync("recovery", ct);
        requests.AddRange(recovery);
        try
        {
            report["recovery_evaluations"] = await WaitForEvaluationsAsync(recovery, ct);
            var responseIds = requests.Select(x => x.GetProperty("response_id").GetString()!).ToArray();
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                var traces = await TelemetryAsync(responseIds, ct);
                var rows = traces.GetProperty("tables")[0].GetProperty("rows").EnumerateArray().ToArray();
                if (responseIds.All(id => rows.Any(row => row.ToString().Contains(id, StringComparison.Ordinal))))
                {
                    report["telemetry"] = traces;
                    break;
                }
                if (deadline.Elapsed >= TimeSpan.FromMinutes(15)) throw new TimeoutException("Both deployed agents must have ingested server-side traces.");
                Console.WriteLine("Waiting for both agents' actual Azure Monitor traces...");
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
            report["passed"] = true;
            return JsonSerializer.SerializeToElement(report);
        }
        finally { Save("live-report.json", report); }
    }

    public static async Task RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file demo.cs -- [--setup | --live-test]");
            return;
        }
        var cancellation = CancellationToken.None;
        JsonElement result;
        if (args.Contains("--setup")) result = await SetupAsync(cancellation);
        else
        {
            if (!File.Exists(PathFor("agents.json"))) await SetupAsync(cancellation);
            result = await LiveTestAsync(cancellation);
        }
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
