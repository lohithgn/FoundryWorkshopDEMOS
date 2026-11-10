using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class FoundryIqDemo
{
    public const string ApiVersion = "2026-08-01-preview";
    public const string Index = "workshop-index";
    public const string Source = "workshop-source";
    public const string Base = "workshop-knowledge";
    public const string Question = "When is the workshop, how long does it last, and what should I bring?";
    public static readonly string ProjectPattern = @"^https://[a-zA-Z0-9-]+\.services\.ai\.azure\.com/api/projects/[a-zA-Z0-9_.-]+/?$";
    public static readonly JsonArray Documents =
    [
        new JsonObject { ["id"] = "schedule", ["title"] = "Workshop schedule", ["content"] = "The beginner Microsoft Foundry workshop is on Friday at 10:00. It lasts 45 minutes." },
        new JsonObject { ["id"] = "equipment", ["title"] = "What to bring", ["content"] = "Bring a laptop with Python 3.11 and Azure CLI installed. No prior AI experience is required." },
        new JsonObject { ["id"] = "cost", ["title"] = "Workshop registration", ["content"] = "Attendance at the workshop is free. Register with the workshop organizer before Thursday." }
    ];

    public sealed record Options(string SearchEndpoint, string ModelEndpoint, string? ProjectEndpoint, string Deployment, string Question);

    public static Options Parse(string[] args, IReadOnlyDictionary<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(item => (string)item.Key, item => item.Value?.ToString());
        var search = environment.GetValueOrDefault("AZURE_SEARCH_ENDPOINT");
        var model = environment.GetValueOrDefault("AZURE_OPENAI_ENDPOINT");
        var project = environment.GetValueOrDefault("FOUNDRY_PROJECT_ENDPOINT");
        var deployment = environment.GetValueOrDefault("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
        var question = Question;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--help") throw new HelpRequestedException();
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {option}.");
            var value = args[++index];
            switch (option)
            {
                case "--search-endpoint": search = value; break;
                case "--endpoint": model = value; break;
                case "--project-endpoint": project = value; break;
                case "--deployment": deployment = value; break;
                case "--question": question = value; break;
                default: throw new ArgumentException($"Unknown option: {option}.");
            }
        }
        if (string.IsNullOrWhiteSpace(search) || !search.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(model) || !model.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Set AZURE_SEARCH_ENDPOINT and AZURE_OPENAI_ENDPOINT to the Bicep HTTPS outputs.");
        if (string.IsNullOrWhiteSpace(deployment) || string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Deployment and question must not be empty.");
        if (project is not null && !Regex.IsMatch(project, ProjectPattern, RegexOptions.CultureInvariant))
            throw new ArgumentException("Use the Bicep projectEndpoint output, not the model or Search endpoint.");
        return new Options(search.TrimEnd('/'), model.TrimEnd('/'), project, deployment, question);
    }

    private static string WithApi(string url) => url + (url.Contains('?') ? "&" : "?") + "api-version=" + ApiVersion;

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpMethod method, string uri, string token, JsonNode? body = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new HttpRequestException($"Azure AI Search returned {(int)response.StatusCode}: {content}");
        }
        return response;
    }

    public static async Task ConfigureAsync(
        HttpClient http, string searchEndpoint, string modelEndpoint, string deployment,
        string token, Func<TimeSpan, Task>? delay = null, Func<DateTimeOffset>? now = null,
        CancellationToken cancellationToken = default)
    {
        delay ??= Task.Delay;
        now ??= () => DateTimeOffset.UtcNow;
        var prefix = searchEndpoint.TrimEnd('/');
        var indexSchema = JsonNode.Parse("""
        {"name":"workshop-index","fields":[{"name":"id","type":"Edm.String","key":true,"filterable":true},{"name":"title","type":"Edm.String","searchable":true},{"name":"content","type":"Edm.String","searchable":true}],"semantic":{"defaultConfiguration":"workshop-semantic","configurations":[{"name":"workshop-semantic","prioritizedFields":{"titleField":{"fieldName":"title"},"prioritizedContentFields":[{"fieldName":"content"}]}}]}}
        """)!;
        using (await SendAsync(http, HttpMethod.Put, WithApi($"{prefix}/indexes/{Index}"), token, indexSchema, cancellationToken)) { }
        var docs = new JsonArray(Documents.Select(document =>
        {
            var row = (JsonObject)document!.DeepClone();
            row["@search.action"] = "mergeOrUpload";
            return (JsonNode)row;
        }).ToArray());
        using var uploaded = await SendAsync(http, HttpMethod.Post, WithApi($"{prefix}/indexes/{Index}/docs/index"),
            token, new JsonObject { ["value"] = docs }, cancellationToken);
        var uploadResult = JsonNode.Parse(await uploaded.Content.ReadAsStringAsync(cancellationToken))?["value"]?.AsArray();
        if (uploadResult is null || uploadResult.Count != Documents.Count ||
            uploadResult.Any(item => item?["status"]?.GetValue<bool>() != true))
            throw new InvalidOperationException($"Document indexing failed: {uploadResult?.ToJsonString() ?? "missing result"}");

        var deadline = now() + TimeSpan.FromSeconds(60);
        while (true)
        {
            using var countResponse = await SendAsync(http, HttpMethod.Get, WithApi($"{prefix}/indexes/{Index}/docs/$count"), token, cancellationToken: cancellationToken);
            var countText = await countResponse.Content.ReadAsStringAsync(cancellationToken);
            if (int.TryParse(countText, out var count) && count == Documents.Count) break;
            if (now() >= deadline)
                throw new TimeoutException("Documents are not yet searchable; rerun after indexing completes.");
            await delay(TimeSpan.FromSeconds(2));
        }
        using (await SendAsync(http, HttpMethod.Put, WithApi($"{prefix}/knowledgesources/{Source}"), token,
            JsonNode.Parse("""
            {"name":"workshop-source","kind":"searchIndex","description":"Three synthetic workshop fact sheets.","searchIndexParameters":{"searchIndexName":"workshop-index","sourceDataFields":[{"name":"id"},{"name":"title"},{"name":"content"}]}}
            """), cancellationToken)) { }
        using (await SendAsync(http, HttpMethod.Put, WithApi($"{prefix}/knowledgebases/{Base}"), token,
            BuildKnowledgeBase(modelEndpoint, deployment), cancellationToken)) { }
    }

    public static JsonNode BuildKnowledgeBase(string modelEndpoint, string deployment) =>
        new JsonObject
        {
            ["name"] = Base,
            ["knowledgeSources"] = new JsonArray(new JsonObject { ["name"] = Source }),
            ["models"] = new JsonArray(new JsonObject
            {
                ["kind"] = "azureOpenAI",
                ["azureOpenAIParameters"] = new JsonObject
                {
                    ["resourceUri"] = modelEndpoint,
                    ["deploymentId"] = deployment,
                    ["modelName"] = "gpt-4.1-mini"
                }
            }),
            ["outputMode"] = "answerSynthesis",
            ["answerInstructions"] = "Answer in two short sentences using only the retrieved workshop facts. Cite sources."
        };

    public static async Task<JsonDocument> RetrieveAsync(
        HttpClient http, string searchEndpoint, string question, string token,
        CancellationToken cancellationToken = default)
    {
        var requestBody = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = question })
            }),
            ["knowledgeSourceParams"] = new JsonArray(new JsonObject
            {
                ["kind"] = "searchIndex", ["knowledgeSourceName"] = Source,
                ["includeReferences"] = true, ["includeReferenceSourceData"] = true, ["alwaysQuerySource"] = true
            }),
            ["includeActivity"] = true,
            ["retrievalReasoningEffort"] = new JsonObject { ["kind"] = "low" }
        };
        using var response = await SendAsync(http, HttpMethod.Post,
            WithApi($"{searchEndpoint.TrimEnd('/')}/knowledgebases/{Base}/retrieve"), token, requestBody, cancellationToken);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = payload.RootElement;
        var answer = string.Join("\n", root.TryGetProperty("response", out var messages)
            ? messages.EnumerateArray().SelectMany(message => message.GetProperty("content").EnumerateArray())
                .Where(content => content.GetProperty("type").GetString() == "text")
                .Select(content => content.GetProperty("text").GetString() ?? "")
            : []);
        if (string.IsNullOrWhiteSpace(answer) || !root.TryGetProperty("references", out var refs) ||
            refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() == 0 ||
            !root.TryGetProperty("activity", out var activity) || activity.ValueKind != JsonValueKind.Array ||
            activity.GetArrayLength() == 0)
            throw new InvalidOperationException("Retrieval returned no grounded answer, references, or activity.");
        if (activity.EnumerateArray().Any(item => item.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.False))
            throw new InvalidOperationException($"Retrieval activity contains an error: {activity}");
        Console.WriteLine($"\nAnswer:\n{answer}\n\nRetrieval activity:\n{activity}\n\nSource references:\n{refs}");
        return payload;
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Options options;
            try { options = Parse(args); }
            catch (HelpRequestedException)
            {
                Console.WriteLine("Usage: dotnet run --file demo.cs -- [--search-endpoint HTTPS_URL] [--endpoint HTTPS_URL] [--project-endpoint HTTPS_URL] [--deployment NAME] [--question TEXT]");
                return 0;
            }
            if (options.ProjectEndpoint is not null) Console.WriteLine($"Foundry project: {options.ProjectEndpoint}");
            var token = await FoundryIqAuth.GetTokenAsync("https://search.azure.com/.default");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            await ConfigureAsync(http, options.SearchEndpoint, options.ModelEndpoint, options.Deployment, token);
            Console.WriteLine($"Knowledge base: {Base}; source: {Source}; index: {Index}");
            using var result = await RetrieveAsync(http, options.SearchEndpoint, options.Question, token);
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

public static class FoundryIqAuth
{
    public static async Task<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "account", "get-access-token", "--scope", scope, "--query", "accessToken", "-o", "tsv" })
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
}
