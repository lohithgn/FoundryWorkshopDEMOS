namespace AgentOrchestrations;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record ChatMessage(string Role, string Text, string? AuthorName = null);

public sealed record AgentResponse(string AuthorName, IReadOnlyList<ChatMessage> Messages)
{
    public string Text => string.Join("\n", Messages.Select(message => message.Text)).Trim();
}

public sealed record WorkflowResult(
    IReadOnlyList<AgentResponse> Outputs,
    IReadOnlyList<AgentResponse> IntermediateOutputs);

public interface IChatClient
{
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);
}

public sealed class FoundryChatClient : IChatClient, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Uri _completionsUri;
    private readonly string _deployment;

    public FoundryChatClient(string projectEndpoint, string deployment)
    {
        var project = new Uri(projectEndpoint, UriKind.Absolute);
        if (!Demo.IsFoundryProjectEndpoint(project))
            throw new ArgumentException("Expected an HTTPS Foundry projectEndpoint URL.", nameof(projectEndpoint));
        var resource = new UriBuilder(project) { Path = "", Query = "", Fragment = "" }.Uri;
        _completionsUri = new Uri(resource,
            $"models/chat/completions?api-version=2024-05-01-preview");
        _deployment = deployment;
    }

    public async Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var token = await AzureCliToken.GetAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, _completionsUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            model = _deployment,
            messages = messages.Select(message => new { role = message.Role, content = message.Text }),
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Foundry chat request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}",
                null, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        var text = json.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidOperationException("Foundry returned an empty chat response.")
            : text;
    }

    public void Dispose() => _http.Dispose();
}

internal static class AzureCliToken
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _token;
    private static DateTimeOffset _expiresAt;

    public static async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return _token;
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("az")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[]
                     {
                         "account", "get-access-token", "--resource",
                         "https://cognitiveservices.azure.com/", "--query", "accessToken", "-o", "tsv",
                     })
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0 || output.Length == 0)
                throw new InvalidOperationException(
                    $"Azure CLI token acquisition failed (exit {process.ExitCode}): {error}");
            _token = output;
            _expiresAt = DateTimeOffset.UtcNow.AddMinutes(45);
            return _token;
        }
        finally
        {
            Gate.Release();
        }
    }
}

public static class Demo
{
    public const int WorkflowTimeoutSeconds = 300;
    public static readonly string[] Patterns = ["sequential", "concurrent", "group-chat", "magentic"];

    public static bool IsFoundryProjectEndpoint(Uri endpoint)
    {
        const string suffix = ".services.ai.azure.com";
        const string pathPrefix = "/api/projects/";
        if (endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort ||
            !endpoint.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            endpoint.Host.Length == suffix.Length || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            !endpoint.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal))
            return false;
        var project = endpoint.AbsolutePath[pathPrefix.Length..];
        return project.Length > 0 && project.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    public static readonly IReadOnlyDictionary<string, string> Prompts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sequential"] = "Invite beginners to a free, 30-minute Python workshop on Friday.",
            ["concurrent"] = "We are planning a friendly beginner Python workshop.",
            ["group-chat"] = "Plan a 30-minute beginner Python workshop with exactly two agenda items.",
            ["magentic"] = "Create a tiny card for a beginner Python workshop: one short title and a two-item bring-along checklist. Ask the title_writer for the title and checklist_writer for the checklist, then combine their answers. No research is needed.",
        };

    public static async Task<int> MainAsync(string[] args, string? fixedPattern = null)
    {
        var options = ParseArguments(args, fixedPattern);
        if (options.Help)
        {
            PrintHelp(fixedPattern);
            return 0;
        }
        if (options.Error is not null)
        {
            Console.Error.WriteLine($"error: {options.Error}");
            Console.Error.WriteLine($"Run with --help for usage.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(options.Endpoint) ||
            !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) ||
            !IsFoundryProjectEndpoint(endpoint))
        {
            Console.Error.WriteLine("error: Set FOUNDRY_PROJECT_ENDPOINT to the Bicep projectEndpoint HTTPS URL.");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(options.Deployment))
        {
            Console.Error.WriteLine("error: Deployment must not be empty.");
            return 2;
        }

        try
        {
            using var client = new FoundryChatClient(endpoint.ToString(), options.Deployment);
            var selected = options.Pattern == "all" ? Patterns : [options.Pattern];
            foreach (var pattern in selected)
            {
                Console.WriteLine($"\n=== {pattern.ToUpperInvariant()} ===\nTask: {Prompts[pattern]}");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(WorkflowTimeoutSeconds));
                var result = await RunPatternAsync(client, pattern, Prompts[pattern], deadline.Token);
                if (result.IntermediateOutputs.Count > 0)
                {
                    Console.WriteLine("\nParticipant updates:");
                    Show(result.IntermediateOutputs, pattern);
                }
                Console.WriteLine("\nWorkflow output:");
                Show(result.Outputs, pattern);
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"error: Workflow exceeded its {WorkflowTimeoutSeconds}-second deadline.");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    public static async Task<WorkflowResult> RunPatternAsync(
        IChatClient client, string pattern, string prompt, CancellationToken cancellationToken = default)
    {
        return pattern switch
        {
            "sequential" => await RunSequentialAsync(client, prompt, cancellationToken),
            "concurrent" => await RunConcurrentAsync(client, prompt, cancellationToken),
            "group-chat" => await RunGroupChatAsync(client, prompt, cancellationToken),
            "magentic" => await RunMagenticAsync(client, prompt, cancellationToken),
            _ => throw new ArgumentException($"Unknown pattern '{pattern}'.", nameof(pattern)),
        };
    }

    public static void Show(IReadOnlyList<AgentResponse> outputs, string label)
    {
        if (outputs.Count == 0)
            throw new InvalidOperationException($"{label}: no workflow output.");
        foreach (var output in outputs)
        {
            if (output is null || string.IsNullOrWhiteSpace(output.Text))
                throw new InvalidOperationException($"{label}: expected a nonempty AgentResponse.");
            var names = string.Join(", ", output.Messages.Select(message => message.AuthorName ?? "agent").Distinct());
            Console.WriteLine($"\n[{names}]\n{output.Text}");
        }
    }

    public static string SequentialPrompt => Prompts["sequential"];
    public static string ConcurrentPrompt => Prompts["concurrent"];
    public static string GroupChatPrompt => Prompts["group-chat"];
    public static string MagenticPrompt => Prompts["magentic"];

    public static string SelectSpeaker(int currentRound, IReadOnlyCollection<string> participants)
    {
        if (participants.Count == 0)
            throw new ArgumentException("At least one participant is required.", nameof(participants));
        var names = participants.ToArray();
        return names[currentRound % names.Length];
    }

    private static async Task<WorkflowResult> RunSequentialAsync(
        IChatClient client, string prompt, CancellationToken cancellationToken)
    {
        var history = new List<ChatMessage> { new("user", prompt) };
        var outputs = new List<AgentResponse>();
        var writer = await AgentAsync(client, "writer", "Write a friendly two-line invitation.",
            history, cancellationToken);
        history.AddRange(writer.Messages);
        outputs.Add(writer);
        var editor = await AgentAsync(client, "editor",
            "Shorten the writer's invitation to one sentence. Preserve facts; invent nothing.",
            history, cancellationToken);
        outputs.Add(editor);
        return new WorkflowResult(outputs, []);
    }

    private static async Task<WorkflowResult> RunConcurrentAsync(
        IChatClient client, string prompt, CancellationToken cancellationToken)
    {
        var branches = new[]
        {
            AgentAsync(client, "title", "Suggest exactly one short workshop title.",
                [new ChatMessage("user", prompt)], cancellationToken),
            AgentAsync(client, "snack", "Suggest exactly one simple vegetarian snack.",
                [new ChatMessage("user", prompt)], cancellationToken),
        };
        var results = await Task.WhenAll(branches);
        return new WorkflowResult(results, []);
    }

    private static async Task<WorkflowResult> RunGroupChatAsync(
        IChatClient client, string prompt, CancellationToken cancellationToken)
    {
        var participants = new[] { "planner", "reviewer" };
        var instructions = new Dictionary<string, string>
        {
            ["planner"] = "Propose or revise a two-item workshop agenda. Use at most 40 words.",
            ["reviewer"] = "Review the agenda for beginners and suggest one small improvement. Use at most 40 words.",
        };
        var history = new List<ChatMessage> { new("user", prompt) };
        var outputs = new List<AgentResponse>();
        var assistantMessages = 0;
        for (var round = 0; round < 5 && assistantMessages < 4; round++)
        {
            var speaker = SelectSpeaker(round, participants);
            var response = await AgentAsync(client, speaker, instructions[speaker], history, cancellationToken);
            history.AddRange(response.Messages);
            outputs.Add(response);
            assistantMessages += response.Messages.Count(message => message.Role == "assistant");
        }
        return new WorkflowResult(outputs, []);
    }

    private static async Task<WorkflowResult> RunMagenticAsync(
        IChatClient client, string prompt, CancellationToken cancellationToken)
    {
        const string managerInstructions =
            """
            Magentic coordinator: decide whether the workshop-card request is complete and which specialist to call.
            You must use both title_writer and checklist_writer before finishing. Return only one JSON object with:
            is_request_satisfied:{reason:string,answer:boolean},
            is_in_loop:{reason:string,answer:boolean},
            is_progress_being_made:{reason:string,answer:boolean},
            next_speaker:{reason:string,answer:string},
            instruction_or_question:{reason:string,answer:string},
            final_answer:string.
            When not complete, next_speaker must be title_writer or checklist_writer. When complete, include the final card in final_answer.
            """;
        var history = new List<ChatMessage> { new("user", prompt) };
        var outputs = new List<AgentResponse>();
        var agentsUsed = new HashSet<string>(StringComparer.Ordinal);
        var stalls = 0;
        var resets = 0;

        for (var round = 0; round < 5; round++)
        {
            var managerText = await client.CompleteAsync(
                [new ChatMessage("system", managerInstructions), .. history], cancellationToken);
            var decision = ParseMagenticDecision(managerText);
            if (decision.IsSatisfied)
            {
                if (agentsUsed.Count < 2)
                {
                    history.Add(new ChatMessage("assistant", managerText, "manager"));
                    history.Add(new ChatMessage("user",
                        "The request is not complete until both title_writer and checklist_writer have contributed."));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(decision.FinalAnswer))
                    throw new InvalidOperationException("Magentic manager returned an empty final answer.");
                var final = Response("manager", decision.FinalAnswer);
                return new WorkflowResult([final], outputs);
            }
            if (decision.IsInLoop)
            {
                resets++;
                if (resets > 1)
                    throw new InvalidOperationException("Magentic reset limit (1) exceeded.");
                history = [new ChatMessage("user", prompt)];
                agentsUsed.Clear();
            }
            if (decision.IsProgressBeingMade)
                stalls = 0;
            else if (++stalls > 2)
                throw new InvalidOperationException("Magentic stall limit (2) exceeded.");

            var (name, instructions) = decision.NextSpeaker switch
            {
                "title_writer" => ("title_writer",
                    "Return one short beginner Python workshop title, nothing else."),
                "checklist_writer" => ("checklist_writer",
                    "Return exactly two bring-along items for a Python workshop: laptop and charger."),
                _ => throw new InvalidOperationException(
                    $"Magentic manager selected invalid specialist '{decision.NextSpeaker}'."),
            };
            var response = await AgentAsync(client, name,
                $"{instructions}\nCoordinator request: {decision.Instruction}", history, cancellationToken);
            history.AddRange(response.Messages);
            outputs.Add(response);
            agentsUsed.Add(name);
        }
        throw new InvalidOperationException("Magentic round limit (5) reached without a final answer.");
    }

    private static MagenticDecision ParseMagenticDecision(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(StripJsonFence(text));
            var root = document.RootElement;
            return new MagenticDecision(
                ReadAnswer(root, "is_request_satisfied").GetBoolean(),
                ReadAnswer(root, "is_in_loop").GetBoolean(),
                ReadAnswer(root, "is_progress_being_made").GetBoolean(),
                ReadAnswer(root, "next_speaker").GetString() ?? "",
                ReadAnswer(root, "instruction_or_question").GetString() ?? "",
                root.TryGetProperty("final_answer", out var answer) ? answer.GetString() ?? "" : "");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Magentic manager returned an invalid coordination response.", exception);
        }
    }

    private static JsonElement ReadAnswer(JsonElement root, string property) =>
        root.GetProperty(property).GetProperty("answer");

    private static string StripJsonFence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var start = trimmed.IndexOf('\n');
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start)
                return trimmed[(start + 1)..end].Trim();
        }
        return trimmed;
    }

    private static async Task<AgentResponse> AgentAsync(
        IChatClient client, string name, string instructions, IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage> { new("system", instructions, name) };
        messages.AddRange(history);
        var text = await client.CompleteAsync(messages, cancellationToken);
        return Response(name, text);
    }

    private static AgentResponse Response(string name, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"{name}: expected a nonempty AgentResponse.");
        return new AgentResponse(name, [new ChatMessage("assistant", text, name)]);
    }

    private static (bool Help, string? Error, string Pattern, string? Endpoint, string Deployment)
        ParseArguments(string[] args, string? fixedPattern)
    {
        var pattern = fixedPattern ?? "all";
        var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
                return (true, null, pattern, endpoint, deployment);
            if (arg is "--pattern" or "--project-endpoint" or "--deployment")
            {
                if (++i >= args.Length)
                    return (false, $"Missing value for {arg}.", pattern, endpoint, deployment);
                if (arg == "--pattern")
                {
                    if (fixedPattern is not null && args[i] != fixedPattern)
                        return (false, $"This entry point runs only the '{fixedPattern}' pattern.", pattern, endpoint, deployment);
                    pattern = args[i];
                }
                else if (arg == "--project-endpoint")
                    endpoint = args[i];
                else
                    deployment = args[i];
                continue;
            }
            return (false, $"Unrecognized argument '{arg}'.", pattern, endpoint, deployment);
        }
        if (pattern != "all" && !Patterns.Contains(pattern, StringComparer.Ordinal))
            return (false, $"Invalid pattern '{pattern}'. Choose: all, {string.Join(", ", Patterns)}.", pattern, endpoint, deployment);
        return (false, null, pattern, endpoint, deployment);
    }

    private static void PrintHelp(string? fixedPattern)
    {
        var defaultPattern = fixedPattern ?? "all";
        Console.WriteLine("Run the Microsoft Agent Framework orchestration samples.");
        Console.WriteLine("Usage: dotnet run --file demo.cs -- [--pattern all|sequential|concurrent|group-chat|magentic]");
        if (fixedPattern is not null)
            Console.WriteLine($"This entry point runs: {fixedPattern}");
        Console.WriteLine("Options: --project-endpoint HTTPS_URL  --deployment NAME  --help");
        Console.WriteLine($"Default pattern: {defaultPattern}; timeout per workflow: {WorkflowTimeoutSeconds} seconds.");
        Console.WriteLine("Environment: FOUNDRY_PROJECT_ENDPOINT, AZURE_OPENAI_DEPLOYMENT (default gpt-4.1-mini).");
    }

    private sealed record MagenticDecision(
        bool IsSatisfied, bool IsInLoop, bool IsProgressBeingMade,
        string NextSpeaker, string Instruction, string FinalAnswer);
}
