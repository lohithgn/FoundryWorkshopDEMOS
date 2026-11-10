#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:include HostedCore.cs

using System.Text.Json;
using System.Text;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"FAILED: {name}");
    Console.WriteLine($"PASS {name}");
    passed++;
}

Check(JsonSerializer.Serialize(HostedAgent.HostedCore.Quote(3)).Contains("\"total\":75"), "authoritative USD price");
var validBody = Encoding.UTF8.GetBytes("""{"attendees":3}""");
using (var boundedStream = new MemoryStream([.. validBody, (byte)'x']))
using (var boundedJson = await HostedAgent.HostedCore.ReadBoundedJsonAsync(boundedStream, validBody.Length, CancellationToken.None))
{
    Check(HostedAgent.HostedCore.ValidateAttendees(boundedJson.RootElement) == 3, "bounded request body parsed");
    Check(boundedStream.Position == validBody.Length, "only declared request bytes are read");
}
foreach (var invalidLength in new long?[] { null, 0, 4097 })
{
    using var empty = new MemoryStream();
    try { await HostedAgent.HostedCore.ReadBoundedJsonAsync(empty, invalidLength, CancellationToken.None); throw new Exception("accepted invalid content length"); }
    catch (ArgumentException) { Check(empty.Position == 0, "missing or invalid length rejected before reading"); }
}
using (var truncatedStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"attendees\":")))
{
    try { await HostedAgent.HostedCore.ReadBoundedJsonAsync(truncatedStream, 20, CancellationToken.None); throw new Exception("accepted truncated body"); }
    catch (ArgumentException) { Check(true, "truncated request body rejected"); }
}
using (var undeclaredStream = new MemoryStream(validBody))
using (var undeclaredJson = await HostedAgent.HostedCore.ReadBoundedJsonAsync(
    undeclaredStream, null, CancellationToken.None, allowUndeclaredContentLength: true))
    Check(HostedAgent.HostedCore.ValidateAttendees(undeclaredJson.RootElement) == 3, "undeclared-length runtime body accepted within limit");
using (var emptyUndeclared = new MemoryStream())
{
    try { await HostedAgent.HostedCore.ReadBoundedJsonAsync(emptyUndeclared, null, CancellationToken.None, allowUndeclaredContentLength: true); throw new Exception("accepted empty undeclared body"); }
    catch (ArgumentException) { Check(true, "empty undeclared runtime body rejected"); }
}
using (var oversizedUndeclared = new MemoryStream(new byte[8192]))
{
    try { await HostedAgent.HostedCore.ReadBoundedJsonAsync(oversizedUndeclared, null, CancellationToken.None, allowUndeclaredContentLength: true); throw new Exception("accepted oversized undeclared body"); }
    catch (ArgumentException) { Check(oversizedUndeclared.Position == 4097, "oversized undeclared runtime body stops at limit plus one"); }
}
HostedAgent.HostedCore.ValidateToolArguments(JsonDocument.Parse("""{"attendees":3}""").RootElement, 3);
Check(true, "exact tool arguments accepted");
try
{
    HostedAgent.HostedCore.ValidateToolArguments(JsonDocument.Parse("""{"attendees":3,"unexpected":true}""").RootElement, 3);
    throw new Exception("accepted extra tool argument");
}
catch (InvalidOperationException) { Check(true, "extra tool arguments rejected"); }
var validQuote = JsonDocument.Parse("""{"attendees":3,"currency":"USD","unit_price":25,"total":75,"source":"hosted-read-only-price-tool"}""");
HostedAgent.HostedCore.ValidateModelQuote(validQuote.RootElement, 3);
Check(true, "exact five-field model quote accepted");
try
{
    HostedAgent.HostedCore.ValidateModelQuote(JsonDocument.Parse("""{"attendees":3,"currency":"USD","unit_price":25,"total":75,"source":"hosted-read-only-price-tool","extra":"rejected"}""").RootElement, 3);
    throw new Exception("accepted extra quote field");
}
catch (InvalidOperationException) { Check(true, "extra quote fields rejected"); }
foreach (var value in new[] { 1, 20 })
    Check(HostedAgent.HostedCore.ValidateAttendees(JsonDocument.Parse($"{{\"attendees\":{value}}}").RootElement) == value, $"attendees boundary {value}");
foreach (var json in new[] { "{\"attendees\":true}", "{\"attendees\":0}", "{\"attendees\":21}", "{\"attendees\":\"3\"}", "{\"attendees\":3.5}" })
{
    try { HostedAgent.HostedCore.ValidateAttendees(JsonDocument.Parse(json).RootElement); throw new Exception("accepted invalid request " + json); }
    catch (ArgumentException) { Check(true, "reject " + json); }
}
foreach (var valid in new[] { "valid", "x", new string('a', 80) }) HostedAgent.HostedCore.ValidateSession(valid);
foreach (var invalid in new[] { "", "../escape", new string('x', 81) })
{
    try { HostedAgent.HostedCore.ValidateSession(invalid); throw new Exception("accepted invalid session " + invalid); }
    catch (ArgumentException) { Check(true, "reject invalid session"); }
}
var directory = Path.Combine(Environment.CurrentDirectory, ".test-state-" + Guid.NewGuid().ToString("N"));
try
{
    var one = HostedAgent.HostedCore.AdvanceState(directory, "alice");
    var two = HostedAgent.HostedCore.AdvanceState(directory, "alice");
    var other = HostedAgent.HostedCore.AdvanceState(directory, "bob");
    Check(Convert.ToInt32(one["visits"]) == 1 && Convert.ToInt32(two["visits"]) == 2, "same user state persists");
    Check((string)one["marker"] == (string)two["marker"], "same user marker persists");
    Check(Convert.ToInt32(other["visits"]) == 1 && (string)other["marker"] != (string)one["marker"], "different user state isolates");
    HostedAgent.HostedCore.AdvanceState(directory, "../../escape");
    Check(Directory.GetFiles(directory, "*.json").Length == 3, "user ID cannot escape state directory");
}
finally { Directory.Delete(directory, true); }
Console.WriteLine($"{passed} offline checks passed.");
