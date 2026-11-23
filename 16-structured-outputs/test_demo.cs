#!/usr/bin/env dotnet
#:property DefineConstants=TEST_HARNESS
#:include demo.cs
using System.Text.Json.Nodes;

public static class Program
{
    private static int assertions;
    private static void Equal<T>(T expected,T actual)
    {assertions++;if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"Expected {expected}, got {actual}.");}
    private static void True(bool value)=>Equal(true,value);

    public static async Task<int> Main()
    {
        foreach(var item in Demo.Cases)
        {var result=Demo.Evaluate(item.Expected.ToJsonString(),item.Expected);True(result["schema_ok"]!.GetValue<bool>());True(result["correct"]!.GetValue<bool>());}
        var validWrong=Demo.Evaluate("""{"category":"outage","priority":"high","summary":"Service unavailable"}""",Demo.Cases[0].Expected);
        True(validWrong["schema_ok"]!.GetValue<bool>());True(!validWrong["correct"]!.GetValue<bool>());
        True(!validWrong["field_matches"]!["category"]!.GetValue<bool>());
        foreach(var invalid in new[]{"{\"category\":null}","not json","[]",
            """{"category":null,"priority":null,"summary":null,"invented":true}"""})
            Equal("invalid",Demo.Evaluate(invalid)["status"]!.GetValue<string>());
        Equal("refusal",Demo.Evaluate("",refusal:"no",finishReason:"length")["status"]!.GetValue<string>());
        foreach(var finish in new[]{"length","content_filter",null!})
            Equal("incomplete",Demo.Evaluate(Demo.Cases[0].Expected.ToJsonString(),finishReason:finish!)["status"]!.GetValue<string>());
        var basic=Demo.Messages("instructions","ticket");
        var few=Demo.Messages("few-shot","ticket");
        Equal(basic[0]!.ToJsonString(),few[0]!.ToJsonString());
        Equal(basic[^1]!.ToJsonString(),few[^1]!.ToJsonString());
        Equal(basic.Count+4,few.Count);
        True(Demo.Schema["required"]!.AsArray().Count==3);
        JsonObject? capturedRequest=null;
        await Demo.CallAsync((body,_)=>{capturedRequest=body;return Task.FromResult(new JsonObject
            { ["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",
                ["message"]=new JsonObject{["content"]=Demo.Cases[0].Expected.ToJsonString()}}) });},
            "model","structured","ticket",Demo.Cases[0].Expected);
        Equal("json_schema",capturedRequest!["response_format"]!["type"]!.GetValue<string>());
        True(capturedRequest["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());

        var timeout=await Demo.CallAsync((_,_)=>throw new TimeoutException("offline fixture"),
            "alias","structured","ticket");
        Equal("request_error",timeout["status"]!.GetValue<string>());
        True(timeout["reason"]!.GetValue<string>().Contains("TimeoutException",StringComparison.Ordinal));
        foreach(var body in new[]{"""{"error":{"code":"content_filter"}}""","""{"code":"content_filter"}"""})
        {
            var filtered=await Demo.CallAsync((_,_)=>throw new ServiceException(400,body),"alias","structured","ticket");
            Equal("content_filter",filtered["status"]!.GetValue<string>());
            Equal(400,filtered["http_status"]!.GetValue<int>());
            True(!filtered["schema_ok"]!.GetValue<bool>());
        }
        var invalidSchema=await Demo.CallAsync((_,_)=>throw new ServiceException(400,"""{"code":"invalid_json_schema"}"""),
            "alias","structured","ticket");
        Equal("request_error",invalidSchema["status"]!.GetValue<string>());
        Equal(1,Demo.ExitCode([invalidSchema]));
        var filteredStatus=new JsonObject{["status"]="content_filter"};
        Equal(0,Demo.ExitCode([filteredStatus]));

        var offlineOutput=new StringWriter();
        var original=Console.Out;
        Console.SetOut(offlineOutput);
        try{Equal(0,await Demo.RunAsync(["--offline-errors"]));}
        finally{Console.SetOut(original);}
        var offline=offlineOutput.ToString().Split(Environment.NewLine,StringSplitOptions.RemoveEmptyEntries)
            .Select(line=>JsonNode.Parse(line)).ToArray();
        Equal("refusal",offline[0]!["status"]!.GetValue<string>());
        Equal("incomplete",offline[1]!["status"]!.GetValue<string>());
        True(offline.All(row=>row!["source"]!.GetValue<string>().Contains("NOT AZURE OUTPUT",StringComparison.Ordinal)));
        foreach(var interval in new[]{"-1","nan","inf"})
            try{await Demo.RunAsync(["--offline-errors","--request-interval",interval]);throw new InvalidOperationException("Accepted bad interval.");}
            catch(ArgumentException){assertions++;}
        Console.WriteLine($"16-structured-outputs: {assertions} offline assertions passed.");
        return 0;
    }
}
