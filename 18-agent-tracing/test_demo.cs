#!/usr/bin/env dotnet
#:property DefineConstants=TEST_HARNESS
#:include demo.cs
using System.Diagnostics;
using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;

public static class Program
{
    private static int assertions;
    private static void Equal<T>(T expected,T actual){assertions++;if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"Expected {expected}, got {actual}.");}
    private static void True(bool value)=>Equal(true,value);

    public static async Task<int> Main()
    {
        var activities=new List<Activity>();
        using var listener=new ActivityListener
        {
            ShouldListenTo=source=>source.Name==Demo.SourceName,
            Sample=(ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped=activity=>activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        JsonObject? captured=null;
        Task<JsonObject> Send(JsonObject body,CancellationToken _)
        {captured=body;return Task.FromResult(new JsonObject{["id"]="response-123",
            ["usage"]=new JsonObject{["prompt_tokens"]=8,["completion_tokens"]=4}});}
        var delays=0;
        await Demo.RunAgentAsync(Send,"deployed-model","slow",3,
            (_,_)=>{delays++;return Task.CompletedTask;},_=>{});
        Equal(1,delays);
        var root=activities.Single(item=>item.OperationName=="workshop-agent");
        Equal(3,activities.Count);
        True(activities.All(item=>item.TraceId==root.TraceId));
        True(activities.Where(item=>item!=root).All(item=>item.ParentSpanId==root.SpanId));
        var model=activities.Single(item=>item.OperationName=="chat workshop-summary");
        Equal(8L,model.GetTagItem("gen_ai.usage.input_tokens"));
        Equal(4L,model.GetTagItem("gen_ai.usage.output_tokens"));
        Equal("deployed-model",captured!["model"]!.GetValue<string>());
        True(!captured["messages"]![1]!["content"]!.GetValue<string>().Contains("SECRET",StringComparison.Ordinal));

        activities.Clear();
        await Demo.RunAgentAsync(Send,"model","failed",0,(_,_)=>Task.CompletedTask,_=>{});
        var failedTool=activities.Single(item=>item.OperationName=="pricing-tool");
        var recoveredModel=activities.Single(item=>item.OperationName=="chat workshop-summary");
        var recoveredRoot=activities.Single(item=>item.OperationName=="workshop-agent");
        Equal(ActivityStatusCode.Error,failedTool.Status);
        Equal("SyntheticToolFailure",failedTool.GetTagItem("error.type"));
        Equal("SyntheticToolFailure",failedTool.GetTagItem("workshop.error_type"));
        Equal(ActivityStatusCode.Ok,recoveredModel.Status);
        Equal(true,recoveredRoot.GetTagItem("workshop.recovered_tool_failure"));

        activities.Clear();
        Task<JsonObject> Fail(JsonObject _,CancellationToken __)=>Task.FromException<JsonObject>(new InvalidOperationException("SECRET-TOKEN private prompt"));
        try{await Demo.RunAgentAsync(Fail,"model","normal",0,(_,_)=>Task.CompletedTask,_=>{});throw new InvalidOperationException("Model failure was hidden.");}
        catch(InvalidOperationException error) when(error.Message.StartsWith("Model request failed",StringComparison.Ordinal)){assertions++;}
        True(!activities.SelectMany(item=>item.TagObjects).Any(tag=>tag.Value?.ToString()?.Contains("SECRET-TOKEN",StringComparison.Ordinal)==true));
        True(activities.All(item=>!item.Events.Any()));
        True(activities.All(item=>!item.TagObjects.Any(tag=>tag.Key.Contains("content",StringComparison.OrdinalIgnoreCase)||
            tag.Key.Contains("authorization",StringComparison.OrdinalIgnoreCase))));
        Equal(ActivityStatusCode.Error,activities.Single(item=>item.OperationName=="workshop-agent").Status);
        Equal("ModelRequestFailure",activities.Single(item=>item.OperationName=="chat workshop-summary").GetTagItem("workshop.error_type"));

        Equal("configured",Demo.ReadConnectionString("configured","missing"));
        var requestUri=OpenAiClient.ChatUri("https://model.example/","deployed model");
        Equal("/openai/deployments/deployed%20model/chat/completions",requestUri.AbsolutePath);
        Equal("?api-version=2024-10-21",requestUri.Query);
        var state=Path.Combine(Path.GetTempPath(),"lab18-state-"+Guid.NewGuid().ToString("N")+".json");
        await File.WriteAllTextAsync(state,"""{"outputs":{"applicationInsightsConnectionString":{"value":"from-state"}}}""");
        try{Equal("from-state",Demo.ReadConnectionString(null,state));}
        finally{File.Delete(state);}
        var credential=new AzureCliCredential();
        var exporterOptions=new AzureMonitorExporterOptions();
        Demo.ConfigureExporter(exporterOptions,"synthetic",credential);
        Equal("synthetic",exporterOptions.ConnectionString);
        Equal(true,exporterOptions.DisableOfflineStorage);
        True(ReferenceEquals(credential,exporterOptions.Credential));
        var options=new Demo.Options(["--endpoint","https://model.example","--connection-string","synthetic"]);
        Equal(2.0,options.SlowSeconds);
        foreach(var value in new[]{"-1","nan","31"})
        {
            try
            {
                var bad=new Demo.Options(["--endpoint","https://model.example","--connection-string","x","--slow-seconds",value]);
                if(double.IsFinite(bad.SlowSeconds)&&bad.SlowSeconds>=0&&bad.SlowSeconds<=30)continue;
                throw new ArgumentException();
            }
            catch(ArgumentException){assertions++;}
        }
        Console.WriteLine($"18-agent-tracing: {assertions} offline assertions passed.");
        return 0;
    }
}
