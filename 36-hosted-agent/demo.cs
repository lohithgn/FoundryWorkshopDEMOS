#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:package Azure.Identity@1.21.0
#:include HostedCore.cs

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Identity;

await HostedDemo.RunAsync(args);

static class HostedDemo
{
    static readonly string State = Path.Combine(Environment.CurrentDirectory, ".azure");
    const string Name = "hosted-workshop-pricer";

    static JsonElement ReadOutputs()
    {
        var path = Path.Combine(State, "deployment.json");
        if (!File.Exists(path)) throw new InvalidOperationException("Deploy this lab first with deploy_lab.cs.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var outputs = document.RootElement.TryGetProperty("outputs", out var nested) ? nested : document.RootElement;
        return outputs.Clone();
    }

    static void Save(string name, object data)
    {
        Directory.CreateDirectory(State);
        var path = Path.Combine(State, name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temporary, path, true);
    }

    static string Output(JsonElement outputs, string name)
    {
        var value = outputs.GetProperty(name);
        return value.ValueKind == JsonValueKind.Object ? value.GetProperty("value").GetString()! : value.GetString()!;
    }

    static async Task<string> TokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        var credential = new AzureCliCredential();
        return (await credential.GetTokenAsync(new Azure.Core.TokenRequestContext([scope]), cancellationToken)).Token;
    }

    static async Task<JsonElement> InvokeAsync(int attendees, string session, CancellationToken cancellationToken = default)
    {
        if (attendees is < 1 or > 20) throw new ArgumentException("attendees must be an integer from 1 to 20");
        HostedAgent.HostedCore.ValidateSession(session);
        var endpoint = Output(ReadOutputs(), "projectEndpoint").TrimEnd('/');
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(240) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync("https://ai.azure.com/.default", cancellationToken));
        using var body = new StringContent(JsonSerializer.Serialize(new { attendees }), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"{endpoint}/agents/{Name}/endpoint/protocols/invocations?api-version=v1&agent_session_id={Uri.EscapeDataString(session)}", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var result = document.RootElement.Clone();
        var quote = result.GetProperty("quote");
        if (quote.GetProperty("attendees").GetInt32() != attendees
            || quote.GetProperty("currency").GetString() != "USD"
            || quote.GetProperty("unit_price").GetInt32() != 25
            || quote.GetProperty("total").GetInt32() != attendees * 25
            || quote.GetProperty("source").GetString() != "hosted-read-only-price-tool"
            || !result.TryGetProperty("response_id", out _)
            || !result.TryGetProperty("tool_call_id", out _))
            throw new InvalidOperationException($"Hosted response failed independent validation: {result}");
        return result;
    }

    static async Task<JsonElement> QueryTracesAsync(JsonElement outputs, IReadOnlyList<string> traces, CancellationToken ct)
    {
        if (traces.Count == 0 || traces.Any(id => !Regex.IsMatch(id, @"\A[a-f0-9]{32}\z")))
            throw new ArgumentException("Expected actual 32-character hexadecimal trace IDs");
        var ids = string.Join(", ", traces.Select(id => JsonSerializer.Serialize(id)));
        var query = $"union isfuzzy=true AppRequests, AppDependencies, AppTraces\n| where TimeGenerated > ago(2h)\n| where OperationId in ({ids})\n| project TimeGenerated, OperationId, Name=column_ifexists(\"Name\", \"\"), Properties, DurationMs=column_ifexists(\"DurationMs\", real(null))\n| take 200";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync("https://api.loganalytics.io/.default", ct));
        using var response = await client.PostAsJsonAsync($"https://api.loganalytics.azure.com/v1/workspaces/{Output(outputs, "workspaceId")}/query", new { query }, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"Telemetry query returned an error: {error}");
        return document.RootElement.Clone();
    }

    static async Task<JsonElement> LiveTestAsync(CancellationToken ct)
    {
        var outputs = ReadOutputs();
        var session = "live-" + Guid.NewGuid().ToString("N");
        var report = new Dictionary<string, object?> { ["started_at"] = DateTimeOffset.UtcNow, ["passed"] = false };
        try
        {
            var first = await InvokeAsync(3, session, ct);
            var second = await InvokeAsync(4, session, ct);
            var separate = await InvokeAsync(2, "live-" + Guid.NewGuid().ToString("N"), ct);
            if (first.GetProperty("state").GetProperty("visits").GetInt32() != 1
                || second.GetProperty("state").GetProperty("visits").GetInt32() != 2
                || first.GetProperty("state").GetProperty("marker").GetString() != second.GetProperty("state").GetProperty("marker").GetString()
                || separate.GetProperty("state").GetProperty("visits").GetInt32() != 1
                || separate.GetProperty("state").GetProperty("marker").GetString() == first.GetProperty("state").GetProperty("marker").GetString())
                throw new InvalidOperationException("Hosted session persistence/isolation gate failed.");
            var invocations = new[] { first, second, separate };
            report["invocations"] = invocations;
            var ids = invocations.Select(x => x.GetProperty("trace_id").GetString()!).ToArray();
            var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
            while (true)
            {
                var traces = await QueryTracesAsync(outputs, ids, ct);
                var rows = traces.TryGetProperty("tables", out var tables) && tables.GetArrayLength() > 0
                    ? tables[0].GetProperty("rows").EnumerateArray().ToArray()
                    : [];
                if (ids.All(id => rows.Any(row => row[1].GetString() == id && row[2].GetString() is { Length: > 0 })))
                {
                    report["telemetry"] = traces;
                    report["passed"] = true;
                    break;
                }
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("No hosted-agent telemetry ingested within 15 minutes.");
                Console.WriteLine("Waiting for real hosted-agent trace ingestion...");
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
            return JsonSerializer.SerializeToElement(report);
        }
        finally { Save("live-report.json", report); }
    }

    static async Task PublishAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        throw new NotSupportedException("Publishing the C# runtime is blocked because runtime/Dockerfile remains Python-only. No image was built or cloud resource changed. See README for the required minimal packaging exception.");
    }

    public static async Task RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file demo.cs -- [--publish | --live-test]");
            return;
        }
        if (args.Contains("--publish")) { await PublishAsync(CancellationToken.None); return; }
        Console.WriteLine(JsonSerializer.Serialize(await LiveTestAsync(CancellationToken.None), new JsonSerializerOptions { WriteIndented = true }));
    }

}
