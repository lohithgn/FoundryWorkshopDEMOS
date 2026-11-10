#:property PublishAot=false
#:package Azure.Identity@1.17.0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

try { return await MainAsync(args); }
catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine($"ERROR: {error.Message}");
    return 2;
}
catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or Azure.RequestFailedException or OperationCanceledException)
{
    Console.Error.WriteLine($"ERROR: {error.Message}");
    return 1;
}

static async Task<int> MainAsync(string[] arguments)
{
    var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
    var text = "Welcome to our friendly beginner AI workshop!";
    var threshold = 2;
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
            case "--text": text = Value(); break;
            case "--threshold": threshold = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
            default: throw new ArgumentException($"Unknown argument: {arguments[i]}");
        }
    }
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme != Uri.UriSchemeHttps)
        throw new ArgumentException("Set AZURE_OPENAI_ENDPOINT to the Bicep endpoint HTTPS URL.");
    if (string.IsNullOrWhiteSpace(text) || text.Length > 10000)
        throw new ArgumentException("--text must contain 1-10000 characters.");
    if (threshold is not (2 or 4 or 6)) throw new ArgumentException("--threshold must be 2, 4, or 6.");
    var credential = new AzureCliCredential();
    var token = await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]));
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    client.BaseAddress = new Uri(endpointUri.ToString().TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    var request = new JsonObject { ["text"] = text, ["outputType"] = "FourSeverityLevels" };
    using var response = await client.PostAsync("contentsafety/text:analyze?api-version=2024-09-01",
        new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"Content Safety request failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
    var result = JsonNode.Parse(body)?.AsObject() ?? throw new JsonException("Content Safety returned invalid JSON.");
    var analyses = result["categoriesAnalysis"]?.AsArray();
    var categories = new HashSet<string>(["Hate", "SelfHarm", "Sexual", "Violence"], StringComparer.Ordinal);
    if (analyses is null || analyses.Count == 0 ||
        !categories.SetEquals(analyses.Select(item => item?["category"]?.ToString() ?? "")))
        throw new InvalidOperationException("Content Safety did not return all four harm categories.");
    var blocked = false;
    foreach (var analysis in analyses)
    {
        var category = analysis!["category"]!.ToString();
        if (!int.TryParse(analysis["severity"]?.ToString(), out var severity) || severity is not (0 or 2 or 4 or 6))
            throw new InvalidOperationException($"Invalid severity for {category}: {analysis["severity"]}");
        Console.WriteLine($"{category}: {severity}");
        blocked |= severity >= threshold;
    }
    Console.WriteLine($"Policy: {(blocked ? "BLOCK" : "ALLOW")} (threshold >= {threshold})");
    return 0;
}

static void Usage() => Console.WriteLine("Usage: dotnet run --file demo.cs -- [--endpoint HTTPS_URL] [--text TEXT] [--threshold 2|4|6]");
