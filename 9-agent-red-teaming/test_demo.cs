#:property TargetFramework=net10.0
#:include demo.cs
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

var env = new Dictionary<string, string?>
{
    ["FOUNDRY_PROJECT_ENDPOINT"] = "https://account.services.ai.azure.com/api/projects/redteam-demo",
    ["AZURE_OPENAI_ENDPOINT"] = "https://account.openai.azure.com/",
};
var options = AgentRedTeamingDemo.Parse([], env);
Check(options.Deployment == "gpt-4.1-mini" && options.Output.StartsWith(".azure/redteam-", StringComparison.Ordinal),
    "CLI defaults changed.");
foreach (var invalidArgs in new[]
{
    new[] { "--endpoint", "http://insecure.example" },
    new[] { "--deployment", " " },
    new[] { "--unknown", "value" },
    new[] { "--output", "" }
})
{
    var failed = false;
    try { AgentRedTeamingDemo.Parse(invalidArgs, env); }
    catch (ArgumentException) { failed = true; }
    Check(failed, "An invalid demo argument was accepted.");
}
var missingEndpoints = false;
try { AgentRedTeamingDemo.Parse([], new Dictionary<string, string?>()); }
catch (ArgumentException) { missingEndpoints = true; }
Check(missingEndpoints, "Missing endpoint environment variables were accepted.");
var missingValue = false;
try { AgentRedTeamingDemo.Parse(["--deployment"], env); }
catch (ArgumentException) { missingValue = true; }
Check(missingValue, "A missing CLI option value was accepted.");

using var objectivesPayload = JsonDocument.Parse("""
[
  {"id":"generated-1","messages":[{"role":"user","content":"Synthetic objective supplied by the offline service stub."}]}
]
""");
var objectives = AgentRedTeamingDemo.ParseObjectives(objectivesPayload.RootElement);
Check(objectives.Count == 1 && objectives[0].Id == "generated-1" &&
      objectives[0].Query.Contains("Synthetic objective", StringComparison.Ordinal),
    "Foundry-generated objective parsing changed.");
foreach (var malformed in new[] { "{}", """{"objectives":[]}""", """[{"messages":[{"content":""}]}]""" })
{
    var failed = false;
    try { AgentRedTeamingDemo.ParseObjectives(JsonDocument.Parse(malformed).RootElement); }
    catch (InvalidOperationException) { failed = true; }
    Check(failed, "Missing or empty objectives were accepted.");
}

var evaluationPayload = AgentRedTeamingDemo.BuildEvaluationPayload("sample objective", "sample tutor answer");
Check(evaluationPayload["testing_criteria"]![0]!["evaluator_name"]!.GetValue<string>() == "builtin.hate_unfairness" &&
      evaluationPayload["data_source"]!["source"]!["content"]!["item"]!["response"]!.GetValue<string>() == "sample tutor answer",
    "Risk evaluator or target response mapping changed.");

var infra = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "infra", "main.bicep"));
Check(infra.Contains("dependsOn: [model]", StringComparison.Ordinal) &&
      infra.Contains("resource model ", StringComparison.Ordinal) &&
      infra.Contains("resource project ", StringComparison.Ordinal),
    "Foundry project no longer waits for model deployment.");

var calls = new List<(string Method, string Path, string Query, string? Body, string? Token)>();
var replies = new Queue<(HttpStatusCode Status, string Body)>([
    (HttpStatusCode.OK, """[{"id":"generated-1","messages":[{"role":"user","content":"Synthetic objective."}]}]"""),
    (HttpStatusCode.OK, """{"choices":[{"finish_reason":"stop","message":{"content":"A safe answer."}}]}"""),
    (HttpStatusCode.OK, """{"results":[{"hate_unfairness":{"label":2,"reasoning":"low risk"}}]}""")
]);
using var http = new HttpClient(new MockHandler(request =>
{
    calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult(), request.Headers.Authorization?.Parameter));
    var reply = replies.Dequeue();
    return Task.FromResult(new HttpResponseMessage(reply.Status)
    {
        Content = new StringContent(reply.Body, Encoding.UTF8, "application/json")
    });
}));
var output = Path.Combine(".azure", $"test-redteam-{Guid.NewGuid():N}");
try
{
    using var report = await AgentRedTeamingDemo.ScanAsync(http,
        "https://account.services.ai.azure.com/api/projects/redteam-demo",
        "https://account.openai.azure.com", "tutor", output, "project-token", "inference-token");
    Check(calls.Count == 3, "Scan must fetch an objective, call the target, and evaluate the response.");
    Check(calls[0].Method == "GET" && calls[0].Path.EndsWith("/redTeams/simulation/attackobjectives", StringComparison.Ordinal) &&
          calls[0].Query.Contains("riskTypes=hate_unfairness", StringComparison.Ordinal) &&
          calls[0].Query.Contains("targetType=model", StringComparison.Ordinal) && calls[0].Token == "project-token",
        "Managed objective-generation route or project authentication changed.");
    Check(calls[1].Path == "/openai/v1/chat/completions" && calls[1].Token == "inference-token" &&
          calls[1].Body!.Contains(AgentRedTeamingDemo.Instructions, StringComparison.Ordinal),
        "Tutor call did not use safety instructions and inference authentication.");
    using var chatRequest = JsonDocument.Parse(calls[1].Body!);
    Check(chatRequest.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() == "Synthetic objective.",
        "The generated objective was not passed to the target.");
    Check(calls[2].Method == "POST" && calls[2].Path.EndsWith("/sync_evals:run", StringComparison.Ordinal) &&
          calls[2].Token == "project-token" && calls[2].Body!.Contains("A safe answer.", StringComparison.Ordinal),
        "The actual target response was not sent to the Foundry evaluator.");
    var reportPath = Path.Combine(output, "report.json");
    Check(File.Exists(reportPath), "Scan report was not written.");
    var saved = JsonNode.Parse(File.ReadAllText(reportPath))!;
    Check(saved["scorecard"]![AgentRedTeamingDemo.Category]!["score"]!.GetValue<double>() == 2 &&
          saved["AOAI_Compatible_Summary"]!["result_counts"]!["passed"]!.GetValue<int>() == 1,
        "Safe evaluation did not produce the expected completed scorecard.");
}
finally
{
    if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
}

var unsafeScore = JsonDocument.Parse("""{"results":[{"hate_unfairness":{"label":4}}]}""");
Check(AgentRedTeamingDemo.ExtractRiskScore(unsafeScore.RootElement) == 4,
    "A numeric risk score was not extracted.");
var namedScore = JsonDocument.Parse("""{"results":[{"name":"hate_unfairness","result":{"label":"3"}}]}""");
Check(AgentRedTeamingDemo.ExtractRiskScore(namedScore.RootElement) == 3,
    "A named string risk score was not extracted.");
var missingScore = false;
try { AgentRedTeamingDemo.ExtractRiskScore(JsonDocument.Parse("""{"status":"completed"}""").RootElement); }
catch (InvalidOperationException) { missingScore = true; }
Check(missingScore, "An evaluation with no risk score was accepted.");

var unsafeReplies = new Queue<string>([
    """[{"id":"generated-2","messages":[{"role":"user","content":"Another synthetic objective."}]}]""",
    """{"choices":[{"finish_reason":"stop","message":{"content":"A sample answer."}}]}""",
    """{"results":[{"hate_unfairness":{"label":4}}]}"""
]);
using (var unsafeHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
       { Content = new StringContent(unsafeReplies.Dequeue(), Encoding.UTF8, "application/json") }))))
{
    var unsafeOutput = Path.Combine(".azure", $"test-redteam-unsafe-{Guid.NewGuid():N}");
    try
    {
        using var unsafeReport = await AgentRedTeamingDemo.ScanAsync(unsafeHttp, "https://project.example",
            "https://account.example", "tutor", unsafeOutput, "project-token", "inference-token");
        var savedUnsafe = JsonNode.Parse(File.ReadAllText(Path.Combine(unsafeOutput, "report.json")))!;
        Check(savedUnsafe["scorecard"]![AgentRedTeamingDemo.Category]!["attack_succeeded"]!.GetValue<bool>() &&
              savedUnsafe["AOAI_Compatible_Summary"]!["result_counts"]!["failed"]!.GetValue<int>() == 1,
            "An above-threshold risk score was not counted as a failed safety test.");
    }
    finally
    {
        if (Directory.Exists(unsafeOutput)) Directory.Delete(unsafeOutput, recursive: true);
    }
}

var incompleteTarget = false;
using (var incompleteHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
       { Content = new StringContent("""{"choices":[{"finish_reason":"length","message":{"content":"partial"}}]}""") }))))
{
    try
    {
        await AgentRedTeamingDemo.GetTutorAnswerAsync(incompleteHttp, "https://account.example", "tutor",
            "synthetic objective", "token");
    }
    catch (InvalidOperationException error) { incompleteTarget = error.Message.Contains("complete answer", StringComparison.Ordinal); }
}
Check(incompleteTarget, "An incomplete tutor response was accepted.");

var existingPath = Path.Combine(".azure", $"test-redteam-existing-{Guid.NewGuid():N}");
Directory.CreateDirectory(existingPath);
try
{
    var rejected = false;
    try
    {
        await AgentRedTeamingDemo.ScanAsync(http, "https://project.example", "https://account.example",
            "tutor", existingPath, "project-token", "inference-token");
    }
    catch (IOException) { rejected = true; }
    Check(rejected, "Existing report output was overwritten.");
}
finally { Directory.Delete(existingPath, recursive: true); }

var failedHttp = false;
using (var forbiddenHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
       { Content = new StringContent("denied") }))))
{
    try
    {
        await AgentRedTeamingDemo.SendAsync(forbiddenHttp, HttpMethod.Get, "https://project.example/test", "token");
    }
    catch (HttpRequestException error) { failedHttp = error.Message.Contains("403") && error.Message.Contains("denied"); }
}
Check(failedHttp, "Foundry service errors were hidden.");
Console.WriteLine("PASS: agent-red-teaming offline regression checks");

sealed class MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request);
}
