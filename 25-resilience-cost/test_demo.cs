#!/usr/bin/env dotnet
#:package Azure.Identity@1.21.0
#:include demo.cs
#:property StartupObject=ResilienceTests

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;

public sealed class ResilienceTests
{
    private sealed class FixedCredential : TokenCredential
    {
        private readonly object gate = new();
        public readonly List<string> Scopes = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            lock (gate) Scopes.AddRange(requestContext.Scopes);
            return new AccessToken("offline-token", DateTimeOffset.UtcNow.AddMinutes(5));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class SuccessfulHandler : HttpMessageHandler
    {
        public int Calls;
        public string? Authorization;
        public string? Body;
        private HttpResponseMessage CreateResponse(HttpRequestMessage request)
        {
            Interlocked.Increment(ref Calls);
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"model":"deployment","choices":[{"finish_reason":"stop","message":{"content":"A short workshop."}}],"usage":{"prompt_tokens":12,"completion_tokens":5}}""",
                    Encoding.UTF8, "application/json")
            };
        }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            CreateResponse(request);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(CreateResponse(request));
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected {expected}, got {actual}");
    }
    private static void True(bool value, string label)
    {
        if (!value) throw new Exception(label);
    }
    private static T Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); } catch (T ex) { return ex; }
        throw new Exception($"{label}: expected {typeof(T).Name}");
    }

    private static Task TestThrottleRecovery()
    {
        var budget = new Budget(100);
        var calls = 0;
        var delays = new List<double>();
        var result = Resilience.Bounded(_ => ++calls == 1 ? throw Resilience.SyntheticStatus(429, "2") : "ok",
            budget, "model", sleep: delays.Add, clock: () => 0);
        Equal("ok", result, "recovered result");
        Equal(2, budget.ModelRequests, "reserved requests");
        Equal(2d, delays.Single(), "Retry-After seconds");
        return Task.CompletedTask;
    }

    private static Task TestPermanentFailure()
    {
        var calls = 0;
        var error = Throws<ApiStatusError>(() =>
            Resilience.Bounded<string>(_ => { calls++; throw Resilience.SyntheticStatus(403); },
                new Budget(100), "model", sleep: _ => { }, clock: () => 0), "403 propagation");
        Equal(403, error.StatusCode, "status code");
        Equal(1, calls, "permanent failure attempt count");
        return Task.CompletedTask;
    }

    private static Task TestRequestBudgetAndDeadline()
    {
        var calls = 0;
        Throws<BudgetExceeded>(() => Resilience.Bounded<string>(_ => { calls++; throw new TimeoutException(); },
            new Budget(100, modelLimit: 1), "model", sleep: _ => { }, clock: () => 0), "budget exhausted");
        Equal(1, calls, "reserve before second call");
        calls = 0;
        Throws<BudgetExceeded>(() => Resilience.Bounded<string>(_ => { calls++; return "bad"; },
            new Budget(100), "model", clock: () => 101), "expired deadline");
        Equal(0, calls, "no call after deadline");
        return Task.CompletedTask;
    }

    private static Task TestRetryAfterDeadline()
    {
        var calls = 0;
        Throws<BudgetExceeded>(() => Resilience.Bounded<string>(_ => { calls++; throw Resilience.SyntheticStatus(429, "200"); },
            new Budget(100), "model", sleep: _ => { }, clock: () => 0), "retry delay exceeds deadline");
        Equal(1, calls, "no retry beyond deadline");
        return Task.CompletedTask;
    }

    private static Task TestHttpDateAndInvalidHeader()
    {
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        Equal(3d, Resilience.RetryDelay(Resilience.SyntheticStatus(429, "Sun, 25 Oct 2026 12:00:03 GMT"), 0, now),
            "HTTP-date Retry-After");
        Throws<FormatException>(() => Resilience.RetryDelay(Resilience.SyntheticStatus(429, "NaN"), 0),
            "invalid Retry-After");
        return Task.CompletedTask;
    }

    private static Task TestIdempotentCommittedWrite()
    {
        var tool = new SyntheticBookingTool();
        var result = Resilience.Bounded(timeout => tool.Book("one-key", timeout), new Budget(100), "tool",
            sleep: _ => { }, clock: () => 0);
        Equal("synthetic-booking-1", result, "recovered booking");
        Equal(1, tool.Bookings.Count, "no duplicate write");
        return Task.CompletedTask;
    }

    private static Task TestAttemptCapPropagatesOutage()
    {
        var calls = 0;
        Throws<IOException>(() => Resilience.Bounded<string>(_ => { calls++; throw new IOException("offline"); },
            new Budget(100), "tool", attempts: 2, sleep: _ => { }, clock: () => 0), "attempt cap");
        Equal(2, calls, "exact retry limit");
        return Task.CompletedTask;
    }

    private static Task TestRealRequestShapeWithOfflineTransport()
    {
        var handler = new SuccessfulHandler();
        using var http = new HttpClient(handler);
        var credential = new FixedCredential();
        var budget = new Budget(Resilience.Now() + 90);
        var usage = Resilience.ModelRequest(http, credential, "https://model.example/", "deployment", "timeout", budget);
        Equal(1, handler.Calls, "one actual request after injected timeout");
        Equal("Bearer " + "offline-token", handler.Authorization, "Entra bearer");
        Equal("https://cognitiveservices.azure.com/.default", credential.Scopes.Single(), "Azure OpenAI audience");
        Equal("deployment", (string?)JsonNode.Parse(handler.Body!)!["model"], "deployment field");
        Equal(12, usage.PromptTokens, "service-reported prompt usage");
        Equal(5, usage.CompletionTokens, "service-reported completion usage");
        Equal(2, budget.ModelRequests, "injected and actual request reservations");
        return Task.CompletedTask;
    }

    private static async Task TestDefaultSyntheticFailurePaths()
    {
        var handler = new SuccessfulHandler();
        using var http = new HttpClient(handler);
        var credential = new FixedCredential();
        var budget = new Budget(Resilience.Now() + 90);
        var modelTasks = new[] { "throttle", "timeout" }.Select(scenario => Task.Run(() =>
            Resilience.ModelRequest(http, credential, "https://model.example", "deployment", scenario, budget))).ToArray();
        var usages = await Task.WhenAll(modelTasks);
        Equal(2, handler.Calls, "concurrent model responses");
        Equal(4, budget.ModelRequests, "two synthetic and two successful model requests");
        Equal(24, usages.Sum(u => u.PromptTokens), "actual prompt token total");
        Equal(10, usages.Sum(u => u.CompletionTokens), "actual completion token total");
        var tool = new SyntheticBookingTool();
        var booking = Resilience.Bounded(timeout => tool.Book("request-1", timeout), budget, "tool",
            sleep: _ => { }, clock: () => 0);
        Equal("synthetic-booking-1", booking, "recovered local booking");
        try
        {
            Resilience.Bounded<string>(_ => throw new IOException("Injected synthetic tool outage."),
                budget, "tool", sleep: _ => { }, clock: () => 0);
            throw new Exception("persistent outage should exhaust the tool budget");
        }
        catch (BudgetExceeded) { }
        var error = Throws<ApiStatusError>(() => Resilience.Bounded<string>(_ => throw Resilience.SyntheticStatus(403),
            budget, "model", sleep: _ => { }, clock: () => 0), "permanent failure");
        Equal(403, error.StatusCode, "permanent status");
        Throws<BudgetExceeded>(() => Resilience.Bounded<string>(_ => "unused",
            new Budget(99), "model", clock: () => 100), "expired budget");
        Equal(4, budget.ToolCalls, "same-key booking retry and bounded outage");
        Equal(5, budget.ModelRequests, "permanent HTTP failure is the only fifth attempt");
        True(Math.Abs(.0000256 - Resilience.EstimatedCost(24, 10, .4, 1.6)) < 1e-12, "illustrative model cost");
    }

    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("throttle honors Retry-After", TestThrottleRecovery),
            ("permanent failures are not retried", TestPermanentFailure),
            ("request budgets and deadline", TestRequestBudgetAndDeadline),
            ("retry delay cannot exceed deadline", TestRetryAfterDeadline),
            ("HTTP-date and invalid Retry-After", TestHttpDateAndInvalidHeader),
            ("idempotent committed write", TestIdempotentCommittedWrite),
            ("bounded outage attempts", TestAttemptCapPropagatesOutage),
            ("authenticated inference request shape", TestRealRequestShapeWithOfflineTransport),
            ("synthetic recovery and failure paths", TestDefaultSyntheticFailurePaths)
        };
        foreach (var test in tests)
        {
            await test.Run();
            Console.WriteLine($"PASS {test.Name}");
        }
        Console.WriteLine($"Passed {tests.Length} offline resilience tests.");
        return 0;
    }
}
