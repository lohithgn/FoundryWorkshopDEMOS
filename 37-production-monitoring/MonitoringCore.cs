using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProductionMonitoring;

public static class MonitoringCore
{
    public static readonly string[] Names = ["monitored-workshop-pricer", "stable-workshop-pricer"];
    public static readonly string[] Modes = ["baseline", "regression", "recovery"];

    public static void ValidateMode(string? mode)
    {
        if (mode is null || !Modes.Contains(mode, StringComparer.Ordinal))
            throw new ArgumentException("mode must be baseline, regression, or recovery");
    }

    public static async Task<JsonDocument> ReadBoundedJsonAsync(Stream body, long? contentLength, CancellationToken cancellationToken)
    {
        if (contentLength is null or < 1 or > 4096)
            throw new ArgumentException("Request body must be 1-4096 bytes");
        var buffer = new byte[(int)contentLength.Value];
        try
        {
            await body.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            throw new ArgumentException("Request body is truncated");
        }
        return JsonDocument.Parse(buffer);
    }

    public static void ValidateObjectRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Expected a JSON object");
    }

    public static (int StatusCode, string Body) ServiceErrorResponse(Exception error) =>
        (502, JsonSerializer.Serialize(new { error = error.Message }));

    public static bool IsValidResponseId(string? id) =>
        id is not null && id.StartsWith("resp_", StringComparison.Ordinal)
        && id.Replace("_", "", StringComparison.Ordinal).All(char.IsLetterOrDigit);

    public static void ValidateResponseIds(IReadOnlyCollection<string>? ids)
    {
        if (ids is null || ids.Count == 0 || ids.Any(id => !IsValidResponseId(id)))
            throw new ArgumentException("Expected actual cloud response IDs");
    }

    public static JsonElement? MatchEvaluation(JsonElement record, IEnumerable<JsonElement> evaluated)
    {
        var agent = record.GetProperty("agent").GetString();
        var responseId = record.GetProperty("response_id").GetString();
        var version = record.GetProperty("configured_version").ToString();
        var expectedPass = record.GetProperty("expected_pass").GetBoolean();
        foreach (var item in evaluated)
        {
            if (item.GetProperty("agent").GetString() != agent) continue;
            var detail = item.GetProperty("item");
            if (!detail.TryGetProperty("datasource_item", out var source)
                || !source.TryGetProperty("response_id", out var id)
                || id.GetString() != responseId) continue;
            if (source.GetProperty("agent_name").GetString() != agent
                || source.GetProperty("agent_version").ToString() != version)
                throw new InvalidOperationException($"Cloud evaluation served-agent/version mismatch: {source}");
            var checks = detail.GetProperty("results").EnumerateArray()
                .Where(x => x.GetProperty("name").GetString() == "price_correct").ToArray();
            if (checks.Length != 1
                || checks[0].GetProperty("passed").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || checks[0].GetProperty("status").GetString() != "completed")
                throw new InvalidOperationException("Unexpected native grader output shape.");
            if (checks[0].GetProperty("passed").GetBoolean() != expectedPass)
                throw new InvalidOperationException("Native evaluator did not identify the expected quality state.");
            return item.Clone();
        }
        return null;
    }

    public static bool IsLoopbackRequest(string? host, string? origin, int port)
    {
        var hosts = new[] { $"127.0.0.1:{port}", $"localhost:{port}" };
        return hosts.Contains(host, StringComparer.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(origin) || hosts.Any(x => string.Equals(origin, "http://" + x, StringComparison.OrdinalIgnoreCase)));
    }
}
