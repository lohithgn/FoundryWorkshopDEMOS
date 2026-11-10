#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property ExperimentalFileBasedProgramEnableRefDirective=true
#:include MonitoringCore.cs
#:ref demo.cs

using System.Text.Json;

await MonitoringUi.RunAsync(args);

static class MonitoringUi
{
    static readonly string Root = Environment.CurrentDirectory;
    static readonly SemaphoreSlim Operation = new(1, 1);
    static bool Allowed(HttpContext context) => ProductionMonitoring.MonitoringCore.IsLoopbackRequest(
        context.Request.Headers.Host.ToString(), context.Request.Headers.Origin.ToString(), context.Connection.LocalPort);

    public static async Task RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file ui.cs -- [--port 8037]");
            return;
        }
        var port = 8037;
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
            await context.Response.WriteAsJsonAsync(new { configuration = Read("agents.json"), last_live_test = Read("live-report.json") });
        });
        app.MapGet("/api/evaluations", async context =>
        {
            try { await context.Response.WriteAsJsonAsync(new { evaluations = await MonitoringDemo.EvaluationResultsAsync(CancellationToken.None) }); }
            catch (Exception ex) { context.Response.StatusCode = 502; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
        });
        app.MapGet("/api/traces", async context =>
        {
            try { await context.Response.WriteAsJsonAsync(await MonitoringDemo.TelemetryAsync(null, CancellationToken.None)); }
            catch (Exception ex) { context.Response.StatusCode = 502; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
        });
        app.MapPost("/api/traffic", async context =>
        {
            if (!await Operation.WaitAsync(0))
            {
                context.Response.StatusCode = 409;
                await context.Response.WriteAsJsonAsync(new { error = "Another scenario is running" });
                return;
            }
            try
            {
                string? mode;
                try
                {
                    using var json = await ProductionMonitoring.MonitoringCore.ReadBoundedJsonAsync(
                        context.Request.Body, context.Request.ContentLength, context.RequestAborted);
                    ProductionMonitoring.MonitoringCore.ValidateObjectRoot(json.RootElement);
                    mode = json.RootElement.TryGetProperty("mode", out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null;
                    ProductionMonitoring.MonitoringCore.ValidateMode(mode);
                }
                catch (Exception ex) when (ex is ArgumentException or JsonException)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { error = ex.Message });
                    return;
                }
                try
                {
                    var requests = await MonitoringDemo.TrafficAsync(mode!, CancellationToken.None);
                    await context.Response.WriteAsJsonAsync(new { requests });
                }
                catch (Exception ex)
                {
                    var error = ProductionMonitoring.MonitoringCore.ServiceErrorResponse(ex);
                    context.Response.StatusCode = error.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(error.Body);
                }
            }
            finally { Operation.Release(); }
        });
        Console.WriteLine($"Lab 37 UI: http://127.0.0.1:{port}");
        try { await app.RunAsync(); }
        finally { await MonitoringDemo.SetModeAsync("recovery", CancellationToken.None); }
    }

}
