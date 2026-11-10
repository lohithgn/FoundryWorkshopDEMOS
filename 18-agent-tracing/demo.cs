#!/usr/bin/env dotnet
#:package Azure.Identity@1.21.0
#:package Azure.Monitor.OpenTelemetry.Exporter@1.10.0
#:package OpenTelemetry@1.18.0
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

public static class Demo
{
    public const string SourceName="lab18";
    public static readonly ActivitySource Source=new(SourceName,"1.0");

    public static async Task<JsonObject> PricingToolAsync(string scenario,double delay,
        Func<TimeSpan,CancellationToken,Task>? wait=null,CancellationToken cancellationToken=default)
    {
        if(scenario=="slow")
            await (wait??Task.Delay)(TimeSpan.FromSeconds(delay),cancellationToken);
        if(scenario=="failed")
            throw new InvalidOperationException("Synthetic pricing service failure");
        return new JsonObject { ["workshop"]="intro",["price_usd"]=25 };
    }

    public static async Task<string> RunAgentAsync(Func<JsonObject,CancellationToken,Task<JsonObject>> send,
        string deployment,string scenario,double delay=2.0,
        Func<TimeSpan,CancellationToken,Task>? wait=null,Action<string>? write=null,
        CancellationToken cancellationToken=default)
    {
        var requestId=Guid.NewGuid().ToString("N");
        using var root=Source.StartActivity("workshop-agent",ActivityKind.Internal)
            ??throw new InvalidOperationException("No tracing listener is configured.");
        root.SetTag("gen_ai.operation.name","invoke_agent");
        root.SetTag("gen_ai.agent.name","workshop-pricing");
        root.SetTag("workshop.request_id",requestId);
        root.SetTag("workshop.scenario",scenario);
        var traceId=root.TraceId.ToHexString();
        (write??Console.WriteLine)(new JsonObject
            { ["request_id"]=requestId,["trace_id"]=traceId,["scenario"]=scenario }.ToJsonString());

        JsonObject pricing;
        using(var tool=Source.StartActivity("pricing-tool",ActivityKind.Internal))
        {
            tool?.SetTag("gen_ai.operation.name","execute_tool");
            tool?.SetTag("gen_ai.tool.name","pricing");
            try
            {
                pricing=await PricingToolAsync(scenario,delay,wait,cancellationToken);
                tool?.SetStatus(ActivityStatusCode.Ok);
            }
            catch(InvalidOperationException)
            {
                tool?.SetTag("error.type","SyntheticToolFailure");
                tool?.SetTag("workshop.error_type","SyntheticToolFailure");
                tool?.SetStatus(ActivityStatusCode.Error,"SyntheticToolFailure");
                pricing=new JsonObject { ["error"]="pricing unavailable" };
            }
        }

        using(var model=Source.StartActivity("chat workshop-summary",ActivityKind.Client))
        {
            model?.SetTag("gen_ai.operation.name","chat");
            model?.SetTag("gen_ai.provider.name","azure.ai.openai");
            model?.SetTag("gen_ai.request.model",deployment);
            try
            {
                var response=await send(new JsonObject
                {
                    ["model"]=deployment,["max_tokens"]=100,
                    ["messages"]=new JsonArray(
                        new JsonObject { ["role"]="system",["content"]="Summarize this synthetic pricing result in one sentence." },
                        new JsonObject { ["role"]="user",["content"]=pricing.ToJsonString() })
                },cancellationToken);
                model?.SetTag("gen_ai.response.id",response["id"]?.GetValue<string>());
                if(response["usage"] is JsonObject usage)
                {
                    if(usage["prompt_tokens"] is JsonValue prompt)
                        model?.SetTag("gen_ai.usage.input_tokens",ReadTokenCount(prompt));
                    if(usage["completion_tokens"] is JsonValue completion)
                        model?.SetTag("gen_ai.usage.output_tokens",ReadTokenCount(completion));
                }
                model?.SetStatus(ActivityStatusCode.Ok);
            }
            catch
            {
                model?.SetTag("error.type","ModelRequestFailure");
                model?.SetTag("workshop.error_type","ModelRequestFailure");
                model?.SetStatus(ActivityStatusCode.Error,"ModelRequestFailure");
                root.SetStatus(ActivityStatusCode.Error,"ModelRequestFailure");
                throw new InvalidOperationException($"Model request failed; inspect trace {traceId}");
            }
        }
        root.SetStatus(ActivityStatusCode.Ok);
        root.SetTag("workshop.recovered_tool_failure",scenario=="failed");
        return traceId;
    }

    private static long ReadTokenCount(JsonValue value)=>value.TryGetValue<long>(out var count)
        ?count:value.GetValue<int>();

    public static string? ReadConnectionString(string? configured,string statePath)
    {
        if(!string.IsNullOrEmpty(configured))return configured;
        if(!File.Exists(statePath))return null;
        using var document=JsonDocument.Parse(File.ReadAllText(statePath));
        return document.RootElement.GetProperty("outputs")
            .GetProperty("applicationInsightsConnectionString").GetProperty("value").GetString();
    }

    public static TracerProvider CreateProvider(string connectionString,TokenCredential credential)=>
        Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("lab18-workshop-agent"))
            .AddSource(SourceName)
            .AddAzureMonitorTraceExporter(options =>
            {
                ConfigureExporter(options,connectionString,credential);
            })
            .Build();

    public static void ConfigureExporter(AzureMonitorExporterOptions options,string connectionString,
        TokenCredential credential)
    {
        options.ConnectionString=connectionString;
        options.Credential=credential;
        options.DisableOfflineStorage=true;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try { options=new Options(args); }
        catch (ArgumentException error) { throw new CliException(error.Message); }
        if(options.Help){options.Usage();return 0;}
        var connection=ReadConnectionString(options.ConnectionString,
            Path.Combine(".azure","deployment.json"));
        if(string.IsNullOrWhiteSpace(options.Endpoint)||!options.Endpoint.StartsWith("https://",StringComparison.OrdinalIgnoreCase)||string.IsNullOrWhiteSpace(connection))
            throw new CliException("Set AZURE_OPENAI_ENDPOINT and APPLICATIONINSIGHTS_CONNECTION_STRING (or use deployment state).");
        if(!double.IsFinite(options.SlowSeconds)||options.SlowSeconds<0||options.SlowSeconds>30)
            throw new CliException("--slow-seconds must be between 0 and 30");

        var credential=new DefaultAzureCredential();
        using var provider=CreateProvider(connection,credential);
        using var client=new OpenAiClient(options.Endpoint,credential);
        foreach(var scenario in new[]{"normal","slow","failed"})
            await RunAgentAsync(client.PostAsync,options.Deployment,scenario,options.SlowSeconds);
        if(!provider.ForceFlush(30000))
            throw new InvalidOperationException("Telemetry flush timed out");
        Console.WriteLine("Spans flushed. Verify ingestion in Application Insights; export does not prove ingestion.");
        return 0;
    }

    public sealed class Options
    {
        public string? Endpoint{get;private set;}=Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        public string Deployment{get;private set;}=Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")??"gpt-4.1-mini";
        public string? ConnectionString{get;private set;}=Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
        public double SlowSeconds{get;private set;}=2.0;
        public bool Help{get;}
        public Options(string[] args)
        {
            for(var i=0;i<args.Length;i++)
            {
                var name=args[i];if(name is "-h" or "--help"){Help=true;continue;}
                if(i+1>=args.Length||args[i+1].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException($"Missing value for {name}.");
                var value=args[++i];switch(name)
                {
                    case "--endpoint":Endpoint=value;break;
                    case "--deployment":Deployment=value;break;
                    case "--connection-string":ConnectionString=value;break;
                    case "--slow-seconds":
                        if(!double.TryParse(value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var seconds))
                            throw new ArgumentException("--slow-seconds must be a number.");
                        SlowSeconds=seconds;break;
                    default:throw new ArgumentException($"Unknown argument: {name}");
                }
            }
        }
        public void Usage()=>Console.WriteLine("Usage: dotnet run --file demo.cs -- [--endpoint URL] [--deployment NAME] [--connection-string STRING] [--slow-seconds SECONDS]");
    }
}

public sealed class OpenAiClient:IDisposable
{
    private readonly TokenCredential credential;private readonly HttpClient http=new(){Timeout=Timeout.InfiniteTimeSpan};private readonly Uri baseUri;
    public OpenAiClient(string endpoint,TokenCredential credential){this.credential=credential;baseUri=new Uri(endpoint.TrimEnd('/')+"/");}
    public static Uri ChatUri(string endpoint,string deployment)=>new(new Uri(endpoint.TrimEnd('/')+"/"),
        $"openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version=2024-10-21");
    public async Task<JsonObject> PostAsync(JsonObject body,CancellationToken cancellationToken=default)
    {
        var deployment=body["model"]?.GetValue<string>()??throw new ArgumentException("Model deployment is required.");
        for(var attempt=0;;attempt++)
        {
            try
            {
                var token=await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),cancellationToken);
                using var request=new HttpRequestMessage(HttpMethod.Post,ChatUri(baseUri.ToString(),deployment))
                {Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json")};
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token.Token);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                var text=await response.Content.ReadAsStringAsync(timeout.Token);
                if(!response.IsSuccessStatusCode)throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {text}",null,response.StatusCode);
                return JsonNode.Parse(text)?.AsObject()??throw new JsonException("Service returned empty JSON.");
            }
            catch(HttpRequestException error) when(attempt<2&&!cancellationToken.IsCancellationRequested&&
                (error.StatusCode is null||error.StatusCode is System.Net.HttpStatusCode.RequestTimeout or
                    System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.TooManyRequests||
                 (int)error.StatusCode.Value>=500))
            {await Task.Delay(TimeSpan.FromMilliseconds(500*(attempt+1)),cancellationToken);}
            catch(TaskCanceledException) when(attempt<2&&!cancellationToken.IsCancellationRequested)
            {await Task.Delay(TimeSpan.FromMilliseconds(500*(attempt+1)),cancellationToken);}
        }
    }
    public void Dispose()=>http.Dispose();
}

#if !TEST_HARNESS
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try{return await Demo.RunAsync(args);}
        catch(CliException error){Console.Error.WriteLine($"ArgumentException: {error.Message}");return 2;}
        catch(Exception error){Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");return 1;}
    }
}
#endif

public sealed class CliException(string message) : ArgumentException(message) { }
