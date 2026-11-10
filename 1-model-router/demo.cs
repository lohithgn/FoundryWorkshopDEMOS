#:property PublishAot=false
#:package Azure.Identity@1.17.0

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

try
{
    return await MainAsync(args);
}
catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine($"ERROR: {error.Message}");
    return 2;
}
catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or Azure.RequestFailedException)
{
    Console.Error.WriteLine($"ERROR: {error.Message}");
    return 1;
}

static async Task<int> MainAsync(string[] arguments)
{
    string? endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
    var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "model-router";
    string? prompt = null;
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
            case "--prompt": prompt = Value(); break;
            default: throw new ArgumentException($"Unknown argument: {arguments[i]}");
        }
    }
    if (string.IsNullOrWhiteSpace(endpoint))
        throw new ArgumentException("Set AZURE_OPENAI_ENDPOINT or pass --endpoint.");
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme != Uri.UriSchemeHttps)
        throw new ArgumentException("--endpoint must be an absolute HTTPS URL.");
    if (string.IsNullOrWhiteSpace(deployment)) throw new ArgumentException("--deployment must not be empty.");
    if (prompt is not null && string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("--prompt must not be empty.");

    var items = prompt is null ? DefaultPrompts() : [("Custom", prompt)];
    var credential = new AzureCliCredential();
    var token = await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]));
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    client.BaseAddress = new Uri(endpointUri.ToString().TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    foreach (var (label, text) in items)
    {
        Console.WriteLine($"\n=== {label} ===\nPrompt: {text}");
        var started = Stopwatch.StartNew();
        var request = new JsonObject
        {
            ["model"] = deployment,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "You are a helpful assistant. Keep answers concise." },
                new JsonObject { ["role"] = "user", ["content"] = text })
        };
        using var response = await client.PostAsync(
            $"openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version=2024-10-21",
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
        var result = JsonNode.Parse(body)?.AsObject() ?? throw new JsonException("The service returned invalid JSON.");
        Console.WriteLine($"Serving model: {result["model"]}");
        Console.WriteLine($"Elapsed: {started.Elapsed.TotalSeconds:F2}s (client-observed, including routing/network)");
        if (result["usage"] is JsonObject usage)
            Console.WriteLine($"Tokens: input={usage["prompt_tokens"]}, output={usage["completion_tokens"]}, total={usage["total_tokens"]}");
        var choices = result["choices"]?.AsArray();
        if (choices is null || choices.Count == 0) throw new InvalidOperationException("The service returned no completion choices.");
        var choice = choices[0]!.AsObject();
        var message = choice["message"]?.AsObject() ?? new JsonObject();
        if (!string.IsNullOrEmpty(message["refusal"]?.ToString()))
            throw new InvalidOperationException($"The model refused the prompt: {message["refusal"]}");
        if (string.IsNullOrEmpty(message["content"]?.ToString()))
            throw new InvalidOperationException($"The service returned no text (finish_reason={choice["finish_reason"]}).");
        Console.WriteLine($"Finish reason: {choice["finish_reason"]}");
        Console.WriteLine($"Answer:\n{message["content"]}");
    }
    return 0;
}

static void Usage() =>
    Console.WriteLine("Usage: dotnet run --file demo.cs -- [--endpoint HTTPS_URL] [--deployment NAME] [--prompt TEXT]");

static (string Label, string Text)[] DefaultPrompts() =>
[
    ("Simple", "What is the capital of France? Answer in one sentence."),
    ("Writing", "Write a friendly, three-sentence invitation to a workshop about Microsoft Foundry. The audience is Python developers new to AI."),
    ("Reasoning", "A workshop has 120 attendees. Each lab needs one mentor per started group of 8 attendees, and there are 11 mentors. Attendees may join either of two consecutive lab slots. Find an allocation that minimizes the maximum number of mentors needed in either slot. Explain why it is optimal, including rounding to whole groups.")
];
