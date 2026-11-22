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
    var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
    var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-5-mini";
    var prompt = DefaultPrompt();
    var debug = false;
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
            case "--project-endpoint": endpoint = Value(); break;
            case "--deployment": deployment = Value(); break;
            case "--prompt": prompt = Value(); break;
            case "--debug-response": debug = true; break;
            default: throw new ArgumentException($"Unknown argument: {arguments[i]}");
        }
    }
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var projectEndpoint) || projectEndpoint.Scheme != Uri.UriSchemeHttps)
        throw new ArgumentException("Set FOUNDRY_PROJECT_ENDPOINT to the Bicep projectEndpoint HTTPS URL.");
    if (string.IsNullOrWhiteSpace(deployment) || string.IsNullOrWhiteSpace(prompt))
        throw new ArgumentException("--deployment and --prompt must not be empty.");

    var credential = new AzureCliCredential();
    var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]));
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    client.BaseAddress = new Uri(projectEndpoint.ToString().TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    var agentName = $"webiq-demo-{Guid.NewGuid():N}"[..20];
    const string instructions = "Search the public web before answering. Be concise and cite sources.";
    foreach (var expectSearch in new[] { true, false })
    {
        var label = expectSearch ? "WITH WebSearchTool()" : "WITHOUT WebSearchTool()";
        Console.WriteLine($"\n=== {label} ===");
        var definition = new JsonObject
        {
            ["kind"] = "prompt",
            ["model"] = deployment,
            ["instructions"] = instructions,
            ["tools"] = expectSearch
                ? new JsonArray(new JsonObject { ["type"] = "web_search" })
                : new JsonArray()
        };
        var create = new JsonObject { ["definition"] = definition };
        var created = await SendJsonAsync(client, $"agents/{Uri.EscapeDataString(agentName)}/versions?api-version=v1", create);
        var version = created["version"]?.ToString() ?? throw new JsonException("Foundry agent response omitted version.");
        var returnedName = created["name"]?.ToString() ?? agentName;
        Console.WriteLine($"Temporary Foundry agent: {returnedName} / {version}");
        try
        {
            var response = await SendJsonAsync(client, "openai/v1/responses", new JsonObject
            {
                ["input"] = prompt,
                ["tool_choice"] = expectSearch ? "required" : "none",
                ["agent_reference"] = new JsonObject { ["name"] = returnedName, ["version"] = version }
            });
            ShowResponse(response, expectSearch, debug);
        }
        finally
        {
            await DeleteAsync(client, $"agents/{Uri.EscapeDataString(returnedName)}/versions/{Uri.EscapeDataString(version)}?api-version=v1");
            Console.WriteLine("Temporary agent version deleted.");
        }
    }
    Console.WriteLine("\nComparison: the same model, instructions, and prompt were used in independent requests. With the tool, the answer has completed searches and structured citations. Without it, instructions alone cannot enable browsing: the model may answer from prior knowledge or explain that it cannot search. This comparison demonstrates source retrieval, not which answer is more accurate.");
    return 0;
}

static async Task<JsonObject> SendJsonAsync(HttpClient client, string path, JsonNode body)
{
    using var response = await client.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"Foundry request failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {text}", null, response.StatusCode);
    return JsonNode.Parse(text)?.AsObject() ?? throw new JsonException("Foundry returned invalid JSON.");
}

static async Task DeleteAsync(HttpClient client, string path)
{
    using var response = await client.DeleteAsync(path);
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"Temporary Foundry agent cleanup failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {text}", null, response.StatusCode);
}

static void ShowResponse(JsonObject response, bool expectSearch, bool debug)
{
    var output = response["output"]?.AsArray() ?? new JsonArray();
    var searches = output.Where(item => item?["type"]?.ToString() == "web_search_call").ToArray();
    var citations = output
        .Where(item => item?["type"]?.ToString() == "message")
        .SelectMany(item => item?["content"]?.AsArray() ?? [])
        .Where(content => content?["type"]?.ToString() == "output_text")
        .SelectMany(content => content?["annotations"]?.AsArray() ?? [])
        .Where(annotation => annotation?["type"]?.ToString() == "url_citation")
        .ToArray();
    var statuses = searches.Select(search => search?["status"]?.ToString()).ToArray();
    Console.WriteLine($"\nResponse status: {response["status"]}\nOutput types: [{string.Join(", ", output.Select(item => $"'{item?["type"]}'"))}]\nSearch calls: {searches.Length}\nSearch statuses: [{string.Join(", ", statuses.Select(status => $"'{status}'"))}]\nURL citations: {citations.Length}");
    if (debug) Console.WriteLine($"\nRaw response JSON:\n{response.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
    var answer = ResponseText(output);
    if (response["status"]?.ToString() != "completed" || string.IsNullOrWhiteSpace(answer))
        throw new InvalidOperationException($"Agent did not complete with text: {response["status"]}; {response["error"]}");
    if (!expectSearch)
    {
        if (searches.Length != 0 || citations.Length != 0)
            throw new InvalidOperationException("The no-tool agent unexpectedly returned web-search calls or URL citations. Inspect the response diagnostics and agent configuration.");
        Console.WriteLine($"\n{answer}\n\nNo Web Search tool: this run did not retrieve web sources. Any plain-text URLs are not verified search citations.");
        return;
    }
    if (searches.Length == 0 || citations.Length == 0)
    {
        var missing = new List<string>();
        if (searches.Length == 0) missing.Add("web_search_call output");
        if (citations.Length == 0) missing.Add("url_citation annotations");
        throw new InvalidOperationException($"Grounding evidence missing: {string.Join(", ", missing)}. Inspect the response diagnostics and Foundry agent trace; plain-text URLs are not structured citations. Use --debug-response to inspect the raw response.");
    }
    if (statuses.Any(status => status != "completed"))
        throw new InvalidOperationException($"A web-search call failed or did not complete: [{string.Join(", ", statuses)}].");
    Console.WriteLine($"\nWeb searches: {searches.Length}\n\n{answer}\n\nSources:");
    foreach (var citation in citations)
        Console.WriteLine($"- {citation?["title"]}: {citation?["url"]}");
}

static string? ResponseText(JsonArray output) =>
    string.Join("", output.Where(item => item?["type"]?.ToString() == "message")
        .SelectMany(item => item?["content"]?.AsArray() ?? [])
        .Where(content => content?["type"]?.ToString() == "output_text")
        .Select(content => content?["text"]?.ToString()));

static void Usage() =>
    Console.WriteLine("Usage: dotnet run --file demo.cs -- [--project-endpoint HTTPS_URL] [--deployment NAME] [--prompt TEXT] [--debug-response]");

static string DefaultPrompt() => "Search Microsoft Learn for Microsoft Foundry model router. Explain what it does in three short bullets and cite the source URLs.";
