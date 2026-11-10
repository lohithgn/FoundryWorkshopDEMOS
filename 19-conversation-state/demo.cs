#!/usr/bin/env dotnet
#:package Azure.Identity@1.21.0
#:package Microsoft.Data.Sqlite@10.0.15
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Data.Sqlite;

public sealed record ChatMessage(string Role,string Content);
public sealed record BoundedMessages(List<ChatMessage> Messages,List<ChatMessage> Retained,int Removed);

public sealed class Store
{
    public string PathName{get;}
    public Store(string path)
    {
        PathName=System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
        using var db=Open();
        using var command=db.CreateCommand();
        command.CommandText="CREATE TABLE IF NOT EXISTS conversations (user TEXT, conversation TEXT, history TEXT NOT NULL, PRIMARY KEY(user, conversation))";
        command.ExecuteNonQuery();
        if(!OperatingSystem.IsWindows())
            File.SetUnixFileMode(PathName,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }

    private SqliteConnection Open()
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=PathName}.ToString());
        db.Open();return db;
    }
    public List<ChatMessage> Load(string user,string conversation)
    {
        using var db=Open();using var command=db.CreateCommand();
        command.CommandText="SELECT history FROM conversations WHERE user=$user AND conversation=$conversation";
        command.Parameters.AddWithValue("$user",user);command.Parameters.AddWithValue("$conversation",conversation);
        var json=command.ExecuteScalar() as string;
        if(json is null)return [];
        return (JsonNode.Parse(json) as JsonArray??[]).Select(item=>new ChatMessage(
            item?["role"]?.GetValue<string>()??throw new JsonException("History role is missing."),
            item?["content"]?.GetValue<string>()??throw new JsonException("History content is missing."))).ToList();
    }
    public void Save(string user,string conversation,IReadOnlyList<ChatMessage> history)
    {
        var json=new JsonArray(history.Select(message=>(JsonNode?)new JsonObject
            { ["role"]=message.Role,["content"]=message.Content }).ToArray()).ToJsonString();
        using var db=Open();using var command=db.CreateCommand();
        command.CommandText="INSERT OR REPLACE INTO conversations(user,conversation,history) VALUES($user,$conversation,$history)";
        command.Parameters.AddWithValue("$user",user);command.Parameters.AddWithValue("$conversation",conversation);
        command.Parameters.AddWithValue("$history",json);command.ExecuteNonQuery();
    }
    public int Delete(string user,string conversation)
    {
        using var db=Open();using var command=db.CreateCommand();
        command.CommandText="DELETE FROM conversations WHERE user=$user AND conversation=$conversation";
        command.Parameters.AddWithValue("$user",user);command.Parameters.AddWithValue("$conversation",conversation);
        return command.ExecuteNonQuery();
    }
    public void Reset(string user,string conversation)=>Save(user,conversation,[]);
}

public static class Demo
{
    public const string SystemPrompt="You are a concise workshop tutor. Only recall personal facts present in this conversation. If a prior fact is absent, say you do not know; do not invent memories. Answer in at most 30 words.";

    public static int Cost(IEnumerable<ChatMessage> messages)=>
        messages.Sum(message=>Encoding.UTF8.GetByteCount(message.Content)+16);

    public static BoundedMessages Bounded(IReadOnlyList<ChatMessage> history,string prompt,int budget,int reserve)
    {
        if(!(1<=reserve&&reserve<budget))
            throw new ArgumentException("Reserve must be positive and smaller than the context budget.");
        var system=new ChatMessage("system",SystemPrompt);
        var current=new ChatMessage("user",prompt);
        if(Cost([system,current])+reserve>budget)
            throw new ArgumentException("Current prompt cannot fit; shorten it or raise --budget.");
        var retained=history.ToList();var removed=0;
        while(Cost(new[]{system}.Concat(retained).Append(current))+reserve>budget)
        {retained=retained.Skip(2).ToList();removed+=2;}
        return new(new[]{system}.Concat(retained).Append(current).ToList(),retained,removed);
    }

    public static async Task<string> TurnAsync(Store store,
        Func<JsonObject,CancellationToken,Task<JsonObject>> send,string deployment,
        string user,string conversation,string prompt,int budget=800,int reserve=120,
        Action<string>? write=null,CancellationToken cancellationToken=default)
    {
        if(string.IsNullOrWhiteSpace(prompt))throw new ArgumentException("Prompt must not be empty.");
        var bound=Bounded(store.Load(user,conversation),prompt,budget,reserve);
        var body=new JsonObject { ["model"]=deployment,["max_completion_tokens"]=reserve,
            ["temperature"]=0,["messages"]=new JsonArray(bound.Messages.Select(message=>(JsonNode?)new JsonObject
                { ["role"]=message.Role,["content"]=message.Content }).ToArray()) };
        var response=await send(body,cancellationToken);
        var choice=response["choices"]?[0] as JsonObject??throw new InvalidOperationException("Incomplete model answer; stored state was not changed.");
        if(choice["finish_reason"]?.GetValue<string>()!="stop"||
            choice["message"]?["content"]?.GetValue<string>() is not {Length:>0} answer)
            throw new InvalidOperationException("Incomplete model answer; stored state was not changed.");
        var retained=bound.Retained.Append(new ChatMessage("user",prompt))
            .Append(new ChatMessage("assistant",answer)).ToList();
        var removed=bound.Removed;
        while(retained.Count>0&&Cost(new[]{new ChatMessage("system",SystemPrompt)}.Concat(retained))+reserve>budget)
        {retained=retained.Skip(2).ToList();removed+=2;}
        store.Save(user,conversation,retained);
        (write??Console.WriteLine)($"user={user} thread={conversation} input_bound={Cost(bound.Messages)} reserved_output_tokens={reserve} removed_messages={removed}\n{answer}");
        return answer;
    }

    public static async Task DemonstrationAsync(Store store,
        Func<JsonObject,CancellationToken,Task<JsonObject>> send,string deployment,int budget=800,int reserve=120,
        Action<string>? write=null)
    {
        var thread="lesson-"+Guid.NewGuid().ToString("N")[..12];
        var output=write??Console.WriteLine;
        output("Two logical application users, not an authentication demonstration.");
        try
        {
            await TurnAsync(store,send,deployment,"alice",thread,"My notebook label is MAPLE-742. Acknowledge it.",budget,reserve,output);
            var saved=store.Load("alice",thread);
            if(saved.Count==0)throw new InvalidOperationException("Budget too small to retain the first turn; use --budget 800.");
            var resumed=new Store(store.PathName);
            if(!resumed.Load("alice",thread).SequenceEqual(saved))throw new InvalidOperationException("Persistence/resume check failed.");
            output("Resumed persisted Alice conversation using a new SQLite connection.");
            await TurnAsync(resumed,send,deployment,"alice",thread,"What is my notebook label?",budget,reserve,output);
            if(resumed.Load("bob",thread).Count!=0)throw new InvalidOperationException("Cross-user history leak.");
            await TurnAsync(resumed,send,deployment,"bob",thread,"What is my notebook label?",budget,reserve,output);
            if(JsonSerializerIsPresent(resumed.Load("bob",thread),"MAPLE-742"))throw new InvalidOperationException("Bob received Alice's marker.");
            output("Bob used the same thread ID but a different user scope: no Alice history.");
            for(var number=0;number<5;number++)
                await TurnAsync(resumed,send,deployment,"alice",thread,
                    $"Lesson {number}: explain resource cleanup briefly. "+"Practice. ".Repeat(18),budget,reserve,output);
            if(JsonSerializerIsPresent(resumed.Load("alice",thread),"MAPLE-742"))throw new InvalidOperationException("Opening fact was not evicted; use the default 800 budget.");
            output("Opening notebook fact is absent from stored/model history after truncation.");
            await TurnAsync(resumed,send,deployment,"alice",thread,"What is my notebook label? Do not guess.",budget,reserve,output);
            resumed.Reset("alice",thread);
            if(resumed.Load("alice",thread).Count!=0)throw new InvalidOperationException("Reset failed.");
            if(resumed.Load("bob",thread).Count==0)throw new InvalidOperationException("Reset crossed user scope.");
            output("Reset Alice to empty history; Bob was unchanged.");
        }
        finally
        {
            foreach(var user in new[]{"alice","bob"})
            {store.Delete(user,thread);if(store.Load(user,thread).Count!=0)throw new InvalidOperationException("Deletion failed.");}
            output("Deleted both demonstration conversations.");
        }
    }

    private static bool JsonSerializerIsPresent(IEnumerable<ChatMessage> history,string value)=>
        history.Any(message=>message.Content.Contains(value,StringComparison.Ordinal));

    public static async Task<int> RunAsync(string[] args)
    {
        var options=new Options(args);
        if(options.Help){options.Usage();return 0;}
        if(!(1<=options.Reserve&&options.Reserve<options.Budget))
            throw new ArgumentException("Require 1 <= reserve < budget.");
        var store=new Store(options.State);
        if(options.Action=="show")
        {
            Console.WriteLine(new JsonArray(store.Load(options.User,options.Conversation)
                .Select(message=>(JsonNode?)new JsonObject{["role"]=message.Role,["content"]=message.Content}).ToArray())
                .ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        }
        else if(options.Action=="reset"){store.Reset(options.User,options.Conversation);Console.WriteLine("Reset scoped conversation.");}
        else if(options.Action=="delete")Console.WriteLine($"Deleted {store.Delete(options.User,options.Conversation)} scoped conversation.");
        else
        {
            if(!Uri.TryCreate(options.Endpoint,UriKind.Absolute,out var endpoint)||endpoint.Scheme!="https")
                throw new ArgumentException("Set AZURE_OPENAI_ENDPOINT to the deployed HTTPS inference endpoint.");
            var credential=new AzureCliCredential();
            using var client=new OpenAiClient(endpoint.ToString(),credential);
            if(options.Action=="demo")await DemonstrationAsync(store,client.PostAsync,options.Deployment,options.Budget,options.Reserve);
            else await TurnAsync(store,client.PostAsync,options.Deployment,options.User,options.Conversation,
                options.Prompt,options.Budget,options.Reserve);
        }
        return 0;
    }

    public sealed class Options
    {
        public string Action{get;private set;}="demo";
        public string State{get;private set;}=".state/conversations.sqlite3";
        public string User{get;private set;}="alice";
        public string Conversation{get;private set;}="workshop";
        public string Prompt{get;private set;}="Explain Microsoft Foundry in one sentence.";
        public int Budget{get;private set;}=800;
        public int Reserve{get;private set;}=120;
        public string? Endpoint{get;private set;}=Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        public string Deployment{get;private set;}=Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")??"gpt-4.1-mini";
        public bool Help{get;}
        public Options(string[] args)
        {
            for(var i=0;i<args.Length;i++)
            {
                var name=args[i];if(name is "-h" or "--help"){Help=true;continue;}
                if(i+1>=args.Length||args[i+1].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException($"Missing value for {name}.");
                var value=args[++i];switch(name)
                {
                    case "--action":if(value is not ("demo" or "turn" or "show" or "reset" or "delete"))throw new ArgumentException("Invalid --action.");Action=value;break;
                    case "--state":State=value;break;case "--user":User=value;break;case "--conversation":Conversation=value;break;
                    case "--prompt":Prompt=value;break;case "--endpoint":Endpoint=value;break;case "--deployment":Deployment=value;break;
                    case "--budget":if(!int.TryParse(value,out var budget))throw new ArgumentException("--budget must be an integer.");Budget=budget;break;
                    case "--reserve":if(!int.TryParse(value,out var reserve))throw new ArgumentException("--reserve must be an integer.");Reserve=reserve;break;
                    default:throw new ArgumentException($"Unknown argument: {name}");
                }
            }
        }
        public void Usage()=>Console.WriteLine("Usage: dotnet run --file demo.cs -- [--action demo|turn|show|reset|delete] [--state FILE] [--user USER] [--conversation ID] [--prompt TEXT] [--budget N] [--reserve N] [--endpoint URL] [--deployment NAME]");
    }
}

internal static class StringExtensions
{
    public static string Repeat(this string value,int count)=>string.Concat(Enumerable.Repeat(value,count));
}

public sealed class OpenAiClient:IDisposable
{
    private readonly TokenCredential credential;private readonly HttpClient http=new(){Timeout=Timeout.InfiniteTimeSpan};private readonly Uri baseUri;
    public OpenAiClient(string endpoint,TokenCredential credential){this.credential=credential;baseUri=new Uri(endpoint.TrimEnd('/')+"/openai/v1/");}
    public async Task<JsonObject> PostAsync(JsonObject body,CancellationToken cancellationToken=default)
    {
        for(var attempt=0;;attempt++)
        {
            try
            {
                var token=await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),cancellationToken);
                using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(baseUri,"chat/completions"))
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
    public static async Task<int> Main(string[] args){try{return await Demo.RunAsync(args);}catch(Exception error){Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");return 1;}}
}
#endif
