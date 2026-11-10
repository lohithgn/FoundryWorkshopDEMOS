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
    private static JsonObject Action(int attendees=4)=>new(){["workshop"]="foundry-intro",["attendees"]=attendees};
    private static string Claims(string tid,string oid)
    {
        var json=new JsonObject { ["tid"]=tid,["oid"]=oid }.ToJsonString();
        return "header."+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+','-').Replace('/','_')+".signature";
    }

    public static async Task<int> Main()
    {
        var directory=Path.Combine(Path.GetTempPath(),"lab20-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var path=Path.Combine(directory,"bookings.db");
            using var db=Demo.Connect(path);
            var action=Action();
            var proposal=Demo.Propose(db,"tenant/alice",action,100);
            Equal(0L,Scalar(db,"SELECT count(*) FROM bookings"));
            True(Demo.Decide(db,"tenant/alice",proposal.Id,proposal.Digest,true,101).Contains("APPROVED",StringComparison.Ordinal));
            True(Demo.Decide(db,"tenant/alice",proposal.Id,proposal.Digest,true,102).Contains("REPLAY",StringComparison.Ordinal));
            Equal(1L,Scalar(db,"SELECT count(*) FROM bookings"));

            var rejected=Demo.Propose(db,"tenant/alice",action,100);
            True(Demo.Decide(db,"tenant/alice",rejected.Id,rejected.Digest,false,101).Contains("REJECTED",StringComparison.Ordinal));
            Throws<UnauthorizedAccessException>(()=>Demo.Decide(db,"tenant/alice",rejected.Id,rejected.Digest,true,102));
            Equal(1L,Scalar(db,"SELECT count(*) FROM bookings"));

            var across=Demo.Propose(db,"tenant/alice",action,100);
            using(var reopened=Demo.Connect(path))
            {
                True(Demo.Decide(reopened,"tenant/alice",across.Id,across.Digest,true,101).Contains("APPROVED",StringComparison.Ordinal));
                True(Demo.Decide(db,"tenant/alice",across.Id,across.Digest,true,102).Contains("REPLAY",StringComparison.Ordinal));
            }
            Equal(2L,Scalar(db,"SELECT count(*) FROM bookings"));

            var guarded=Demo.Propose(db,"tenant/alice",action,100);
            foreach(var (actor,digest,now) in new[]{("tenant/bob",guarded.Digest,101.0),("tenant/alice","wrong",101.0),("tenant/alice",guarded.Digest,1001.0)})
                Throws<UnauthorizedAccessException>(()=>Demo.Decide(db,actor,guarded.Id,digest,true,now));
            using(var update=db.CreateCommand())
            {update.CommandText="UPDATE proposals SET payload=$payload WHERE id=$id";update.Parameters.AddWithValue("$payload","""{"attendees":8,"workshop":"foundry-intro"}""");update.Parameters.AddWithValue("$id",guarded.Id);update.ExecuteNonQuery();}
            Throws<UnauthorizedAccessException>(()=>Demo.Decide(db,"tenant/alice",guarded.Id,guarded.Digest,true,101));
            foreach(var bad in new[]
            {
                new JsonObject{["workshop"]="foundry-intro",["attendees"]=JsonValue.Create(true)},
                Action(21),new JsonObject{["workshop"]="real-booking",["attendees"]=4},
                new JsonObject{["workshop"]="foundry-intro",["attendees"]=4,["execute"]=true}
            })Throws<ArgumentException>(()=>Demo.Validate(bad));

            JsonObject ToolResponse(JsonObject args,string finish="tool_calls",string name="propose_booking",string? refusal=null)=>
                new(){["choices"]=new JsonArray(new JsonObject{["finish_reason"]=finish,
                    ["message"]=new JsonObject{["refusal"]=refusal,["tool_calls"]=new JsonArray(
                        new JsonObject{["type"]="function",["function"]=new JsonObject
                            { ["name"]=name,["arguments"]=args.ToJsonString() }})}})};
            Task<JsonObject> Send(JsonObject _,CancellationToken __)=>Task.FromResult(ToolResponse(action));
            Equal(action.ToJsonString(),(await Demo.ModelActionAsync(Send,"model",4)).ToJsonString());
            Task<JsonObject> Mismatch(JsonObject _,CancellationToken __)=>Task.FromResult(ToolResponse(Action(5)));
            try{await Demo.ModelActionAsync(Mismatch,"model",4);throw new InvalidOperationException("Accepted changed attendee count.");}
            catch(InvalidOperationException error)when(error.Message.Contains("differs",StringComparison.Ordinal)){assertions++;}
            Equal(action.ToJsonString(),(await Demo.ModelActionAsync(Send,"model",4)).ToJsonString());
            foreach(var invalid in new[]{ToolResponse(action,"length"),ToolResponse(action,"stop",refusal:"Cannot comply.")})
            {
                Task<JsonObject> Invalid(JsonObject _,CancellationToken __)=>Task.FromResult(invalid);
                try{await Demo.ModelActionAsync(Invalid,"model",4);throw new InvalidOperationException("Accepted incomplete/refused proposal.");}
                catch(InvalidOperationException error)when(error.Message.Contains("complete tool",StringComparison.Ordinal)){assertions++;}
            }
            Task<JsonObject> NoChoice(JsonObject _,CancellationToken __)=>Task.FromResult(new JsonObject{["choices"]=new JsonArray()});
            try{await Demo.ModelActionAsync(NoChoice,"model",4);throw new InvalidOperationException("Accepted no choices.");}
            catch(InvalidOperationException error)when(error.Message.Contains("one model choice",StringComparison.Ordinal)){assertions++;}

            Equal("tenant/alice",Demo.ActorFromToken(Claims("tenant","alice")));
            Throws<ArgumentException>(()=>Demo.ActorFromToken("invalid"));
            var noClaims="header."+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{}")).TrimEnd('=').Replace('+','-').Replace('/','_')+".sig";
            Throws<ArgumentException>(()=>Demo.ActorFromToken(noClaims));
            try{new Demo.Options(["--approve","id","--reject","id"]);throw new InvalidOperationException("Accepted both decisions.");}
            catch(ArgumentException){assertions++;}
            try{await Demo.RunAsync(["--approve","id","--database",path]);throw new InvalidOperationException("Accepted approval without digest.");}
            catch(ArgumentException error)when(error.Message.Contains("digest",StringComparison.Ordinal)){assertions++;}
            Console.WriteLine($"20-human-approval: {assertions} offline assertions passed.");
        }
        finally{Directory.Delete(directory,true);}
        return 0;
    }
    private static long Scalar(SqliteConnection db,string sql){using var command=db.CreateCommand();command.CommandText=sql;return (long)command.ExecuteScalar()!;}
}
