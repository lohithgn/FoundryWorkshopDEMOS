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

public sealed record Proposal(string Id,string Digest);

public static class Demo
{
    public const string Scope="https://cognitiveservices.azure.com/.default";
    public static readonly JsonObject Tool=new()
    {
        ["type"]="function",
        ["function"]=new JsonObject
        {
            ["name"]="propose_booking",
            ["description"]="Propose, but never execute, a synthetic workshop booking.",
            ["strict"]=true,
            ["parameters"]=new JsonObject
            {
                ["type"]="object",
                ["properties"]=new JsonObject
                {
                    ["workshop"]=new JsonObject { ["type"]="string",["enum"]=new JsonArray("foundry-intro") },
                    ["attendees"]=new JsonObject { ["type"]="integer" }
                },
                ["required"]=new JsonArray("workshop","attendees"),["additionalProperties"]=false
            }
        }
    };

    public static string Validate(JsonNode? action)
    {
        if(action is not JsonObject obj||obj.Count!=2||!obj.ContainsKey("workshop")||!obj.ContainsKey("attendees"))
            throw new ArgumentException("Only workshop and attendees are permitted.");
        if(obj["workshop"]?.GetValue<string>()!="foundry-intro")
            throw new ArgumentException("Only the synthetic foundry-intro workshop is permitted.");
        if(obj["attendees"] is not JsonValue value||!value.TryGetValue<int>(out var attendees)||attendees<1||attendees>20)
            throw new ArgumentException("Booking policy permits 1-20 attendees.");
        return $"{{\"attendees\":{attendees},\"workshop\":\"foundry-intro\"}}";
    }

    public static string ActorFromToken(string token)
    {
        var segments=token.Split('.');
        if(segments.Length<2)throw new ArgumentException("An Entra tenant/object identity is required.");
        var payload=segments[1].Replace('-','+').Replace('_','/');
        payload+=new string('=',(4-payload.Length%4)%4);
        using var claims=JsonDocument.Parse(Convert.FromBase64String(payload));
        if(!claims.RootElement.TryGetProperty("oid",out var oid)||string.IsNullOrEmpty(oid.GetString())||
           !claims.RootElement.TryGetProperty("tid",out var tid)||string.IsNullOrEmpty(tid.GetString()))
            throw new ArgumentException("An Entra tenant/object identity is required.");
        return $"{tid.GetString()}/{oid.GetString()}";
    }

    public static SqliteConnection Connect(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString());
        db.Open();
        using(var command=db.CreateCommand())
        {
            command.CommandText="CREATE TABLE IF NOT EXISTS proposals (id TEXT PRIMARY KEY, actor TEXT NOT NULL, payload TEXT NOT NULL, digest TEXT NOT NULL, expires REAL NOT NULL, status TEXT NOT NULL)";
            command.ExecuteNonQuery();
            command.CommandText="CREATE TABLE IF NOT EXISTS bookings (proposal_id TEXT PRIMARY KEY, payload TEXT NOT NULL)";
            command.ExecuteNonQuery();
        }
        return db;
    }

    private static string Digest(string proposalId,string actor,string payload)=>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{proposalId}:{actor}:{payload}")));

    public static Proposal Propose(SqliteConnection db,string actor,JsonNode action,double now)
    {
        var payload=Validate(action);var id=Guid.NewGuid().ToString("N");var digest=Digest(id,actor,payload);
        using var command=db.CreateCommand();
        command.CommandText="INSERT INTO proposals VALUES ($id,$actor,$payload,$digest,$expires,'pending')";
        command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$actor",actor);
        command.Parameters.AddWithValue("$payload",payload);command.Parameters.AddWithValue("$digest",digest);
        command.Parameters.AddWithValue("$expires",now+900);command.ExecuteNonQuery();
        return new(id,digest);
    }

    public static string Decide(SqliteConnection db,string actor,string proposalId,string digest,bool approve,double now)
    {
        using var transaction=db.BeginTransaction();
        string owner,payload,savedDigest,status;double expires;
        using(var command=db.CreateCommand())
        {
            command.Transaction=transaction;
            command.CommandText="SELECT actor,payload,digest,expires,status FROM proposals WHERE id=$id";
            command.Parameters.AddWithValue("$id",proposalId);
            using var reader=command.ExecuteReader();
            if(!reader.Read())throw new ArgumentException("Unknown proposal.");
            owner=reader.GetString(0);payload=reader.GetString(1);savedDigest=reader.GetString(2);
            expires=reader.GetDouble(3);status=reader.GetString(4);
        }
        var expected=Digest(proposalId,owner,payload);
        if(actor!=owner||digest!=savedDigest||expected!=digest)
            throw new UnauthorizedAccessException("Actor or exact approved action does not match.");
        Validate(JsonNode.Parse(payload));
        if(status=="executed"&&approve)
        {
            using var check=db.CreateCommand();check.Transaction=transaction;
            check.CommandText="SELECT payload FROM bookings WHERE proposal_id=$id";
            check.Parameters.AddWithValue("$id",proposalId);
            var booking=check.ExecuteScalar() as string;
            if(booking!=payload)throw new InvalidOperationException("Stored booking does not match the approved proposal.");
            transaction.Commit();
            return "REPLAY: existing synthetic booking; no duplicate created.";
        }
        if(status!="pending"||now>expires)
            throw new UnauthorizedAccessException("Proposal is rejected, completed, or expired.");
        if(approve)
        {
            using var insert=db.CreateCommand();insert.Transaction=transaction;
            insert.CommandText="INSERT INTO bookings VALUES ($id,$payload)";
            insert.Parameters.AddWithValue("$id",proposalId);insert.Parameters.AddWithValue("$payload",payload);
            insert.ExecuteNonQuery();
        }
        using(var update=db.CreateCommand())
        {
            update.Transaction=transaction;update.CommandText="UPDATE proposals SET status=$status WHERE id=$id";
            update.Parameters.AddWithValue("$status",approve?"executed":"rejected");
            update.Parameters.AddWithValue("$id",proposalId);update.ExecuteNonQuery();
        }
        transaction.Commit();
        return approve?"APPROVED: synthetic booking created.":"REJECTED: no booking created.";
    }

    public static async Task<JsonObject> ModelActionAsync(Func<JsonObject,CancellationToken,Task<JsonObject>> send,
        string deployment,int attendees,CancellationToken cancellationToken=default)
    {
        var body=new JsonObject
        {
            ["model"]=deployment,["max_completion_tokens"]=200,["parallel_tool_calls"]=false,
            ["tool_choice"]=new JsonObject { ["type"]="function",["function"]=new JsonObject{["name"]="propose_booking"} },
            ["tools"]=new JsonArray(Tool.DeepClone()),
            ["messages"]=new JsonArray(
                new JsonObject{["role"]="system",["content"]="Propose the requested synthetic booking using the tool."},
                new JsonObject{["role"]="user",["content"]=$"Book foundry-intro for {attendees} attendees."})
        };
        var response=await send(body,cancellationToken);
        if(response["choices"] is not JsonArray choices||choices.Count!=1)
            throw new InvalidOperationException("Expected exactly one model choice.");
        var choice=choices[0] as JsonObject??throw new InvalidOperationException("Expected exactly one complete tool proposal.");
        var message=choice["message"] as JsonObject??new JsonObject();
        var calls=message["tool_calls"] as JsonArray;
        if(choice["finish_reason"]?.GetValue<string>() is not ("tool_calls" or "stop")||
            calls is null||calls.Count!=1||!string.IsNullOrEmpty(message["refusal"]?.GetValue<string>()))
            throw new InvalidOperationException("Expected exactly one complete tool proposal.");
        var call=calls[0] as JsonObject;
        if(call?["type"]?.GetValue<string>()!="function"||
            call["function"]?["name"]?.GetValue<string>()!="propose_booking")
            throw new InvalidOperationException("Unexpected tool; no action executed.");
        var arguments=call["function"]?["arguments"]?.GetValue<string>()
            ??throw new InvalidOperationException("Unexpected tool; no action executed.");
        JsonObject action;
        try{action=JsonNode.Parse(arguments) as JsonObject??throw new JsonException();}
        catch(JsonException error){throw new InvalidOperationException("Unexpected tool; no action executed.",error);}
        Validate(action);
        if(action["attendees"]!.GetValue<int>()!=attendees)
            throw new InvalidOperationException("Model proposal differs from the requested attendee count.");
        return action;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try { options=new Options(args); }
        catch (ArgumentException error) { throw new CliException(error.Message); }
        if(options.Help){options.Usage();return 0;}
        if((options.Approve is not null||options.Reject is not null)&&string.IsNullOrEmpty(options.Digest))
            throw new CliException("Approval/rejection requires the exact --digest.");
        if(options.Attendees<1||options.Attendees>20||string.IsNullOrWhiteSpace(options.Deployment))
            throw new CliException("Use 1-20 attendees and a nonempty deployment.");
        if(options.Approve is null&&options.Reject is null&&
            (!Uri.TryCreate(options.Endpoint,UriKind.Absolute,out var endpoint)||endpoint.Scheme!="https"))
            throw new CliException("Set AZURE_OPENAI_ENDPOINT to the Bicep endpoint output.");
        var credential=new AzureCliCredential();
        var token=await credential.GetTokenAsync(new TokenRequestContext([Scope]));
        var actor=ActorFromToken(token.Token);
        using var db=Connect(options.Database);
        if(options.Approve is not null||options.Reject is not null)
        {
            Console.WriteLine(Decide(db,actor,options.Approve??options.Reject!,options.Digest!,
                options.Approve is not null,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0));
            return 0;
        }
        using var client=new OpenAiClient(options.Endpoint!,credential);
        var action=await ModelActionAsync(client.PostAsync,options.Deployment,options.Attendees);
        var proposal=Propose(db,actor,action,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0);
        Console.WriteLine($"PROPOSED ONLY (expires in 15 minutes): {action.ToJsonString()}");
        Console.WriteLine($"Proposal: {proposal.Id}\nDigest: {proposal.Digest}");
        Console.WriteLine("Approve: "+DecisionCommand(proposal.Id,proposal.Digest,true,options.Database));
        Console.WriteLine("Reject:  "+DecisionCommand(proposal.Id,proposal.Digest,false,options.Database));
        Console.WriteLine("No booking has been made. Review the exact action before approving.");
        return 0;
    }

    public static string DecisionCommand(string proposalId,string digest,bool approve,string database)
    {
        var action=approve?"--approve":"--reject";
        return $"dotnet run --file demo.cs -- {action} {proposalId} --digest {digest} --database {QuoteShell(database)}";
    }

    private static string QuoteShell(string value)=>"'"+value.Replace("'","'\\''",StringComparison.Ordinal)+"'";

    public sealed class Options
    {
        public string? Endpoint{get;private set;}=Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        public string Deployment{get;private set;}=Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")??"gpt-4.1-mini";
        public string Database{get;private set;}=Path.Combine(".azure","bookings.db");
        public int Attendees{get;private set;}=4;
        public string? Approve{get;private set;}
        public string? Reject{get;private set;}
        public string? Digest{get;private set;}
        public bool Help{get;}
        public Options(string[] args)
        {
            for(var i=0;i<args.Length;i++)
            {
                var name=args[i];if(name is "-h" or "--help"){Help=true;continue;}
                if(i+1>=args.Length||args[i+1].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException($"Missing value for {name}.");
                var value=args[++i];switch(name)
                {
                    case "--endpoint":Endpoint=value;break;case "--deployment":Deployment=value;break;
                    case "--database":Database=value;break;
                    case "--attendees":if(!int.TryParse(value,out var n))throw new ArgumentException("--attendees must be an integer.");Attendees=n;break;
                    case "--approve":if(Reject is not null)throw new ArgumentException("--approve and --reject are mutually exclusive.");Approve=value;break;
                    case "--reject":if(Approve is not null)throw new ArgumentException("--approve and --reject are mutually exclusive.");Reject=value;break;
                    case "--digest":Digest=value;break;default:throw new ArgumentException($"Unknown argument: {name}");
                }
            }
        }
        public void Usage()=>Console.WriteLine("Usage: dotnet run --file demo.cs -- [--endpoint URL] [--deployment NAME] [--database FILE] [--attendees N] [--approve ID|--reject ID --digest SHA256]");
    }
}

public sealed class OpenAiClient:IDisposable
{
    private readonly TokenCredential credential;private readonly HttpClient http=new(){Timeout=Timeout.InfiniteTimeSpan};private readonly Uri baseUri;
    public OpenAiClient(string endpoint,TokenCredential credential){this.credential=credential;baseUri=new Uri(endpoint.TrimEnd('/')+"/openai/v1/");}
    public async Task<JsonObject> PostAsync(JsonObject body,CancellationToken cancellationToken=default)
    {
        var token=await credential.GetTokenAsync(new TokenRequestContext([Demo.Scope]),cancellationToken);
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(baseUri,"chat/completions"))
        {Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json")};
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token.Token);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        var text=await response.Content.ReadAsStringAsync(timeout.Token);
        if(!response.IsSuccessStatusCode)throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {text}",null,response.StatusCode);
        return JsonNode.Parse(text)?.AsObject()??throw new JsonException("Service returned empty JSON.");
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
