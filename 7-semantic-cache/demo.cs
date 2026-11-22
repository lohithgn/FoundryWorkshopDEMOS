using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public static class SemanticCacheDemo
{
    public static readonly string[] Prompts =
    [
        "What is 2 + 2? Reply with the integer only.",
        "What is two plus two? Reply with the integer only.",
        "What is 2 + 2? Reply with the integer only."
    ];

    public sealed record Options(string Endpoint, string Deployment, string Session);

    public static Options Parse(string[] args)
    {
        string? endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        string deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
        string session = Guid.NewGuid().ToString();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--help")
                throw new HelpRequestedException();
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {args[index]}.");
            var value = args[++index];
            switch (args[index - 1])
            {
                case "--endpoint": endpoint = value; break;
                case "--deployment": deployment = value; break;
                case "--session": session = value; break;
                default: throw new ArgumentException($"Unknown option: {args[index - 1]}.");
            }
        }
        if (string.IsNullOrWhiteSpace(endpoint) || !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Set AZURE_OPENAI_ENDPOINT to the Bicep APIM endpoint HTTPS URL.");
        if (string.IsNullOrWhiteSpace(deployment) || string.IsNullOrWhiteSpace(session))
            throw new ArgumentException("Deployment and session must not be empty.");
        return new Options(endpoint.TrimEnd('/'), deployment, session);
    }

    public static async Task<(string Text, string Id)> AnswerAsync(
        HttpClient http, string endpoint, string deployment, string prompt, string token,
        CancellationToken cancellationToken = default, string? session = null)
    {
        var uri = $"{endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version=2024-10-21";
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (session is not null)
            request.Headers.TryAddWithoutValidation("x-workshop-cache-session", session);
        var jsonPrompt = JsonEncodedText.Encode(prompt).ToString();
        request.Content = new StringContent(
            $"{{\"messages\":[{{\"role\":\"user\",\"content\":\"{jsonPrompt}\"}}],\"max_completion_tokens\":120,\"stream\":false}}",
            Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await http.SendAsync(request, timeout.Token);
        var payload = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Gateway returned {(int)response.StatusCode}: {payload}");
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("Model returned no choices.");
        var choice = choices[0];
        var finishReason = choice.GetProperty("finish_reason").GetString();
        var text = choice.GetProperty("message").GetProperty("content").GetString();
        if (finishReason != "stop" || string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Model did not return a complete, nonempty answer.");
        var id = root.TryGetProperty("id", out var responseId) ? responseId.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Response has no ID; cannot observe gateway response reuse.");
        return (text, id);
    }

    public static async Task PresentAsync(
        HttpClient http, string endpoint, string deployment, string session, string token,
        Func<TimeSpan, Task>? delay = null, CancellationToken cancellationToken = default)
    {
        delay ??= Task.Delay;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reused = 0;
        for (var index = 0; index < Prompts.Length; index++)
        {
            if (index > 0)
                await delay(TimeSpan.FromSeconds(2));
            var stopwatch = Stopwatch.StartNew();
            var (text, id) = await AnswerAsync(
                http, endpoint, deployment, Prompts[index], token, cancellationToken, session);
            if (text.Trim() != "4")
                throw new InvalidOperationException($"Unexpected arithmetic answer \"{JsonEncodedText.Encode(text)}\"; expected exactly 4.");
            var repeated = !seen.Add(id);
            if (repeated)
                reused++;
            Console.WriteLine($"\n{(repeated ? "REUSED RESPONSE" : "NEW RESPONSE ID")}; {stopwatch.Elapsed.TotalSeconds:F2}s; id={id}\n{Prompts[index]}\n{text}");
        }
        Console.WriteLine($"\nChat requests sent to APIM: {Prompts.Length}; repeated response IDs: {reused}.");
        Console.WriteLine("APIM owns embedding calls, cache lookup, and storage. Confirm HIT/MISS and backend calls in the APIM portal trace.");
        if (reused == 0)
            throw new InvalidOperationException("No response reuse observed. Inspect APIM tracing, Redis connectivity, managed-identity RBAC, threshold, and cache storage/index propagation.");
    }

    public static async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo("az")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "account", "get-access-token", "--scope", "https://cognitiveservices.azure.com/.default", "--query", "accessToken", "-o", "tsv" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Azure CLI.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"Azure CLI token acquisition failed: {error.Trim()}");
        return output.Trim();
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Options options;
            try { options = Parse(args); }
            catch (HelpRequestedException)
            {
                Console.WriteLine("Usage: dotnet run --file demo.cs -- [--endpoint HTTPS_URL] [--deployment NAME] [--session ID]");
                return 0;
            }
            Console.WriteLine($"Gateway: {options.Endpoint}; cache session: {options.Session}");
            var token = await GetTokenAsync();
            using var http = new HttpClient();
            await PresentAsync(http, options.Endpoint, options.Deployment, options.Session, token);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}");
            return 1;
        }
    }

    private sealed class HelpRequestedException : Exception { }
}
