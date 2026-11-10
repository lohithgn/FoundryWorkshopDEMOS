#:property PublishAot=false
#:package Azure.Identity@1.17.0

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

var teams = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["billing"] = "Charges, invoices, and refunds",
    ["technical"] = "Software errors, bugs, and outages",
    ["account"] = "Sign-in, password, and account access"
};
var examples = new (string Ticket, string Expected)[]
{
    ("I was charged twice for my subscription.", "billing"),
    ("The API returns an error on every request.", "technical"),
    ("I cannot sign in after resetting my password.", "account")
};

try { return await MainAsync(args); }
catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine($"ERROR: {error.GetType().Name}: {error.Message}");
    return 2;
}
catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or Azure.RequestFailedException or OperationCanceledException)
{
    Console.Error.WriteLine($"ERROR: {error.GetType().Name}: {error.Message}");
    return 1;
}

static async Task<int> MainAsync(string[] arguments)
{
    var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
    var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "decision";
    string? ticket = null;
    for (var i = 0; i < arguments.Length; i++)
    {
        string Value()
        {
            if (++i >= arguments.Length) throw new ArgumentException($"Missing value for {arguments[i - 1]}.");
            return arguments[i];
        }
        switch (arguments[i])
        {
            case "--help": case "-h": Usage(); return 0;
            case "--endpoint": endpoint = Value(); break;
            case "--deployment": deployment = Value(); break;
            case "--ticket": ticket = Value(); break;
            default: throw new ArgumentException($"Unknown argument: {arguments[i]}");
        }
    }
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme != Uri.UriSchemeHttps)
        throw new ArgumentException("Set AZURE_OPENAI_ENDPOINT to the Bicep endpoint HTTPS URL.");
    if (string.IsNullOrWhiteSpace(deployment) || ticket is not null && string.IsNullOrWhiteSpace(ticket))
        throw new ArgumentException("Deployment and ticket must not be empty.");

    var identity = new AzureCliCredential();
    var token = await identity.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]));
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    client.BaseAddress = new Uri(endpointUri.ToString().TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    var items = ticket is null ? DefaultExamples() : [(ticket, (string?)null)];
    var correct = 0;
    foreach (var (text, expected) in items)
    {
        var started = Stopwatch.StartNew();
        var team = await ClassifyAsync(client, deployment, text);
        if (expected is not null && team == expected) correct++;
        Console.WriteLine($"{text}\n  Team: {team}; expected: {expected ?? "(custom)"}; latency: {started.Elapsed.TotalSeconds:F2}s");
    }
    if (ticket is null) Console.WriteLine($"Accuracy on toy examples: {correct}/{items.Length} (not a benchmark).");
    return 0;
}

static async Task<string> ClassifyAsync(HttpClient client, string deployment, string ticket)
{
    var request = new JsonObject
    {
        ["model"] = deployment,
        ["state"] = ticket,
        ["questions"] = new JsonObject
        {
            ["team"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = "Which team should handle this support ticket?",
                ["criteria"] = new JsonObject
                {
                    ["billing"] = "Charges, invoices, and refunds",
                    ["technical"] = "Software errors, bugs, and outages",
                    ["account"] = "Sign-in, password, and account access"
                }
            }
        }
    };
    HttpResponseMessage response = null!;
    for (var attempt = 0; attempt < 3; attempt++)
    {
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        response = await client.PostAsync("providers/microsoft/v1/systemone", content);
        if (response.StatusCode == HttpStatusCode.NotFound && IsRoutingError(await response.Content.ReadAsStringAsync()))
        {
            if (attempt == 2) break;
            var delay = 1 << attempt;
            Console.WriteLine($"Decision API routing error (HTTP 404): Subdomain does not map to a resource. Request ID: {RequestId(response)}; retrying in {delay}s (retry {attempt + 1}/2).");
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(delay));
            continue;
        }
        if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 2) break;
        if (!TryRetryDelay(response, out var retryDelay)) break;
        Console.WriteLine($"Decision API throttled (HTTP 429); retrying in {retryDelay.TotalSeconds:F2}s (retry {attempt + 1}/2).");
        response.Dispose();
        await Task.Delay(retryDelay);
    }
    using (response)
    {
        var responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\nDeployment: {deployment}; request ID: {RequestId(response)}; service response: {responseText}",
                null, response.StatusCode);
        var payload = JsonNode.Parse(responseText)?.AsObject() ?? throw new JsonException("Decision API returned invalid JSON.");
        var answer = payload["answers"]?["team"]?.AsObject();
        var value = answer?["choice"]?.ToString();
        if (answer?["type"]?.ToString() != "choice" || value is null || !TeamNames().Contains(value))
            throw new InvalidOperationException($"Decision API returned an invalid team answer: {payload}");
        return value;
    }
}

static bool IsRoutingError(string responseText)
{
    try
    {
        using var document = JsonDocument.Parse(responseText);
        var error = document.RootElement.TryGetProperty("error", out var element) ? element : default;
        return error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("code", out var code) && code.GetString() == "ResourceNotFound" &&
            error.TryGetProperty("message", out var message) && message.GetString() == "Subdomain does not map to a resource.";
    }
    catch (JsonException) { return false; }
}

static bool TryRetryDelay(HttpResponseMessage response, out TimeSpan delay)
{
    delay = default;
    if (response.Headers.TryGetValues("retry-after-ms", out var values))
    {
        if (!double.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var milliseconds) || milliseconds is < 0 or > 65000)
            return false;
        delay = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }
    if (response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero && delta <= TimeSpan.FromSeconds(65))
    {
        delay = delta;
        return true;
    }
    return false;
}

static string RequestId(HttpResponseMessage response) =>
    response.Headers.TryGetValues("apim-request-id", out var values) ? values.FirstOrDefault() ?? "(not supplied)" : "(not supplied)";

static string[] TeamNames() => ["billing", "technical", "account"];
static (string Ticket, string? Expected)[] DefaultExamples() =>
[
    ("I was charged twice for my subscription.", "billing"),
    ("The API returns an error on every request.", "technical"),
    ("I cannot sign in after resetting my password.", "account")
];
static void Usage() => Console.WriteLine("Usage: dotnet run --file demo.cs -- [--endpoint HTTPS_URL] [--deployment NAME] [--ticket TEXT]");
