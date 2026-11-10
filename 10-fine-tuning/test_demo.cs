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

var count = FineTuningDemo.ValidateData(FineTuningDemo.DataPath);
Check(count == 12, "Expected twelve training examples.");
var labels = File.ReadLines(FineTuningDemo.DataPath).Select(line =>
    JsonNode.Parse(line)!["messages"]![2]!["content"]!.GetValue<string>()).ToArray();
Check(labels.GroupBy(label => label).ToDictionary(group => group.Key, group => group.Count())
        .OrderBy(pair => pair.Key).SequenceEqual(new Dictionary<string, int>
        {
            ["ACCOUNT"] = 4, ["BILLING"] = 4, ["TECHNICAL"] = 4
        }.OrderBy(pair => pair.Key)),
    "Training labels are no longer balanced.");

var body = FineTuningDemo.BuildJobPayload(FineTuningDemo.Model, "file-1");
Check(body["model"]?.GetValue<string>() == FineTuningDemo.Model &&
      body["training_file"]?.GetValue<string>() == "file-1" &&
      body["method"]?["type"]?.GetValue<string>() == "supervised" &&
      body["method"]?["supervised"]?["hyperparameters"]?["n_epochs"]?.GetValue<int>() == 1 &&
      body["trainingType"]?.GetValue<string>() == "GlobalStandard",
    "Training request lost model, one-epoch supervised method, or GlobalStandard.");

var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var file = Path.Combine(temp, "invalid.jsonl");
    File.WriteAllText(file, "{}\n" + string.Concat(Enumerable.Repeat("{}\n", 8)));
    var tooSmall = false;
    try { FineTuningDemo.ValidateData(file); }
    catch (ArgumentException error) { tooSmall = error.Message.Contains("10 training"); }
    Check(tooSmall, "A dataset smaller than ten rows was accepted.");
    File.WriteAllText(file, string.Concat(Enumerable.Repeat("{}\n", 10)));
    var malformed = false;
    try { FineTuningDemo.ValidateData(file); }
    catch (ArgumentException error) { malformed = error.Message.Contains("system, user"); }
    Check(malformed, "Rows without three ordered chat messages were accepted.");
}
finally { Directory.Delete(temp, true); }

var calls = new List<(string Method, string Path, string? Body, string? ContentType, string? Auth)>();
var replies = new Queue<(string PathEnd, string Body)>([
    ("files", """{"id":"file-1","status":"uploaded"}"""),
    ("file-1", """{"id":"file-1","status":"processed"}"""),
    ("jobs", """{"id":"job-1","status":"pending"}""")
]);
using var http = new HttpClient(new MockHandler(request =>
{
    var content = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
    calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, content,
        request.Content?.Headers.ContentType?.MediaType, request.Headers.Authorization?.Scheme));
    var response = replies.Dequeue().Body;
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(response, Encoding.UTF8, "application/json")
    });
}));
using var job = await FineTuningDemo.SubmitAsync(http, "https://account.openai.azure.com", FineTuningDemo.Model,
    FineTuningDemo.DataPath, "offline-token", _ => Task.CompletedTask);
Check(job.RootElement.GetProperty("id").GetString() == "job-1", "Training job ID was not returned.");
Check(calls.Count == 3 && calls[0].Method == "POST" && calls[0].Path.EndsWith("/files", StringComparison.Ordinal) &&
      calls[1].Method == "GET" && calls[2].Method == "POST" && calls[2].Path.EndsWith("/fine_tuning/jobs", StringComparison.Ordinal),
    "File upload, processing wait, and training submission sequence changed.");
Check(calls.All(call => call.Auth == "Bearer"), "Foundry request omitted bearer authentication.");
Check(calls[0].ContentType == "multipart/form-data" && calls[0].Body!.Contains("fine-tune"),
    "Training file was not uploaded as fine-tuning input.");
var sentJob = JsonNode.Parse(calls[2].Body!)!;
Check(sentJob["trainingType"]?.GetValue<string>() == "GlobalStandard" &&
      sentJob["training_file"]?.GetValue<string>() == "file-1",
    "The API submission payload changed.");

using var retrievalHttp = new HttpClient(new MockHandler(request =>
{
    Check(request.RequestUri!.AbsolutePath.EndsWith("/fine_tuning/jobs/job-1", StringComparison.Ordinal),
        "Existing-job lookup path changed.");
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"id":"job-1","status":"succeeded","fine_tuned_model":"trained-model-1"}""")
    });
}));
using var fetched = await FineTuningDemo.RetrieveJobAsync(retrievalHttp, "https://account.openai.azure.com", "job-1", "token");
FineTuningDemo.ValidateJob(fetched.RootElement);
var invalidJobs = new[]
{
    """{"id":"job-1","status":"failed","error":{"message":"failed"}}""",
    """{"id":"job-1","status":"cancelled"}""",
    """{"id":"job-1","status":"succeeded","fine_tuned_model":null}"""
};
foreach (var payload in invalidJobs)
{
    var failed = false;
    try { FineTuningDemo.ValidateJob(JsonDocument.Parse(payload).RootElement); }
    catch (InvalidOperationException) { failed = true; }
    Check(failed, "Failed/cancelled or model-less successful job was accepted.");
}
var reportPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "job.json");
try
{
    FineTuningDemo.WriteReport(reportPath, "https://account.openai.azure.com", fetched.RootElement);
    var report = JsonNode.Parse(File.ReadAllText(reportPath))!;
    Check(report["endpoint"]?.GetValue<string>() == "https://account.openai.azure.com" &&
          report["job"]?["fine_tuned_model"]?.GetValue<string>() == "trained-model-1",
        "Job report did not retain the endpoint and trained-model ID.");
}
finally
{
    var folder = Path.GetDirectoryName(reportPath)!;
    if (Directory.Exists(folder)) Directory.Delete(folder, true);
}
Check(FineTuningDemo.Parse(["--endpoint", "https://account.openai.azure.com", "--job-id", "job-1"]).JobId == "job-1",
    "Resume by job ID was not preserved.");
Console.WriteLine("PASS: fine-tuning offline regression checks");

sealed class MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}
