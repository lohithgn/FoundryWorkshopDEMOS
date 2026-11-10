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
catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or Azure.RequestFailedException or OperationCanceledException)
{
    Console.Error.WriteLine($"ERROR: {error.GetType().Name}: {error.Message}");
    return 1;
}

static async Task<int> MainAsync(string[] arguments)
{
    var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
    var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
    var toolEndpoint = Environment.GetEnvironmentVariable("WORKSHOP_TOOL_ENDPOINT");
    var attendees = 12;
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
            case "--tool-endpoint": toolEndpoint = Value(); break;
            case "--attendees": attendees = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
            default: throw new ArgumentException($"Unknown argument: {arguments[i]}");
        }
    }
    if (!IsHttps(endpoint)) throw new ArgumentException("Set FOUNDRY_PROJECT_ENDPOINT (projectEndpoint output) to an HTTPS URL.");
    if (!IsHttps(toolEndpoint)) throw new ArgumentException("Set WORKSHOP_TOOL_ENDPOINT (toolEndpoint output) to an HTTPS URL.");
    if (string.IsNullOrWhiteSpace(deployment)) throw new ArgumentException("--deployment must not be empty.");
    if (attendees is < 1 or > 999) throw new ArgumentException("--attendees must be between 1 and 999.");

    var credential = new AzureCliCredential();
    var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]));
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    client.BaseAddress = new Uri(endpoint!.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    var name = $"workshop-tools-{Guid.NewGuid():N}"[..24];
    var specPath = Path.Combine(Environment.CurrentDirectory, "openapi.json");
    if (!File.Exists(specPath)) throw new FileNotFoundException("The workshop OpenAPI specification is missing.", specPath);
    var spec = JsonNode.Parse(await File.ReadAllTextAsync(specPath))?.AsObject() ?? throw new JsonException("Invalid OpenAPI specification.");
    spec["servers"] = new JsonArray(new JsonObject { ["url"] = toolEndpoint!.TrimEnd('/') });
    var toolbox = await SendJsonAsync(client, $"toolboxes/{Uri.EscapeDataString(name)}/versions?api-version=v1", new JsonObject
    {
        ["description"] = "Read-only workshop pricing hosted in Azure Functions.",
        ["tools"] = new JsonArray(new JsonObject
        {
            ["type"] = "openapi",
            ["openapi"] = new JsonObject
            {
                ["name"] = "workshop",
                ["description"] = "Calculate the workshop price using the hosted API.",
                ["spec"] = spec,
                ["auth"] = new JsonObject { ["type"] = "anonymous" }
            }
        })
    });
    var version = toolbox["version"]?.ToString() ?? throw new JsonException("Foundry toolbox response omitted version.");
    var toolboxName = toolbox["name"]?.ToString() ?? name;
    var mcpUrl = new Uri(client.BaseAddress, $"toolboxes/{Uri.EscapeDataString(toolboxName)}/versions/{Uri.EscapeDataString(version)}/mcp?api-version=v1");
    Console.WriteLine($"Foundry Toolbox: {toolboxName} / {version}");
    try
    {
        using var mcp = new McpClient(mcpUrl, token.Token);
        var tools = await mcp.ListToolsAsync();
        if (tools.Count == 0) throw new InvalidOperationException("Foundry Toolbox returned no tools.");
        var messages = new JsonArray(new JsonObject
        {
            ["role"] = "system",
            ["content"] = "Always call the workshop pricing tool. Never calculate or guess prices yourself. Report the tool's attendee count, unit price, total, and currency."
        }, new JsonObject { ["role"] = "user", ["content"] = $"What is the workshop price for {attendees} attendees?" });
        var toolResults = new List<string>();
        for (var turn = 0; turn < 5; turn++)
        {
            var response = await ChatAsync(client, deployment, messages, tools);
            var choice = response["choices"]?.AsArray()?.FirstOrDefault()?.AsObject()
                ?? throw new InvalidOperationException("The service returned no completion choices.");
            var message = choice["message"]?.AsObject() ?? new JsonObject();
            var calls = message["tool_calls"]?.AsArray();
            if (calls is null || calls.Count == 0)
            {
                if (toolResults.Count == 0) throw new InvalidOperationException("Expected a real tool result and an agent answer.");
                var answer = message["content"]?.ToString();
                if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("Expected a real tool result and an agent answer.");
                if (!toolResults.Any(result => result.Contains("workshop-price-tool-v1", StringComparison.Ordinal)))
                    throw new InvalidOperationException("The tool did not return the deployed pricing function's source marker.");
                foreach (var result in toolResults) Console.WriteLine($"Tool result: {result}");
                Console.WriteLine($"\nAgent answer:\n{answer}");
                return 0;
            }
            messages.Add(new JsonObject { ["role"] = "assistant", ["tool_calls"] = calls.DeepClone() });
            foreach (var call in calls)
            {
                var callObject = call!.AsObject();
                var callId = callObject["id"]?.ToString() ?? throw new JsonException("Tool call omitted its ID.");
                var function = callObject["function"]?.AsObject() ?? throw new JsonException("Tool call omitted its function.");
                var functionName = function["name"]?.ToString() ?? throw new JsonException("Tool call omitted its name.");
                _ = tools.SingleOrDefault(item => item?["name"]?.ToString() == functionName)
                    ?? throw new InvalidOperationException($"Model requested an unknown Toolbox operation: {functionName}");
                using var argsJson = JsonDocument.Parse(function["arguments"]?.ToString() ?? "{}");
                var result = await mcp.CallToolAsync(functionName, JsonNode.Parse(argsJson.RootElement.GetRawText())!);
                if (result["isError"]?.GetValue<bool>() == true)
                    throw new InvalidOperationException($"Tool call failed: {result}");
                var content = result["content"]?.AsArray() ?? new JsonArray();
                var resultText = string.Join("", content.Where(item => item?["type"]?.ToString() == "text").Select(item => item?["text"]?.ToString()));
                if (string.IsNullOrWhiteSpace(resultText)) throw new InvalidOperationException("Tool call returned no result.");
                toolResults.Add(resultText);
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = resultText });
            }
        }
        throw new InvalidOperationException("The agent did not complete after five tool-call turns.");
    }
    finally
    {
        await DeleteAsync(client, $"toolboxes/{Uri.EscapeDataString(toolboxName)}/versions/{Uri.EscapeDataString(version)}?api-version=v1");
        Console.WriteLine("Temporary toolbox version deleted.");
    }
}

static async Task<JsonObject> ChatAsync(HttpClient client, string model, JsonArray messages, JsonArray tools)
{
    var functionTools = new JsonArray();
    foreach (var tool in tools)
    {
        var name = tool?["name"]?.ToString() ?? throw new JsonException("MCP tool omitted its name.");
        var description = tool?["description"]?.ToString() ?? "";
        var schema = tool?["inputSchema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" };
        functionTools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = schema }
        });
    }
    var body = new JsonObject { ["model"] = model, ["messages"] = messages.DeepClone(), ["tools"] = functionTools, ["tool_choice"] = "required" };
    using var response = await client.PostAsync("openai/v1/chat/completions", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Foundry inference failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {text}", null, response.StatusCode);
    return JsonNode.Parse(text)?.AsObject() ?? throw new JsonException("Foundry returned invalid chat-completion JSON.");
}

static async Task<JsonObject> SendJsonAsync(HttpClient client, string path, JsonNode body)
{
    using var response = await client.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Foundry Toolbox request failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {text}", null, response.StatusCode);
    return JsonNode.Parse(text)?.AsObject() ?? throw new JsonException("Foundry returned invalid JSON.");
}

static async Task DeleteAsync(HttpClient client, string path)
{
    using var response = await client.DeleteAsync(path);
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Temporary toolbox cleanup failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {text}", null, response.StatusCode);
}

static bool IsHttps(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
static void Usage() => Console.WriteLine("Usage: dotnet run --file demo.cs -- [--project-endpoint HTTPS_URL] [--deployment NAME] [--tool-endpoint HTTPS_URL] [--attendees 1..999]");

sealed class McpClient : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly Uri _url;
    private string? _session;
    private int _id = 1;

    public McpClient(Uri url, string token)
    {
        _url = url;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    }

    public async Task<JsonArray> ListToolsAsync()
    {
        var initialize = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "FoundryWorkshopDEMOS", ["version"] = "1.0" }
        });
        _ = initialize["protocolVersion"]?.ToString() ?? throw new JsonException("MCP initialization omitted protocol version.");
        using var notified = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AddSession(notified);
        using var notificationResponse = await _client.SendAsync(notified);
        await EnsureSuccessAsync(notificationResponse);
        var result = await RequestAsync("tools/list", new JsonObject());
        return result["tools"]?.AsArray() ?? throw new JsonException("MCP server returned no tools list.");
    }

    public Task<JsonObject> CallToolAsync(string name, JsonNode arguments) =>
        RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() });

    private async Task<JsonObject> RequestAsync(string method, JsonObject parameters)
    {
        var id = _id++;
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AddSession(request);
        using var response = await _client.SendAsync(request);
        if (_session is null && response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) _session = sessions.FirstOrDefault();
        await EnsureSuccessAsync(response);
        var content = await response.Content.ReadAsStringAsync();
        var jsonText = content.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].Trim()).LastOrDefault() ?? content;
        var envelope = JsonNode.Parse(jsonText)?.AsObject() ?? throw new JsonException("MCP returned invalid JSON-RPC.");
        if (envelope["error"] is JsonObject error) throw new InvalidOperationException($"MCP {method} failed: {error}");
        return envelope["result"]?.AsObject() ?? throw new JsonException($"MCP {method} omitted result.");
    }

    private void AddSession(HttpRequestMessage request)
    {
        if (_session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-03-26");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Foundry Toolbox MCP failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {await response.Content.ReadAsStringAsync()}", null, response.StatusCode);
    }

    public void Dispose() => _client.Dispose();
}
