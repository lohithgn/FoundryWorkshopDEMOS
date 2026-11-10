#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:include HostedCore.cs
#:package Azure.Identity@1.21.0

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;

await HostedUi.RunAsync(args);

static class HostedUi
{
    static readonly string Root = Environment.CurrentDirectory;
    static async Task<string> InvokeAsync(JsonElement data)
    {
        var attendees = HostedAgent.HostedCore.ValidateAttendees(data);
        var session = data.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null;
        HostedAgent.HostedCore.ValidateSession(session);
        var deployment = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Root, ".azure", "deployment.json")));
        var outs = deployment.RootElement.GetProperty("outputs");
        var endpoint = outs.GetProperty("projectEndpoint").GetProperty("value").GetString()!.TrimEnd('/');
        var token = await new AzureCliCredential().GetTokenAsync(new Azure.Core.TokenRequestContext(["https://ai.azure.com/.default"]));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(240) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var content = new StringContent(JsonSerializer.Serialize(new { attendees }), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"{endpoint}/agents/hosted-workshop-pricer/endpoint/protocols/invocations?api-version=v1&agent_session_id={Uri.EscapeDataString(session!)}", content);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement;
        var quote = result.GetProperty("quote");
        if (quote.GetProperty("attendees").GetInt32() != attendees
            || quote.GetProperty("currency").GetString() != "USD"
            || quote.GetProperty("unit_price").GetInt32() != 25
            || quote.GetProperty("total").GetInt32() != attendees * 25
            || quote.GetProperty("source").GetString() != "hosted-read-only-price-tool"
            || !result.TryGetProperty("response_id", out _)
            || !result.TryGetProperty("tool_call_id", out _))
            throw new InvalidOperationException("Hosted response failed independent validation.");
        return result.GetRawText();
    }

    static bool Allowed(HttpContext context)
    {
        var port = context.Connection.LocalPort;
        var hosts = new[] { $"127.0.0.1:{port}", $"localhost:{port}" };
        var host = context.Request.Headers.Host.ToString();
        var origin = context.Request.Headers.Origin.ToString();
        return hosts.Contains(host, StringComparer.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(origin) || hosts.Any(x => string.Equals(origin, "http://" + x, StringComparison.OrdinalIgnoreCase)));
    }

    public static async Task RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file ui.cs -- [--port 8036]");
            return;
        }
        var port = 8036;
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var parsed) && parsed is > 0 and < 65536) port = parsed;
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
            if (!Allowed(context))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "Loopback origin required" });
                return;
            }
            await next();
        });
        app.MapGet("/", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(Path.Combine(Root, "index.html"));
        });
        app.MapGet("/api/status", async context =>
        {
            var state = Path.Combine(Root, ".azure");
            object? Read(string name) => File.Exists(Path.Combine(state, name)) ? JsonDocument.Parse(File.ReadAllText(Path.Combine(state, name))).RootElement.Clone() : null;
            await context.Response.WriteAsJsonAsync(new { agent = Read("agent.json"), last_live_test = Read("live-report.json") });
        });
        app.MapPost("/api/invoke", async context =>
        {
            try
            {
                using var json = await HostedAgent.HostedCore.ReadBoundedJsonAsync(
                    context.Request.Body, context.Request.ContentLength, context.RequestAborted);
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a JSON object");
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(await InvokeAsync(json.RootElement));
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });
        Console.WriteLine($"Lab 36 UI: http://127.0.0.1:{port}");
        await app.RunAsync();
    }
}
