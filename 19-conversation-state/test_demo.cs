#!/usr/bin/env dotnet
#:property DefineConstants=TEST_HARNESS
#:include demo.cs
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

public static class Program
{
    private static int assertions;
    private static void Equal<T>(T expected,T actual){assertions++;if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"Expected {expected}, got {actual}.");}
    private static void True(bool value)=>Equal(true,value);
    private static void Throws<T>(Action action)where T:Exception{try{action();throw new InvalidOperationException($"Expected {typeof(T).Name}.");}catch(T){assertions++;}}
    private static JsonObject Reply(string answer,string finish="stop")=>new(){["choices"]=new JsonArray(
        new JsonObject{["finish_reason"]=finish,["message"]=new JsonObject{["content"]=answer}})};

    public static async Task<int> Main()
    {
        var directory=Path.Combine(Path.GetTempPath(),"lab19-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path=Path.Combine(directory,"conversations.sqlite3");var store=new Store(path);
            var history=new[]{new ChatMessage("user","private")};
            store.Save("alice","same-thread",history);store.Save("bob","same-thread",[new("user","other")]);
            var reopened=new Store(path);
            True(reopened.Load("alice","same-thread").SequenceEqual(history));
            Equal(0,reopened.Load("alice","other-thread").Count);
            Equal(0,reopened.Load("mallory' OR 1=1 --","same-thread").Count);
            Equal(0,reopened.Delete("mallory","same-thread"));
            reopened.Reset("bob","same-thread");Equal(0,reopened.Load("bob","same-thread").Count);
            True(reopened.Load("alice","same-thread").SequenceEqual(history));
            Equal(1,reopened.Delete("alice","same-thread"));Equal(0,reopened.Load("alice","same-thread").Count);

            var longHistory=new[]{new ChatMessage("user",string.Concat(Enumerable.Repeat("secret ",20))),
                new ChatMessage("assistant",string.Concat(Enumerable.Repeat("ack ",20))),
                new ChatMessage("user","recent"),new ChatMessage("assistant","response")};
            var bounded=Demo.Bounded(longHistory,"question",500,120);
            Equal(2,bounded.Removed);True(bounded.Retained.SequenceEqual(longHistory[2..]));
            Equal("system",bounded.Messages[0].Role);True(Demo.Cost(bounded.Messages)+120<=500);
            True(!string.Join(' ',bounded.Messages.Select(item=>item.Content)).Contains("secret",StringComparison.Ordinal));
            Equal(18,Demo.Cost([new("user","é")]));
            Throws<ArgumentException>(()=>Demo.Bounded([],new string('x',1000),200,120));
            Throws<ArgumentException>(()=>Demo.Bounded([], "x",100,100));
            Throws<ArgumentException>(()=>Demo.Bounded([], "x",100,0));

            var calls=new List<JsonObject>();
            Task<JsonObject> Send(JsonObject body,CancellationToken _)
            {calls.Add((JsonObject)body.DeepClone());return Task.FromResult(Reply("A short answer."));}
            await Demo.TurnAsync(store,Send,"deployed-model","alice","thread","Question?");
            Equal("deployed-model",calls[^1]["model"]!.GetValue<string>());
            True(Demo.Cost(calls[^1]["messages"]!.AsArray().Select(item=>new ChatMessage(
                item!["role"]!.GetValue<string>(),item["content"]!.GetValue<string>())))+120<=800);
            Equal(2,store.Load("alice","thread").Count);Equal(0,store.Load("bob","thread").Count);
            var saved=store.Load("alice","thread");
            Task<JsonObject> Incomplete(JsonObject _,CancellationToken __)=>Task.FromResult(Reply("partial","length"));
            try{await Demo.TurnAsync(store,Incomplete,"model","alice","thread","Again?");throw new InvalidOperationException("Accepted incomplete response.");}
            catch(InvalidOperationException error)when(error.Message.StartsWith("Incomplete",StringComparison.Ordinal)){assertions++;}
            True(store.Load("alice","thread").SequenceEqual(saved));

            var mock=0;var requests=new List<JsonObject>();
            Task<JsonObject> Lesson(JsonObject body,CancellationToken __){mock++;requests.Add((JsonObject)body.DeepClone());return Task.FromResult(Reply("A short answer."));}
            await Demo.DemonstrationAsync(store,Lesson,"test-model",write:_=>{});
            Equal(9,mock);
            True(!requests[2]["messages"]!.ToJsonString().Contains("MAPLE-742",StringComparison.Ordinal));
            True(!requests[^1]["messages"]!.ToJsonString().Contains("MAPLE-742",StringComparison.Ordinal));
            True(requests.All(request=>request["max_completion_tokens"]!.GetValue<int>()==120));
            Equal(0,store.Load("alice","workshop").Count);
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString()))
            {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM conversations WHERE conversation LIKE 'lesson-%'";Equal(0L,(long)cmd.ExecuteScalar()!);}
            var mode=new Demo.Options(["--action","show"]);
            Equal("show",mode.Action);
            Console.WriteLine($"19-conversation-state: {assertions} offline assertions passed.");
        }
        finally{Directory.Delete(directory,true);}
        return 0;
    }
}
