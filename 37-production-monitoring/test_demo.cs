#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:include MonitoringCore.cs

using System.Text.Json;
using System.Text;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"FAILED: {name}");
    Console.WriteLine($"PASS {name}");
    passed++;
}
JsonElement Parse(string value) => JsonDocument.Parse(value).RootElement.Clone();
var record = Parse("""{"agent":"agent-a","response_id":"resp_123","expected_pass":true,"configured_version":"1"}""");
JsonElement Eval(bool success = true, string id = "resp_123", string agent = "agent-a", string version = "1", string status = "completed", bool stringPassed = false) =>
    Parse(JsonSerializer.Serialize(new
    {
        agent,
        run_id = "run",
        item = new
        {
            datasource_item = new { response_id = id, agent_name = agent, agent_version = version },
            results = new[] { new { name = "price_correct", passed = stringPassed ? "true" : (object)success, status } }
        }
    }));
Check(ProductionMonitoring.MonitoringCore.MatchEvaluation(record, []) is null, "pending is not pass");
var requestBytes = Encoding.UTF8.GetBytes("""{"mode":"baseline"}""");
using (var requestStream = new MemoryStream([.. requestBytes, (byte)'x']))
using (var requestJson = await ProductionMonitoring.MonitoringCore.ReadBoundedJsonAsync(requestStream, requestBytes.Length, CancellationToken.None))
{
    ProductionMonitoring.MonitoringCore.ValidateObjectRoot(requestJson.RootElement);
    Check(requestJson.RootElement.GetProperty("mode").GetString() == "baseline", "bounded traffic body parsed");
    Check(requestStream.Position == requestBytes.Length, "traffic reads only declared bytes");
}
foreach (var invalidLength in new long?[] { null, 0, 4097 })
{
    using var empty = new MemoryStream();
    try { await ProductionMonitoring.MonitoringCore.ReadBoundedJsonAsync(empty, invalidLength, CancellationToken.None); throw new Exception("accepted invalid content length"); }
    catch (ArgumentException) { Check(empty.Position == 0, "missing or invalid traffic length rejected before reading"); }
}
using (var truncated = new MemoryStream(Encoding.UTF8.GetBytes("{\"mode\":")))
{
    try { await ProductionMonitoring.MonitoringCore.ReadBoundedJsonAsync(truncated, 16, CancellationToken.None); throw new Exception("accepted truncated request"); }
    catch (ArgumentException) { Check(true, "truncated traffic body rejected"); }
}
foreach (var invalidRoot in new[] { "null", "3", "\"string\"", "[]" })
{
    try { ProductionMonitoring.MonitoringCore.ValidateObjectRoot(Parse(invalidRoot)); throw new Exception("accepted non-object JSON"); }
    catch (ArgumentException) { Check(true, "non-object JSON rejected"); }
}
ProductionMonitoring.MonitoringCore.ValidateObjectRoot(Parse("{}"));
var serviceError = ProductionMonitoring.MonitoringCore.ServiceErrorResponse(new InvalidOperationException("cloud invocation failed"));
Check(serviceError.StatusCode == 502 && JsonDocument.Parse(serviceError.Body).RootElement.GetProperty("error").GetString() == "cloud invocation failed", "service failures map to JSON 502");
Check(ProductionMonitoring.MonitoringCore.MatchEvaluation(record, [Eval()]) is not null, "completed cloud pass correlated");
Check(ProductionMonitoring.MonitoringCore.MatchEvaluation(record, [Eval(id: "resp_other")]) is null, "wrong response does not count");
Check(ProductionMonitoring.MonitoringCore.MatchEvaluation(record, [Eval(agent: "agent-b")]) is null, "wrong agent does not count");
foreach (var value in new[] { "baseline", "regression", "recovery" }) ProductionMonitoring.MonitoringCore.ValidateMode(value);
foreach (var value in new[] { "unknown", "", "Baseline" })
{
    try { ProductionMonitoring.MonitoringCore.ValidateMode(value); throw new Exception("accepted mode " + value); }
    catch (ArgumentException) { Check(true, "reject invalid mode"); }
}
var regression = Parse("""{"agent":"agent-a","response_id":"resp_123","expected_pass":false,"configured_version":"1"}""");
Check(ProductionMonitoring.MonitoringCore.MatchEvaluation(regression, [Eval(false)]) is not null, "expected regression must fail native grader");
foreach (var value in new[] { "bad", "resp_has space" })
    Check(!ProductionMonitoring.MonitoringCore.IsValidResponseId(value), "reject malformed response ID");
Check(ProductionMonitoring.MonitoringCore.IsValidResponseId("resp_"), "response ID validator matches existing prefix/alphanumeric contract");
ProductionMonitoring.MonitoringCore.ValidateResponseIds(["resp_abc123"]);
try { ProductionMonitoring.MonitoringCore.ValidateResponseIds([]); throw new Exception("accepted empty response IDs"); }
catch (ArgumentException) { Check(true, "reject empty telemetry response IDs"); }
Check(ProductionMonitoring.MonitoringCore.IsLoopbackRequest("127.0.0.1:8037", null, 8037), "loopback host allowed");
Check(!ProductionMonitoring.MonitoringCore.IsLoopbackRequest("127.0.0.1:8037", "https://evil.example", 8037), "cross-origin rejected");
Check(!ProductionMonitoring.MonitoringCore.IsLoopbackRequest("0.0.0.0:8037", null, 8037), "non-loopback host rejected");
Console.WriteLine($"{passed} offline checks passed.");
