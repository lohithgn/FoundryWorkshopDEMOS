#!/usr/bin/env dotnet
#:package Azure.Identity@1.21.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

public sealed class BudgetExceeded(string message) : Exception(message);

public sealed class ApiStatusError(int statusCode, string? retryAfter, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? RetryAfter { get; } = retryAfter;
}

public sealed class Budget
{
    private readonly object gate = new();
    public double Deadline { get; }
    public int ModelLimit { get; }
    public int ToolLimit { get; }
    public int ModelRequests { get; private set; }
    public int ToolCalls { get; private set; }

    public Budget(double deadline, int modelLimit = 6, int toolLimit = 4)
    {
        Deadline = deadline;
        ModelLimit = modelLimit;
        ToolLimit = toolLimit;
    }

    public double Reserve(string kind, Func<double>? clock = null)
    {
        lock (gate)
        {
            var remaining = Deadline - (clock ?? Resilience.Now)();
            if (remaining <= 0) throw new BudgetExceeded("Overall execution deadline reached.");
            if (kind == "model")
            {
                if (ModelRequests >= ModelLimit) throw new BudgetExceeded("Model-request budget reached.");
                ModelRequests++;
            }
            else if (kind == "tool")
            {
                if (ToolCalls >= ToolLimit) throw new BudgetExceeded("Tool-call budget reached.");
                ToolCalls++;
            }
            else throw new ArgumentException("Unknown budget kind.");
            return Math.Min(remaining, 20.0);
        }
    }
}

public sealed class SyntheticBookingTool
{
    public Dictionary<string, string> Bookings { get; } = new();
    private bool responseLost;

    public string Book(string key, double timeout)
    {
        if (timeout <= 0 || string.IsNullOrEmpty(key))
            throw new ArgumentException("A deadline and idempotency key are required.");
        if (!Bookings.ContainsKey(key)) Bookings[key] = "synthetic-booking-1";
        if (!responseLost)
        {
            responseLost = true;
            throw new TimeoutException("Injected lost tool response AFTER committing the booking.");
        }
        return Bookings[key];
    }
}

public static class Resilience
{
    public sealed record Usage(int PromptTokens, int CompletionTokens);
    public static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public static double EstimatedCost(int inputTokens, int outputTokens, double inputRate, double outputRate) =>
        (inputTokens * inputRate + outputTokens * outputRate) / 1_000_000;

    public static double RetryDelay(Exception error, int attempt, DateTimeOffset? now = null)
    {
        if (error is ApiStatusError status && !string.IsNullOrEmpty(status.RetryAfter))
        {
            double delay;
            if (!double.TryParse(status.RetryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out delay))
            {
                if (!DateTimeOffset.TryParse(status.RetryAfter, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var retryAt))
                    throw new FormatException("Invalid Retry-After header.");
                delay = (retryAt - (now ?? DateTimeOffset.UtcNow)).TotalSeconds;
            }
            if (!double.IsFinite(delay) || delay < 0) throw new FormatException("Invalid Retry-After header.");
            return delay;
        }
        return Math.Min(0.25 * Math.Pow(2, attempt), 4.0);
    }

    public static T Bounded<T>(Func<double, T> operation, Budget budget, string kind, int attempts = 3,
        Action<double>? sleep = null, Func<double>? clock = null)
    {
        if (attempts < 1) throw new ArgumentException("At least one attempt is required.");
        clock ??= Now;
        sleep ??= seconds => Thread.Sleep(TimeSpan.FromSeconds(seconds));
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var timeout = budget.Reserve(kind, clock);
            try { return operation(timeout); }
            catch (Exception error) when (IsRetryable(error))
            {
                var transient = error is not ApiStatusError status ||
                    status.StatusCode is 408 or 429 or 500 or 502 or 503 or 504;
                if (!transient || attempt == attempts - 1) throw;
                var delay = RetryDelay(error, attempt);
                if (delay >= budget.Deadline - clock())
                    throw new BudgetExceeded("Retry-After/backoff exceeds the remaining deadline.") { Source = error.GetType().Name };
                Console.WriteLine($"RETRY {kind}: {error.GetType().Name}; wait {delay:F2}s");
                sleep(delay);
            }
        }
        throw new InvalidOperationException("Unreachable retry state.");
    }

    private static bool IsRetryable(Exception ex) =>
        ex is ApiStatusError or TimeoutException or OperationCanceledException or HttpRequestException or IOException;

    public static ApiStatusError SyntheticStatus(int code, string? retryAfter = null) =>
        new(code, retryAfter, $"Explicitly injected HTTP {code}; no Azure quota consumed.");

    public static Usage ModelRequest(HttpClient http, TokenCredential credential, string endpoint, string deployment,
        string scenario, Budget budget)
    {
        var injected = true;
        return Bounded(timeout =>
        {
            if (injected)
            {
                injected = false;
                if (scenario == "throttle") throw SyntheticStatus(429, "0.1");
                throw new TimeoutException("Injected model timeout.");
            }
            using var request = new HttpRequestMessage(HttpMethod.Post,
                endpoint.TrimEnd('/') + "/openai/v1/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                credential.GetToken(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), CancellationToken.None).Token);
            request.Content = new StringContent(new JsonObject
            {
                ["model"] = deployment,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Explain a workshop in one short sentence." }),
                ["max_completion_tokens"] = 80
            }.ToJsonString(), Encoding.UTF8, "application/json");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            using var response = http.Send(request, cancellation.Token);
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new ApiStatusError((int)response.StatusCode,
                    response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                        ?? response.Headers.RetryAfter?.Date?.ToString("R", CultureInfo.InvariantCulture),
                    $"HTTP {(int)response.StatusCode}: {text}");
            var data = JsonNode.Parse(text)!;
            var choice = data["choices"]?[0];
            var answer = (string?)choice?["message"]?["content"];
            if (choice is null || (string?)choice["finish_reason"] != "stop" ||
                string.IsNullOrEmpty(answer) || data["usage"] is null)
                throw new InvalidOperationException("Expected a complete answer and real token usage.");
            Console.WriteLine($"{scenario}: {answer}");
            return new Usage((int)data["usage"]!["prompt_tokens"]!, (int)data["usage"]!["completion_tokens"]!);
        }, budget, "model");
    }
}

public static class ResilienceApp
{
    public static int Main(string[] args)
    {
        args = ExpandEquals(args);
        string? endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
        var inputRate = .40;
        var outputRate = 1.60;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                Console.WriteLine("Bound retries, deadlines, concurrency, and costs without exhausting Azure quota.\n--endpoint HTTPS_URL --deployment NAME --input-rate USD --output-rate USD");
                return 0;
            }
            if (args[i] is "--endpoint" or "--deployment" or "--input-rate" or "--output-rate")
            {
                if (++i >= args.Length) return UsageError($"argument {args[i - 1]}: expected one argument");
                var option = args[i - 1];
                if (option == "--endpoint") endpoint = args[i];
                else if (option == "--deployment") deployment = args[i];
                else if (!double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                    return UsageError($"argument {option}: invalid float value: {args[i]}");
                else if (option == "--input-rate") inputRate = rate;
                else outputRate = rate;
            }
            else return UsageError($"unrecognized argument: {args[i]}");
        }
        if (string.IsNullOrEmpty(endpoint) || !endpoint.StartsWith("https://", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(deployment))
            return UsageError("Set an HTTPS model endpoint and nonempty deployment.");
        if (!double.IsFinite(inputRate) || inputRate < 0 || !double.IsFinite(outputRate) || outputRate < 0)
            return UsageError("Prices must be finite and nonnegative.");

        try
        {
            var budget = new Budget(Resilience.Now() + 90);
            var credential = new AzureCliCredential();
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var jobs = new[] { "throttle", "timeout" }.Select(scenario =>
                Task.Run(() => Resilience.ModelRequest(http, credential, endpoint, deployment, scenario, budget))).ToArray();
            var usages = Task.WhenAll(jobs).GetAwaiter().GetResult();
            var tool = new SyntheticBookingTool();
            var booking = Resilience.Bounded(timeout => tool.Book("request-1", timeout), budget, "tool");
            Console.WriteLine($"Recovered write: {booking}; bookings={tool.Bookings.Count} (must be 1)");
            if (tool.Bookings.Count != 1) throw new InvalidOperationException("Duplicate booking created.");
            try
            {
                Resilience.Bounded<string?>(_ => throw new IOException("Injected synthetic tool outage."), budget, "tool");
            }
            catch (BudgetExceeded ex)
            {
                Console.WriteLine($"EXPECTED STOP: {ex.Message}; persistent outage did not loop indefinitely.");
            }
            try
            {
                Resilience.Bounded<string?>(_ => throw Resilience.SyntheticStatus(403), budget, "model");
            }
            catch (ApiStatusError ex) when (ex.StatusCode == 403)
            {
                Console.WriteLine("EXPECTED PERMANENT FAILURE: HTTP 403 propagated without retry.");
            }
            try { Resilience.Bounded<string?>(_ => null, new Budget(Resilience.Now() - 1), "model"); }
            catch (BudgetExceeded ex) { Console.WriteLine($"EXPECTED DEADLINE STOP: {ex.Message}"); }
            var promptTokens = usages.Sum(u => u.PromptTokens);
            var completionTokens = usages.Sum(u => u.CompletionTokens);
            var estimate = Resilience.EstimatedCost(promptTokens, completionTokens, inputRate, outputRate);
            Console.WriteLine($"Attempts: model={budget.ModelRequests}/{budget.ModelLimit}; tool={budget.ToolCalls}/{budget.ToolLimit}; concurrency <= 2");
            Console.WriteLine($"Actual successful usage: input={promptTokens}; output={completionTokens}");
            Console.WriteLine($"ILLUSTRATIVE estimate: USD {estimate:F6}; assumed per-million rates input={inputRate}, output={outputRate}. Not a billing report.");
            Console.WriteLine("Injected faults never reached Azure. Budget alerts are not hard spending caps.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}"); return 1; }
    }

    private static int UsageError(string text)
    {
        Console.Error.WriteLine(text);
        return 2;
    }

    private static string[] ExpandEquals(string[] args) => args.SelectMany(argument =>
    {
        var equals = argument.StartsWith("--", StringComparison.Ordinal) ? argument.IndexOf('=') : -1;
        return equals > 0 ? new[] { argument[..equals], argument[(equals + 1)..] } : new[] { argument };
    }).ToArray();
}
