#!/usr/bin/env dotnet
#:property DefineConstants=TEST_HARNESS
#:include demo.cs
using System.Text.Json.Nodes;
public static class Program
{
    private static int assertions;
    private static void Equal<T>(T expected,T actual)
    {
        assertions++;
        if(!EqualityComparer<T>.Default.Equals(expected,actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private static void True(bool value)=>Equal(true,value);

    public static async Task<int> Main()
    {
        foreach(var ticket in Demo.Cases)
            True(Demo.Score(ticket.Expected.ToJsonString(),ticket.Expected).Correct);
        var shape=Demo.Score("""{"category":"billing","priority":"low"}""",
            new JsonObject{["category"]="outage",["priority"]="high"});
        True(shape.SchemaOk); True(!shape.Correct);
        foreach(var invalid in new[]{"not json","{}","""{"category":"access","priority":"low","extra":1}""",
                                     """{"category":true,"priority":"low"}"""})
            True(!Demo.Score(invalid,new JsonObject()).SchemaOk);
        True(!Demo.Score("""{"category":null,"priority":null}""",new JsonObject(),
            refusal:"no").Correct);
        True(!Demo.Score("""{"category":null,"priority":null}""",new JsonObject(),
            finishReason:"length").Correct);

        Equal<double?>(4,Demo.Percentile([4,1,3,2],.95));
        Equal<double?>(null,Demo.NearestRankPercentile([], .95));
        Equal<double?>(null,Demo.EstimateCost(new Usage(10,2),null));
        Equal(.0002,Demo.EstimateCost(new Usage(100,50),[1,2]));
        var failed=await Demo.RunOneAsync((_,_)=>throw new TimeoutException("offline timeout fixture"),
            "alias",Demo.Cases[0],[1,2]);
        True(failed.TransportError!.Contains("TimeoutException",StringComparison.Ordinal));
        var summary=Demo.Summary([failed]);
        Equal(0.0,summary["accuracy"]!.GetValue<double>());
        Equal(1,summary["transport_failures"]!.GetValue<int>());
        Equal<JsonNode?>(null,summary["estimated_usd"]);
        Equal<JsonNode?>(null,summary["latency_successful_http_seconds"]!["p50"]);
        Equal(1,Demo.ExitCode([failed]));
        var successful=await Demo.RunOneAsync((_,_)=>Task.FromResult(new JsonObject
            { ["model"]="serving-model",["choices"]=new JsonArray(new JsonObject
                { ["finish_reason"]="stop",["message"]=new JsonObject{["content"]="{\"category\":\"access\",\"priority\":\"low\"}"} }),
              ["usage"]=new JsonObject{["prompt_tokens"]=10,["completion_tokens"]=2} }),
            "alias",Demo.Cases[0],[1,2]);
        Equal(0,Demo.ExitCode([failed,successful]));
        var successRow=Demo.RecordJson(("mini","alias",1,Demo.Cases[0],successful));
        True(successRow.ContainsKey("prediction"));True(successRow.ContainsKey("raw"));
        True(!successRow.ContainsKey("transport_error"));
        var failedRow=Demo.RecordJson(("mini","alias",1,Demo.Cases[0],failed));
        True(failedRow.ContainsKey("transport_error"));True(!failedRow.ContainsKey("raw"));
        True(!failedRow.ContainsKey("error"));

        try { await Demo.RunAsync(["--endpoint","https://example.org","--repeats","1"]); throw new InvalidOperationException("Accepted one repeat."); }
        catch (ArgumentException) { assertions++; }
        foreach(var invalid in new[]{"{\"mini\":[-1,2]}","{\"mini\":[true,2]}","{\"other\":[1,2]}","{\"mini\":[1]}"})
        {
            try { Demo.ParseRates(JsonNode.Parse(invalid)); throw new InvalidOperationException("Accepted invalid rates."); }
            catch (ArgumentException) { assertions++; }
        }
        Console.WriteLine($"15-model-benchmark: {assertions} offline assertions passed.");
        return 0;
    }
}
