#:property PublishAot=false
#:package Azure.Identity@1.17.0
#:package Microsoft.Agents.AI.Workflows@1.28.0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI.Workflows;

try
{
    return await MainAsync(args);
}
catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or ArgumentException or Azure.RequestFailedException or OperationCanceledException)
{
    Console.Error.WriteLine($"ERROR: {error.Message}");
    return 1;
}

static async Task<int> MainAsync(string[] arguments)
{
    var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
    var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
    var prompt = DefaultPrompt();
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
    var writer = ((Func<string, ValueTask<string>>)(input =>
        GenerateAsync(client, deployment, "Draft a friendly workshop invitation in at most 100 words.", [new("user", input)])))
        .BindAsExecutor("writer");
    var editor = ((Func<string, ValueTask<string>>)(draft =>
        GenerateAsync(client, deployment,
            "Edit the writer's draft into a final invitation of at most 60 words. Preserve all supplied facts, invent no details, and return only the invitation.",
            [new("user", prompt), new("assistant", draft)])))
        .BindAsExecutor("editor");
    WorkflowBuilder builder = new(writer);
    builder.AddEdge(writer, editor).WithOutputFrom(writer, editor);
    var workflow = builder.Build();
    Console.WriteLine("Workflow: writer -> editor");
    await using var run = await InProcessExecution.RunAsync(workflow, prompt);
    var outputs = run.NewEvents.OfType<WorkflowOutputEvent>().Select(e => e.Data as string).ToArray();
    if (outputs.Length != 2)
        throw new InvalidOperationException($"Expected both workflow stages; received {outputs.Length}.");
    ShowStage("Writer draft", outputs[0]);
    ShowStage("Editor final", outputs[1]);
    return 0;
}

static async ValueTask<string> GenerateAsync(HttpClient client, string model, string instructions, (string Role, string Text)[] messages)
{
    var allMessages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = instructions });
    foreach (var (role, messageText) in messages)
        allMessages.Add(new JsonObject { ["role"] = role, ["content"] = messageText });
    var request = new JsonObject { ["model"] = model, ["messages"] = allMessages };
    using var response = await client.PostAsync("openai/v1/chat/completions",
        new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"Foundry inference failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
    var result = JsonNode.Parse(body)?.AsObject() ?? throw new JsonException("Foundry returned invalid chat-completion JSON.");
    var choices = result["choices"]?.AsArray();
    var choice = choices?.FirstOrDefault()?.AsObject();
    var text = choice?["message"]?["content"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(text) || choice?["finish_reason"]?.ToString() != "stop")
        throw new InvalidOperationException($"Workflow agent returned no complete text (finish_reason={choice?["finish_reason"]}).");
    return text;
}

static void ShowStage(string label, string? output)
{
    if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException($"{label} returned no agent text.");
    Console.WriteLine($"\n=== {label} ===\n{output}");
}

static void Usage() =>
    Console.WriteLine("Usage: dotnet run --file demo.cs -- [--project-endpoint HTTPS_URL] [--deployment NAME] [--prompt TEXT]");

static string DefaultPrompt() =>
    "Invite Python developers to a free, 45-minute introductory Microsoft Foundry workshop on Friday. No prior AI experience is needed.";
