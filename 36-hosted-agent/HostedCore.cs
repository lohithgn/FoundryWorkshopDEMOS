using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HostedAgent;

public static class HostedCore
{
    public const int UnitPrice = 25;

    public static int ValidateAttendees(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("attendees", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var attendees)
            || attendees is < 1 or > 20)
            throw new ArgumentException("attendees must be an integer from 1 to 20");
        return attendees;
    }

    public static async Task<JsonDocument> ReadBoundedJsonAsync(
        Stream body, long? contentLength, CancellationToken cancellationToken, bool allowUndeclaredContentLength = false)
    {
        if (contentLength is null)
        {
            if (!allowUndeclaredContentLength)
                throw new ArgumentException("Request body must be 1-4096 bytes");
            var bounded = new byte[4097];
            var total = 0;
            while (total < bounded.Length)
            {
                var read = await body.ReadAsync(bounded.AsMemory(total), cancellationToken);
                if (read == 0) break;
                total += read;
            }
            if (total == 0) throw new ArgumentException("Request body must be 1-4096 bytes");
            if (total > 4096) throw new ArgumentException("Request body must be 1-4096 bytes");
            return JsonDocument.Parse(bounded.AsMemory(0, total));
        }
        if (contentLength is < 1 or > 4096)
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

    public static void ValidateSession(string? sessionId)
    {
        if (sessionId is null || !Regex.IsMatch(sessionId, @"\A[a-zA-Z0-9_-]{1,80}\z"))
            throw new ArgumentException("Invalid session ID");
    }

    public static object Quote(int attendees) => new
    {
        attendees,
        currency = "USD",
        unit_price = UnitPrice,
        total = attendees * UnitPrice,
        source = "hosted-read-only-price-tool"
    };

    public static void ValidateToolArguments(JsonElement arguments, int attendees)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || arguments.EnumerateObject().Count() != 1
            || !arguments.TryGetProperty("attendees", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var proposed)
            || proposed != attendees)
            throw new InvalidOperationException("The model proposed an unexpected tool or changed the attendee count.");
    }

    public static void ValidateModelQuote(JsonElement quote, int attendees)
    {
        var properties = quote.ValueKind == JsonValueKind.Object
            ? quote.EnumerateObject().ToArray()
            : [];
        var expectedNames = new HashSet<string>(["attendees", "currency", "unit_price", "total", "source"], StringComparer.Ordinal);
        if (properties.Length != expectedNames.Count || !expectedNames.SetEquals(properties.Select(property => property.Name))
            || quote.GetProperty("attendees").ValueKind != JsonValueKind.Number
            || !quote.GetProperty("attendees").TryGetInt32(out var actualAttendees)
            || actualAttendees != attendees
            || quote.GetProperty("currency").ValueKind != JsonValueKind.String
            || quote.GetProperty("currency").GetString() != "USD"
            || quote.GetProperty("unit_price").ValueKind != JsonValueKind.Number
            || !quote.GetProperty("unit_price").TryGetInt32(out var unitPrice)
            || unitPrice != UnitPrice
            || quote.GetProperty("total").ValueKind != JsonValueKind.Number
            || !quote.GetProperty("total").TryGetInt32(out var total)
            || total != attendees * UnitPrice
            || quote.GetProperty("source").ValueKind != JsonValueKind.String
            || quote.GetProperty("source").GetString() != "hosted-read-only-price-tool")
            throw new InvalidOperationException("Model answer failed the independent pricing check.");
    }

    public static Dictionary<string, object> AdvanceState(string directory, string userId)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId))) + ".json");
        var (visits, marker) = (0, Guid.NewGuid().ToString("N"));
        if (File.Exists(path))
        {
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            visits = saved.RootElement.GetProperty("visits").GetInt32();
            marker = saved.RootElement.GetProperty("marker").GetString()!;
        }
        var state = new Dictionary<string, object> { ["visits"] = visits + 1, ["marker"] = marker };
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, path, true);
        return state;
    }
}
