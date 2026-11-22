#:include demo.cs
using System.Net;
using System.Text;
using System.Text.Json;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

var prompts = SemanticCacheDemo.Prompts;
Check(prompts[0].Contains("2 + 2") && prompts[1].Contains("two plus two") && prompts[0] == prompts[2], "Cache prompts changed.");
string? capturedPath = null;
string? capturedQuery = null;
string? capturedAuth = null;
string? capturedBody = null;
string? capturedSession = null;
var handler = new MockHandler(async request =>
{
    capturedPath = request.RequestUri!.AbsolutePath;
    capturedQuery = request.RequestUri.Query;
    capturedAuth = request.Headers.Authorization?.Scheme;
    capturedSession = request.Headers.GetValues("x-workshop-cache-session").SingleOrDefault();
    capturedBody = await request.Content!.ReadAsStringAsync();
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"id":"chatcmpl-cached","choices":[{"finish_reason":"stop","message":{"content":"4"}}]}""", Encoding.UTF8, "application/json")
    };
});
using var http = new HttpClient(handler);
var (text, id) = await SemanticCacheDemo.AnswerAsync(http, "https://gateway.example/ai", "chat", prompts[0], "offline-token", session: "workshop");
Check(text == "4" && id == "chatcmpl-cached", "Gateway answer parsing failed.");
Check(capturedPath == "/ai/openai/deployments/chat/chat/completions", "Gateway path changed.");
Check(capturedQuery == "?api-version=2024-10-21", "Gateway API version changed.");
Check(capturedAuth == "Bearer", "Gateway bearer auth missing.");
Check(capturedSession == "workshop", "Cache partition header missing.");
var body = JsonDocument.Parse(capturedBody!).RootElement;
Check(body.GetProperty("messages")[0].GetProperty("content").GetString() == prompts[0], "Gateway prompt body changed.");

var all = new List<string>();
var allHandler = new MockHandler(_ =>
{
    all.Add("call");
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"id":"same","choices":[{"finish_reason":"stop","message":{"content":"4"}}]}""")
    });
});
using var allHttp = new HttpClient(allHandler);
var delayCalls = 0;
var output = new StringWriter();
var original = Console.Out;
Console.SetOut(output);
try
{
    await SemanticCacheDemo.PresentAsync(allHttp, "https://gateway.example/ai", "chat", "workshop", "token", _ =>
    {
        delayCalls++;
        return Task.CompletedTask;
    });
}
finally { Console.SetOut(original); }
Check(all.Count == 3 && delayCalls == 2, "All gateway prompts and visibility delays must be preserved.");
Check(output.ToString().Contains("repeated response IDs: 2"), "Response-ID reuse was not reported.");

foreach (var payload in new[]
{
    """{"id":"x","choices":[]}""",
    """{"id":"x","choices":[{"finish_reason":"length","message":{"content":"partial"}}]}""",
    """{"id":"","choices":[{"finish_reason":"stop","message":{"content":"4"}}]}"""
})
{
    using var badHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) })));
    var failed = false;
    try { await SemanticCacheDemo.AnswerAsync(badHttp, "https://gateway.example/ai", "chat", "prompt", "token"); }
    catch (InvalidOperationException) { failed = true; }
    Check(failed, "Incomplete/unobservable gateway output was accepted.");
}

var badStatus = false;
using (var errorHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied") }))))
{
    try { await SemanticCacheDemo.AnswerAsync(errorHttp, "https://gateway.example/ai", "chat", "prompt", "token"); }
    catch (HttpRequestException error) { badStatus = error.Message.Contains("403") && error.Message.Contains("denied"); }
}
Check(badStatus, "Gateway errors were hidden.");
var noReuse = false;
var counter = 0;
using (var uniqueHttp = new HttpClient(new MockHandler(_ =>
{
    var n = Interlocked.Increment(ref counter);
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"id\":\"id-{n}\",\"choices\":[{{\"finish_reason\":\"stop\",\"message\":{{\"content\":\"4\"}}}}]}}")
    });
})))
{
    var old = Console.Out;
    Console.SetOut(TextWriter.Null);
    try { await SemanticCacheDemo.PresentAsync(uniqueHttp, "https://gateway.example/ai", "chat", "session", "token", _ => Task.CompletedTask); }
    catch (InvalidOperationException error) { noReuse = error.Message.Contains("No response reuse"); }
    finally { Console.SetOut(old); }
}
Check(noReuse, "Identical answer text with distinct IDs was treated as cache reuse.");
var invalidCli = false;
try { SemanticCacheDemo.Parse(["--endpoint", "http://localhost"]); }
catch (ArgumentException) { invalidCli = true; }
Check(invalidCli, "Insecure endpoint was accepted.");
Console.WriteLine("PASS: semantic-cache offline regression checks");

sealed class MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}
