#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:package Azure.AI.AgentServer.Invocations@1.0.0-beta.8
#:package Azure.Identity@1.21.0
#:include ../HostedCore.cs

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.AI.AgentServer.Invocations;
using Microsoft.AspNetCore.Http;

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:8088");
Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER",
    Environment.GetEnvironmentVariable("OTEL_TRACES_SAMPLER") ?? "always_on");
Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT",
    Environment.GetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT") ?? "false");
InvocationsServer.Run<HostedInvocationHandler>(args);

public sealed class HostedInvocationHandler : InvocationHandler
{
    static readonly ActivitySource Tracer = new("workshop.hosted");
    static readonly SemaphoreSlim StateLock = new(1, 1);
    const string ModelScope = "https://ai.azure.com/.default";
    const int Price = 25;

    public override async Task HandleAsync(HttpRequest request, HttpResponse response, InvocationContext context, CancellationToken cancellationToken)
    {
        JsonDocument requestJson;
        try
        {
            requestJson = await HostedAgent.HostedCore.ReadBoundedJsonAsync(
                request.Body, request.ContentLength, cancellationToken, allowUndeclaredContentLength: true);
            HostedAgent.HostedCore.ValidateAttendees(requestJson.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            response.StatusCode = 400;
            await response.WriteAsJsonAsync(new { error = ex.Message }, cancellationToken);
            return;
        }
        using var requestDocument = requestJson;
        var attendees = requestJson.RootElement.GetProperty("attendees").GetInt32();
        var userId = context.PlatformContext.UserIdKey;
        if (string.IsNullOrEmpty(userId))
            throw new InvalidOperationException("Foundry did not supply the authenticated user ID; refusing unscoped state.");
        var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is required.");
        var model = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME")
            ?? throw new InvalidOperationException("MODEL_DEPLOYMENT_NAME is required.");
        var credential = new DefaultAzureCredential();
        var token = await credential.GetTokenAsync(new Azure.Core.TokenRequestContext([ModelScope]), cancellationToken);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(240) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        foreach (var header in context.ClientHeaders)
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        if (!string.IsNullOrEmpty(context.PlatformContext.CallId))
            client.DefaultRequestHeaders.Add("x-agent-foundry-call-id", context.PlatformContext.CallId);
        var api = endpoint.TrimEnd('/') + "/openai/v1/responses";
        var proposal = await PostAsync(client, api, new
        {
            model,
            input = $"Obtain the workshop price for exactly {attendees} attendees.",
            tools = new[] { new { type = "function", name = "lookup_price", description = "Read the authoritative workshop price. No booking or payment.", parameters = new { type = "object", properties = new { attendees = new { type = "integer" } }, required = new[] { "attendees" }, additionalProperties = false }, strict = true } },
            tool_choice = new { type = "function", name = "lookup_price" },
            parallel_tool_calls = false,
            max_output_tokens = 600
        }, cancellationToken);
        if (proposal.GetProperty("status").GetString() != "completed")
            throw new InvalidOperationException("The live model did not complete its pricing tool proposal.");
        var calls = proposal.GetProperty("output").EnumerateArray().Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "function_call").ToArray();
        if (calls.Length != 1 || calls[0].GetProperty("name").GetString() != "lookup_price")
            throw new InvalidOperationException("The live model did not produce exactly one pricing tool call.");
        var call = calls[0];
        using var arguments = JsonDocument.Parse(call.GetProperty("arguments").GetString()!);
        HostedAgent.HostedCore.ValidateToolArguments(arguments.RootElement, attendees);
        var evidence = new { attendees, currency = "USD", unit_price = Price, total = attendees * Price, source = "hosted-read-only-price-tool" };
        using (var span = Tracer.StartActivity("execute_tool lookup_price"))
        {
            span?.SetTag("workshop.attendees", attendees);
            span?.SetTag("workshop.total", attendees * Price);
        }
        var answer = await PostAsync(client, api, new
        {
            model,
            previous_response_id = proposal.GetProperty("id").GetString(),
            input = new[] { new { type = "function_call_output", call_id = call.GetProperty("call_id").GetString(), output = JsonSerializer.Serialize(evidence) } },
            instructions = "Return exactly the authoritative tool result fields. Do not book anything.",
            text = new { format = new { type = "json_schema", name = "verified_quote", strict = true, schema = new { type = "object", properties = new { attendees = new { type = "integer" }, currency = new { type = "string" }, unit_price = new { type = "integer" }, total = new { type = "integer" }, source = new { type = "string" } }, required = new[] { "attendees", "currency", "unit_price", "total", "source" }, additionalProperties = false } } },
            max_output_tokens = 600
        }, cancellationToken);
        if (answer.GetProperty("status").GetString() != "completed" || !answer.TryGetProperty("output_text", out var outputText))
            throw new InvalidOperationException($"Incomplete live model response: {answer.GetProperty("status")}");
        using var parsed = JsonDocument.Parse(outputText.GetString()!);
        HostedAgent.HostedCore.ValidateModelQuote(parsed.RootElement, attendees);
        await StateLock.WaitAsync(cancellationToken);
        Dictionary<string, object> state;
        try
        {
            var directory = Environment.GetEnvironmentVariable("WORKSHOP_STATE_DIR") ?? "/files/workshop";
            Directory.CreateDirectory(directory);
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)));
            var path = Path.Combine(directory, key + ".json");
            var visits = 0;
            var marker = Guid.NewGuid().ToString("N");
            if (File.Exists(path))
            {
                using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
                visits = saved.RootElement.GetProperty("visits").GetInt32();
                marker = saved.RootElement.GetProperty("marker").GetString()!;
            }
            state = new() { ["visits"] = visits + 1, ["marker"] = marker };
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(state), cancellationToken);
            File.Move(tmp, path, true);
        }
        finally { StateLock.Release(); }
        var spanContext = Activity.Current?.TraceId.ToString()
            ?? throw new InvalidOperationException("Invocation tracing did not create an activity.");
        response.ContentType = "application/json";
        await response.WriteAsJsonAsync(new
        {
            quote = parsed.RootElement.Clone(), tool_result = evidence,
            tool_call_id = call.GetProperty("call_id").GetString(),
            proposal_response_id = proposal.GetProperty("id").GetString(),
            response_id = answer.GetProperty("id").GetString(),
            usage = answer.TryGetProperty("usage", out var usage) ? usage.Clone() : (JsonElement?)null,
            state, instance = Environment.MachineName,
            agent_name = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_NAME"),
            agent_version = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_VERSION"),
            trace_id = spanContext, session_id = context.SessionId
        }, cancellationToken);
    }

    static async Task<JsonElement> PostAsync(HttpClient client, string url, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var result = await client.PostAsync(url, content, ct);
        result.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await result.Content.ReadAsStringAsync(ct));
        return document.RootElement.Clone();
    }
}
