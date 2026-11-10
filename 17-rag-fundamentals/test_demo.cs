#!/usr/bin/env dotnet
#:property DefineConstants=TEST_HARNESS
#:include demo.cs
using System.Text.Json.Nodes;

public static class Program
{
    private static int assertions;
    private static void Equal<T>(T expected,T actual){assertions++;if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"Expected {expected}, got {actual}.");}
    private static void True(bool value)=>Equal(true,value);
    private static void Throws<T>(Action action) where T:Exception{try{action();throw new InvalidOperationException($"Expected {typeof(T).Name}.");}catch(T){assertions++;}}

    public static async Task<int> Main()
    {
        var small=Demo.Chunks(Demo.Documents,12);var large=Demo.Chunks(Demo.Documents,48);
        Equal(small.Count,small.Select(item=>item.Id).Distinct().Count());
        foreach(var (source,text) in Demo.Documents)
        {
            Equal(text,string.Join(' ',small.Where(item=>item.Source==source).Select(item=>item.Content)));
            True(small.Where(item=>item.Source==source).All(item=>item.Content.Split(' ').Length<=12));
        }
        True(small.Count>large.Count);Throws<ArgumentException>(()=>Demo.Chunks(Demo.Documents,0));
        Equal(new RetrievalScore(1,.5),Demo.Metrics([new("x-0","x",""),new("cleanup-0","cleanup","")],"cleanup"));
        Equal(new RetrievalScore(0,0),Demo.Metrics([],"cleanup"));
        Equal<RetrievalScore?>(null,Demo.Metrics([],(string?)null));
        Equal(1,Demo.Questions.Count(item=>item.Expected is null));

        var hits=new[]{new Chunk("a-0","a","Starts at 09:30.")};
        var valid=new JsonObject { ["answer"]="09:30",["citations"]=new JsonArray("a-0"),
            ["quotes"]=new JsonArray(new JsonObject{["id"]="a-0",["text"]="09:30"}) };
        Equal(valid.ToJsonString(),Demo.ValidateAnswer(valid,hits).ToJsonString());
        foreach(var bad in new[]
        {
            new JsonObject{["answer"]="09:30",["citations"]=new JsonArray("secret-0"),["quotes"]=new JsonArray(new JsonObject{["id"]="secret-0",["text"]="09:30"})},
            new JsonObject{["answer"]="09:30",["citations"]=new JsonArray("a-0"),["quotes"]=new JsonArray(new JsonObject{["id"]="a-0",["text"]="10:30"})},
            new JsonObject{["answer"]="09:30",["citations"]=new JsonArray("a-0"),["quotes"]=new JsonArray()},
            new JsonObject{["answer"]=Demo.Abstention,["citations"]=new JsonArray("a-0"),["quotes"]=new JsonArray()}
        })Throws<ArgumentException>(()=>Demo.ValidateAnswer(bad,hits));
        Equal(Demo.Abstention,Demo.ValidateAnswer(new JsonObject{["answer"]=Demo.Abstention,
            ["citations"]=new JsonArray(),["quotes"]=new JsonArray()},[])["answer"]!.GetValue<string>());

        JsonObject? captured=null;
        await Demo.GroundedAnswerAsync((body,_)=>{captured=body;return Task.FromResult(new JsonObject { ["choices"]=new JsonArray(
            new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]="{\"answer\":\"Insufficient evidence.\",\"citations\":[],\"quotes\":[]}"}}) });},
            "deployment","Unknown?",[]);
        Equal("deployment",captured!["model"]!.GetValue<string>());
        True(captured["messages"]![1]!["content"]!.GetValue<string>().Contains("\"evidence\":[]",StringComparison.Ordinal));
        True(!captured["messages"]![1]!["content"]!.GetValue<string>().Contains("expected",StringComparison.Ordinal));

        foreach(var mode in new[]{"keyword","vector","hybrid"})
        {
            var body=Demo.BuildSearchRequest(mode,"question",[1.0]);
            Equal(3,body["top"]!.GetValue<int>());
            Equal(mode!="keyword",body.ContainsKey("vectorQueries"));
            Equal(mode=="vector",body["search"] is null);
            if(mode!="keyword")Equal(3,body["vectorQueries"]![0]!["k"]!.GetValue<int>());
        }
        Throws<ArgumentException>(()=>Demo.BuildSearchRequest("unknown","q",[]));
        await Demo.WaitForIndexAsync(_=>Task.FromResult(4),4,TimeSpan.FromSeconds(1),TimeSpan.Zero);
        try{await Demo.WaitForIndexAsync(_=>Task.FromResult(0),4,TimeSpan.Zero);throw new InvalidOperationException("Expected timeout.");}
        catch(TimeoutException){assertions++;}

        var attempts=0;
        var eventual=await Demo.RetryEmbeddingSetupAsync<int>(_=>
        {
            if(attempts++==0)return Task.FromException<int>(new ServiceException(404,"""{"error":{"code":"DeploymentNotFound"}}"""));
            return Task.FromResult(9);
        },TimeSpan.FromSeconds(2),TimeSpan.Zero);
        Equal(9,eventual);Equal(2,attempts);
        foreach(var error in new[]{new ServiceException(404,"""{"code":"OtherNotFound"}"""),new ServiceException(403,"""{"code":"DeploymentNotFound"}""")})
        {
            try{await Demo.RetryEmbeddingSetupAsync<int>(_=>Task.FromException<int>(error),TimeSpan.Zero,TimeSpan.Zero);throw new InvalidOperationException("Retried unrelated error.");}
            catch(ServiceException){assertions++;}
        }
        True(Demo.IsServiceRoot("https://example.search.windows.net/"));
        True(!Demo.IsServiceRoot("http://example.org"));
        True(!Demo.IsServiceRoot("https://example.org/path"));
        var schema=Demo.IndexDefinition("test");
        Equal(1536,schema["fields"]![3]!["dimensions"]!.GetValue<int>());
        Console.WriteLine($"17-rag-fundamentals: {assertions} offline assertions passed.");
        return 0;
    }
}
