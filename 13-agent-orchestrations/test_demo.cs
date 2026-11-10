#!/usr/bin/env dotnet
#:property PublishAot=false
#:include Orchestration.cs

using AgentOrchestrations;
using System.Collections.Concurrent;
using System.Text.Json;

if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
{
    Console.WriteLine("Run the offline orchestration graph tests. Usage: dotnet run --file test_demo.cs");
    return;
}

await OfflineTests.RunAsync();

static class OfflineTests
{
    private static int _passed;

    public static async Task RunAsync()
    {
        await TestAsync("sequential passes the writer response to the editor", SequentialAsync);
        await TestAsync("concurrent branches overlap and receive independent input", ConcurrentAsync);
        await TestAsync("group chat shares context and stops after four replies", GroupChatAsync);
        await TestAsync("magentic coordinates both specialists", MagenticAsync);
        await TestAsync("round-robin selection and output validation", ValidationAsync);
        await TestAsync("Foundry project endpoint validation", EndpointValidationAsync);
        await TestAsync("workflow cancellation propagates", CancellationAsync);
        Console.WriteLine($"PASS: {_passed} offline orchestration tests.");
    }

    private static async Task TestAsync(string name, Func<Task> test)
    {
        await test();
        _passed++;
        Console.WriteLine($"PASS: {name}");
    }

    private static async Task SequentialAsync()
    {
        var client = new LocalChatClient();
        var result = await Demo.RunPatternAsync(client, "sequential", Demo.SequentialPrompt);
        Equal(2, result.Outputs.Count, "sequential outputs");
        Equal(2, client.Calls.Count, "sequential calls");
        True(client.Calls[1].Any(message => message.Role == "assistant" && message.AuthorName == "writer"),
            "editor history includes writer response");
        Demo.Show(result.Outputs, "sequential");
    }

    private static async Task ConcurrentAsync()
    {
        var client = new LocalChatClient();
        var result = await Demo.RunPatternAsync(client, "concurrent", Demo.ConcurrentPrompt);
        Equal(2, result.Outputs.Count, "concurrent outputs");
        Equal(2, client.Calls.Count, "concurrent calls");
        Equal(2, client.MaxActive, "maximum overlapping calls");
        True(client.Calls.All(call => call.All(message => message.Role != "assistant")),
            "concurrent branches have no cross-branch assistant messages");
        Demo.Show(result.Outputs, "concurrent");
    }

    private static async Task GroupChatAsync()
    {
        var client = new LocalChatClient();
        var result = await Demo.RunPatternAsync(client, "group-chat", Demo.GroupChatPrompt);
        Equal(4, result.Outputs.Count, "group-chat outputs");
        Equal(4, client.Calls.Count, "group-chat calls");
        True(client.Calls[^1].Any(message => message.Role == "assistant"),
            "last speaker sees shared discussion");
        Equal("reviewer", Demo.SelectSpeaker(3, ["planner", "reviewer"]), "round-robin speaker");
        Demo.Show(result.Outputs, "group-chat");
    }

    private static async Task MagenticAsync()
    {
        var client = new LocalChatClient();
        var result = await Demo.RunPatternAsync(client, "magentic", Demo.MagenticPrompt);
        Equal(2, client.SpecialistCalls, "specialist calls");
        Equal(1, result.Outputs.Count, "manager final output");
        Equal(2, result.IntermediateOutputs.Count, "specialist intermediate outputs");
        True(result.IntermediateOutputs.Any(output => output.AuthorName == "title_writer"),
            "title specialist called");
        True(result.IntermediateOutputs.Any(output => output.AuthorName == "checklist_writer"),
            "checklist specialist called");
        Demo.Show(result.IntermediateOutputs, "magentic");
        Demo.Show(result.Outputs, "magentic");
    }

    private static Task ValidationAsync()
    {
        Throws<InvalidOperationException>(() => Demo.Show([], "empty"));
        Throws<InvalidOperationException>(() =>
            Demo.Show([new AgentResponse("missing", [])], "empty-text"));
        Throws<ArgumentException>(() => Demo.SelectSpeaker(0, []));
        return Task.CompletedTask;
    }

    private static Task EndpointValidationAsync()
    {
        True(Demo.IsFoundryProjectEndpoint(
            new Uri("https://offline.services.ai.azure.com/api/projects/test")),
            "valid Foundry project endpoint");
        True(!Demo.IsFoundryProjectEndpoint(new Uri("https://example.com/api/projects/test")),
            "foreign host rejected");
        True(!Demo.IsFoundryProjectEndpoint(new Uri("https://offline.services.ai.azure.com/other")),
            "unexpected project path rejected");
        True(!Demo.IsFoundryProjectEndpoint(new Uri("http://offline.services.ai.azure.com/api/projects/test")),
            "non-HTTPS endpoint rejected");
        return Task.CompletedTask;
    }

    private static async Task CancellationAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        await ThrowsAsync<OperationCanceledException>(async () =>
            await Demo.RunPatternAsync(new DelayedChatClient(), "sequential", Demo.SequentialPrompt, cancellation.Token));
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
    }

    private static void True(bool value, string label)
    {
        if (!value)
            throw new InvalidOperationException($"{label}: assertion failed.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

sealed class LocalChatClient : IChatClient
{
    private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> _calls = new();
    private int _active;
    private int _maxActive;
    private int _specialistCalls;
    private int _managerCalls;

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls => _calls.ToArray();
    public int MaxActive => _maxActive;
    public int SpecialistCalls => _specialistCalls;

    public async Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        _calls.Enqueue(messages.ToArray());
        var active = Interlocked.Increment(ref _active);
        UpdateMaximum(active);
        try
        {
            await Task.Delay(20, cancellationToken);
            var instruction = messages.First(message => message.Role == "system").Text;
            if (instruction.StartsWith("Magentic coordinator:", StringComparison.Ordinal))
                return ManagerResponse(Interlocked.Increment(ref _managerCalls));
            if (instruction.Contains("Return one short beginner", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _specialistCalls);
                return "Python Together";
            }
            if (instruction.Contains("Return exactly two bring-along", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _specialistCalls);
                return "Laptop and charger";
            }
            return instruction switch
            {
                "Write a friendly two-line invitation." => "Join us Friday for a free Python workshop.",
                "Shorten the writer's invitation to one sentence. Preserve facts; invent nothing."
                    => "Join us Friday for a free Python workshop.",
                "Suggest exactly one short workshop title." => "Python Together",
                "Suggest exactly one simple vegetarian snack." => "Apple slices",
                _ => "A short workshop agenda.",
            };
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private void UpdateMaximum(int value)
    {
        var current = Volatile.Read(ref _maxActive);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref _maxActive, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static string ManagerResponse(int round) => round switch
    {
        1 => Decision(false, false, true, "title_writer", "Write a short title.", ""),
        2 => Decision(false, false, true, "checklist_writer", "Write the two-item checklist.", ""),
        _ => Decision(true, false, true, "", "", "Python Together — Bring laptop and charger."),
    };

    private static string Decision(
        bool satisfied, bool inLoop, bool progress, string next, string instruction, string final) =>
        JsonSerializer.Serialize(new
        {
            is_request_satisfied = new { reason = "offline test", answer = satisfied },
            is_in_loop = new { reason = "offline test", answer = inLoop },
            is_progress_being_made = new { reason = "offline test", answer = progress },
            next_speaker = new { reason = "offline test", answer = next },
            instruction_or_question = new { reason = "offline test", answer = instruction },
            final_answer = final,
        });
}

sealed class DelayedChatClient : IChatClient
{
    public async Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return "";
    }
}
