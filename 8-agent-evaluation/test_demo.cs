#:include demo.cs
using System.Net;
using System.Text;
using System.Text.Json;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

Check(AgentEvaluationDemo.Questions.Length == 2 &&
      AgentEvaluationDemo.Questions[0].Contains("Azure platform") &&
      AgentEvaluationDemo.Questions[0].Contains("AI applications and agents") &&
      AgentEvaluationDemo.Questions[1].Contains("supervised model training using labeled data"),
    "Evaluation questions lost product context.");
var rows = new[]
{
    new AgentEvaluationDemo.ResponseRow("Question 1", "Actual answer 1", "response-1"),
    new AgentEvaluationDemo.ResponseRow("Question 2", "Actual answer 2", "response-2")
};
var evalPayload = JsonDocument.Parse(AgentEvaluationDemo.BuildEvaluationPayload("judge", rows)).RootElement;
Check(evalPayload.GetProperty("testing_criteria")[0].GetProperty("evaluator_name").GetString() == "builtin.coherence",
    "Cloud coherence evaluator was not selected.");
Check(evalPayload.GetProperty("testing_criteria")[0].GetProperty("initialization_parameters").GetProperty("model").GetString() == "judge",
    "Judge deployment was not forwarded.");
Check(evalPayload.GetProperty("testing_criteria")[0].GetProperty("data_mapping").GetProperty("query").GetString() == "{{item.query}}",
    "Evaluator query mapping changed.");
Check(evalPayload.GetProperty("data_source_config").GetProperty("item_schema").GetProperty("required").GetArrayLength() == 3,
    "Evaluation schema must require query, answer, and response ID.");
var runPayload = JsonDocument.Parse(AgentEvaluationDemo.BuildRunPayload(rows)).RootElement;
var content = runPayload.GetProperty("data_source").GetProperty("source").GetProperty("content");
Check(content.GetArrayLength() == 2 && content[0].GetProperty("item").GetProperty("response").GetString() == "Actual answer 1",
    "The run did not submit the exact actual answers inline.");
foreach (var invalid in new[]
{
    Array.Empty<AgentEvaluationDemo.ResponseRow>(),
    new[] { rows[0], rows[0] },
    new[] { rows[0] with { Response = "" } },
    new[] { rows[0] with { ResponseId = "" } }
})
{
    var failed = false;
    try { AgentEvaluationDemo.ValidateResponses(invalid); }
    catch (ArgumentException) { failed = true; }
    Check(failed, "Invalid response rows or duplicate IDs were accepted.");
}

var requests = new List<(string Method, string Path, string? Body, string? Authorization)>();
var responseQueue = new Queue<string>([
    """{"id":"eval-1"}""",
    """{"id":"run-1","status":"completed","result_counts":{"total":2,"passed":1,"failed":1,"errored":0}}""",
    """{"data":[{"status":"completed","datasource_item":{"query":"Question 1","response":"Actual answer 1","response_id":"response-1"},"results":[{"name":"coherence","status":"completed","passed":true}]},{"status":"completed","datasource_item":{"query":"Question 2","response":"Actual answer 2","response_id":"response-2"},"results":[{"name":"coherence","status":"completed","passed":false}]}]}"""
]);
using var http = new HttpClient(new MockHandler(request =>
{
    requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath,
        request.Content is null ? null : request.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
        request.Headers.Authorization?.Scheme));
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(responseQueue.Dequeue(), Encoding.UTF8, "application/json")
    });
}));
using var run = await AgentEvaluationDemo.EvaluateAsync(http, "https://project.example/api/projects/workshop", rows,
    "judge", "offline-token", delay: _ => Task.CompletedTask);
Check(requests.Count == 3, "Evaluation did not create its config, submit its run, and read output items.");
Check(requests[0].Path == "/api/projects/workshop/openai/v1/evals" &&
      requests[1].Path == "/api/projects/workshop/openai/v1/evals/eval-1/runs" &&
      requests[2].Path.EndsWith("/output_items", StringComparison.Ordinal),
    "Evaluation API routes changed.");
Check(requests.All(request => request.Authorization == "Bearer"), "Foundry bearer authentication was missing.");
Check(requests[0].Body!.Contains("builtin.coherence") && requests[1].Body!.Contains("Actual answer 1"),
    "Evaluation requests omitted the real evaluator or response text.");

var agentCalls = new List<(string Method, string Path, string? Body)>();
var agentReplies = new Queue<string>([
    """{"name":"workshop-agent","version":"1"}""",
    """{"id":"stored-1","status":"completed","output_text":"A concise answer."}"""
]);
using (var agentHttp = new HttpClient(new MockHandler(request =>
{
    agentCalls.Add((request.Method.Method, request.RequestUri!.AbsolutePath,
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(agentReplies.Dequeue(), Encoding.UTF8, "application/json")
    });
})))
{
    var agent = await AgentEvaluationDemo.CreateAgentAsync(agentHttp, "https://project.example/api/projects/workshop",
        "judge", "token", "workshop-agent");
    Check(agent == new AgentEvaluationDemo.AgentVersion("workshop-agent", "1"), "Agent version metadata changed.");
    var response = await AgentEvaluationDemo.GetAgentAnswerAsync(agentHttp,
        "https://project.example/api/projects/workshop", agent, "Question", "token");
    Check(response == new AgentEvaluationDemo.ResponseRow("Question", "A concise answer.", "stored-1"),
        "Agent-scoped Responses call did not retain the actual response and ID.");
}
Check(agentCalls[0].Method == "POST" && agentCalls[0].Path.EndsWith("/agents/workshop-agent/versions", StringComparison.Ordinal) &&
      agentCalls[0].Body!.Contains("\"kind\":\"prompt\"") && agentCalls[0].Body!.Contains("concise AI workshop tutor"),
    "Prompt agent definition or endpoint changed.");
Check(agentCalls[1].Path.EndsWith("/agents/workshop-agent/endpoint/protocols/openai/responses", StringComparison.Ordinal),
    "Responses request did not use the agent endpoint.");

using var validRun = JsonDocument.Parse("""{"status":"completed","result_counts":{"total":2,"passed":1,"failed":1,"errored":0}}""");
AgentEvaluationDemo.ValidateRun(validRun.RootElement, rows);
var failedRun = false;
try { AgentEvaluationDemo.ValidateRun(JsonDocument.Parse("""{"status":"failed"}""").RootElement, rows); }
catch (InvalidOperationException) { failedRun = true; }
Check(failedRun, "Failed evaluation was accepted.");
var evaluationTimedOut = false;
var timeoutReplies = new Queue<string>([
    """{"id":"eval-1"}""",
    """{"id":"run-1","status":"in_progress"}"""
]);
using (var timeoutHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent(timeoutReplies.Dequeue(), Encoding.UTF8, "application/json")
}))))
{
    try
    {
        using var ignored = await AgentEvaluationDemo.EvaluateAsync(timeoutHttp, "https://project.example", rows,
            "judge", "token", TimeSpan.Zero, _ => Task.CompletedTask);
    }
    catch (TimeoutException) { evaluationTimedOut = true; }
}
Check(evaluationTimedOut, "An evaluation that remained in progress did not time out.");
var serviceErrorObserved = false;
using var errorHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
{
    Content = new StringContent("denied")
})));
try
{
    await AgentEvaluationDemo.SendAsync(errorHttp, HttpMethod.Get, "https://project.example/test", "token");
}
catch (HttpRequestException error) { serviceErrorObserved = error.Message.Contains("403") && error.Message.Contains("denied"); }
Check(serviceErrorObserved, "Foundry service errors were hidden.");
var options = AgentEvaluationDemo.Parse(["--project-endpoint", "https://project.example"]);
Check(options.Deployment == "gpt-4.1-mini", "Default deployment changed.");
var badEndpoint = false;
try { AgentEvaluationDemo.Parse(["--project-endpoint", "http://localhost"]); }
catch (ArgumentException) { badEndpoint = true; }
Check(badEndpoint, "An insecure project endpoint was accepted.");
Console.WriteLine("PASS: agent-evaluation offline regression checks");

sealed class MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}
