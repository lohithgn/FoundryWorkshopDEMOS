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

Check(FoundryIqDemo.ApiVersion == "2026-08-01-preview" && FoundryIqDemo.Documents.Count == 3,
    "Foundry IQ API version or synthetic source count changed.");
var calls = new List<(string Method, string Path, string Query, string? Body, string? Auth)>();
using var http = new HttpClient(new MockHandler(request =>
{
    var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
    calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
        body, request.Headers.Authorization?.Scheme));
    var response = request.RequestUri.AbsolutePath.EndsWith("/docs/index", StringComparison.Ordinal)
        ? """{"value":[{"status":true},{"status":true},{"status":true}]}"""
        : request.RequestUri.AbsolutePath.EndsWith("/$count", StringComparison.Ordinal) ? "3" : "{}";
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(response, Encoding.UTF8, "application/json")
    });
}));
await FoundryIqDemo.ConfigureAsync(http, "https://search.example", "https://model.example", "chat",
    "search-token", _ => Task.CompletedTask);
Check(calls.Count == 5, "Index setup, document upload, visibility check, source, and knowledge base calls changed.");
Check(calls.All(call => call.Query == "?api-version=2026-08-01-preview" && call.Auth == "Bearer"),
    "Search API version or Entra bearer authentication changed.");
var schema = JsonNode.Parse(calls[0].Body!)!;
Check(schema["semantic"]?["defaultConfiguration"]?.GetValue<string>() == "workshop-semantic",
    "Semantic Search configuration changed.");
var uploaded = JsonNode.Parse(calls[1].Body!)!["value"]!.AsArray();
Check(uploaded.Count == 3 && uploaded.All(item => item?["@search.action"]?.GetValue<string>() == "mergeOrUpload"),
    "Search indexing actions changed.");
var source = JsonNode.Parse(calls[3].Body!)!;
var knowledge = JsonNode.Parse(calls[4].Body!)!;
Check(source["kind"]?.GetValue<string>() == "searchIndex" &&
      source["searchIndexParameters"]?["searchIndexName"]?.GetValue<string>() == FoundryIqDemo.Index,
    "Knowledge source boundary changed.");
Check(knowledge["outputMode"]?.GetValue<string>() == "answerSynthesis" &&
      knowledge["models"]?[0]?["azureOpenAIParameters"]?["deploymentId"]?.GetValue<string>() == "chat",
    "Knowledge base synthesis or deployment mapping changed.");

var retrievalBody = "";
using var retrievalHttp = new HttpClient(new MockHandler(request =>
{
    retrievalBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
        {"response":[{"content":[{"type":"text","text":"Friday at 10:00. [ref_id:0]"}]}],"activity":[{"type":"searchIndex","knowledgeSourceName":"workshop-source"}],"references":[{"id":"0","docKey":"schedule"}]}
        """, Encoding.UTF8, "application/json")
    });
}));
using var result = await FoundryIqDemo.RetrieveAsync(retrievalHttp, "https://search.example",
    "When is the workshop?", "token");
var retrieval = JsonNode.Parse(retrievalBody)!;
Check(retrieval["knowledgeSourceParams"]?[0]?["alwaysQuerySource"]?.GetValue<bool>() == true &&
      retrieval["includeActivity"]?.GetValue<bool>() == true &&
      retrieval["retrievalReasoningEffort"]?["kind"]?.GetValue<string>() == "low",
    "Retrieval request did not ask for live source queries and evidence.");
Check(result.RootElement.GetProperty("references").GetArrayLength() == 1, "Retrieved references were lost.");

var failedIndex = false;
using (var badIndex = new HttpClient(new MockHandler(request =>
{
    var response = request.RequestUri!.AbsolutePath.EndsWith("/docs/index", StringComparison.Ordinal)
        ? """{"value":[{"status":false,"errorMessage":"bad field"}]}""" : "{}";
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
})))
{
    try { await FoundryIqDemo.ConfigureAsync(badIndex, "https://search.example", "https://model.example", "chat", "token"); }
    catch (InvalidOperationException error) { failedIndex = error.Message.Contains("indexing failed"); }
}
Check(failedIndex, "Failed document indexing was accepted.");

var timedOut = false;
var nowCalls = 0;
using (var lateIndex = new HttpClient(new MockHandler(request =>
{
    var content = request.RequestUri!.AbsolutePath.EndsWith("/docs/index", StringComparison.Ordinal)
        ? """{"value":[{"status":true},{"status":true},{"status":true}]}"""
        : request.RequestUri.AbsolutePath.EndsWith("/$count", StringComparison.Ordinal) ? "0" : "{}";
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
})))
{
    try
    {
        await FoundryIqDemo.ConfigureAsync(lateIndex, "https://search.example", "https://model.example", "chat",
            "token", _ => Task.CompletedTask, () => nowCalls++ == 0 ? DateTimeOffset.UnixEpoch : DateTimeOffset.UnixEpoch.AddSeconds(61));
    }
    catch (TimeoutException) { timedOut = true; }
}
Check(timedOut, "Index visibility timeout was not enforced.");

foreach (var payload in new[]
{
    "{}",
    """{"response":[{"content":[{"type":"text","text":"Answer"}]}],"activity":[{"error":{"message":"failed"}}],"references":[{"id":"0"}]}"""
})
{
    var failedRetrieval = false;
    using var invalidHttp = new HttpClient(new MockHandler(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) })));
    try { using var ignored = await FoundryIqDemo.RetrieveAsync(invalidHttp, "https://search.example", "question", "token"); }
    catch (InvalidOperationException) { failedRetrieval = true; }
    Check(failedRetrieval, "Missing grounding evidence or failed retrieval activity was accepted.");
}
var httpFailure = false;
using (var forbiddenHttp = new HttpClient(new MockHandler(_ =>
    Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied") }))))
{
    try { await FoundryIqDemo.ConfigureAsync(forbiddenHttp, "https://search.example", "https://model.example", "chat", "token"); }
    catch (HttpRequestException error) { httpFailure = error.Message.Contains("403") && error.Message.Contains("denied"); }
}
Check(httpFailure, "Search service errors were hidden.");
var project = "https://account.services.ai.azure.com/api/projects/workshop";
var options = FoundryIqDemo.Parse(["--search-endpoint", "https://search.example", "--endpoint", "https://model.example",
    "--project-endpoint", project]);
Check(options.ProjectEndpoint == project && options.Question == FoundryIqDemo.Question, "Foundry IQ defaults changed.");
var badProject = false;
try { FoundryIqDemo.Parse(["--search-endpoint", "https://search.example", "--endpoint", "https://model.example",
    "--project-endpoint", "https://model.openai.azure.com"]); }
catch (ArgumentException) { badProject = true; }
Check(badProject, "Non-project endpoint was accepted.");
Console.WriteLine("PASS: foundry-iq offline regression checks");

sealed class MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}
