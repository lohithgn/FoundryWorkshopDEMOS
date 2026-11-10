#:package Azure.Identity@1.17.2

using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

return await AgentSkills.RunAsync(args);

static class AgentSkills
{
    const string Instructions = "You are a concise workshop assistant.";
    const string ContractCode = "WORKSHOP-BRIEF-V1";
    static readonly string Root = AppContext.BaseDirectory;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file demo.cs -- [--project-endpoint URL] [--deployment NAME] [--title TEXT] [--debug-response]");
            return 0;
        }
        if (args.Contains("--self-test"))
            return SelfTest();

        var options = Parse(args);
        var endpoint = options.GetValueOrDefault("--project-endpoint") ??
                       Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        if (!(endpoint ?? "").StartsWith("https://", StringComparison.Ordinal))
            return UsageError("Set FOUNDRY_PROJECT_ENDPOINT to the Bicep projectEndpoint HTTPS URL.");
        var deployment = options.GetValueOrDefault("--deployment") ??
                         Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
        var title = options.GetValueOrDefault("--title") ?? "Introduction to Microsoft Foundry";
        if (string.IsNullOrWhiteSpace(deployment) || string.IsNullOrWhiteSpace(title))
            return UsageError("--deployment and --title must not be empty.");

        var credential = new AzureCliCredential();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var project = endpoint!.TrimEnd('/');
        string? skillName = null;
        var agents = new List<(string Name, string Version)>();
        try
        {
            skillName = "workshop-brief-" + Guid.NewGuid().ToString("N")[..12];
            var (zip, expected) = MakePackage(skillName);
            var skill = await UploadSkill(http, credential, project, skillName, zip);
            var version = skill["version"]?.GetValue<string>() ?? throw new InvalidDataException("Foundry returned no skill version.");
            var downloaded = await DownloadSkill(http, credential, project, skillName, version);
            var injected = ReadVerifiedSkill(downloaded, expected);
            Console.WriteLine($"Created Foundry skill: {skillName} / {version}");
            Console.WriteLine("Downloaded and byte-verified pinned skill version; direct injection only.");

            foreach (var useSkill in new[] { false, true })
            {
                var name = "skill-demo-" + Guid.NewGuid().ToString("N")[..12];
                var definition = new JsonObject
                {
                    ["kind"] = "prompt",
                    ["model"] = deployment,
                    ["instructions"] = useSkill ? Instructions + "\n\n" + injected : Instructions,
                    ["tools"] = new JsonArray()
                };
                var agent = await CreateAgent(http, credential, project, name, definition);
                var agentVersion = agent["version"]?.GetValue<string>() ?? throw new InvalidDataException("Foundry returned no agent version.");
                agents.Add((name, agentVersion));
                Console.WriteLine($"Temporary Foundry agent: {name} / {agentVersion}");
                using var response = await Send(http, credential, HttpMethod.Post, project + "/openai/v1/responses",
                    new StringContent(new JsonObject
                    {
                        ["input"] = $"Create a short workshop briefing for this title: {JsonValue.Create(title)!.ToJsonString()}",
                        ["tool_choice"] = "none",
                        ["agent_reference"] = new JsonObject { ["name"] = name, ["version"] = agentVersion, ["type"] = "agent_reference" }
                    }.ToJsonString(), Encoding.UTF8, "application/json"));
                var body = await ReadJson(response);
                var text = ResponseText(body);
                Console.WriteLine($"\nResponse ID: {body["id"]}; status: {body["status"]}");
                if (options.ContainsKey("--debug-response"))
                    Console.WriteLine(body.ToJsonString(JsonOptions));
                if ((string?)body["status"] != "completed" || string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException($"Agent did not complete with text: {body["status"]}; {body["error"]}");
                Console.WriteLine(text);
                var matched = MatchesContract(text, title);
                Console.WriteLine($"Exact skill contract match: {matched}");
                if (useSkill && !matched)
                    throw new InvalidOperationException("Skill-enabled answer did not satisfy the exact workshop-brief contract.");
            }
            Console.WriteLine("Both requests used independent conversations; the skill supplies instructions, not executable tools.");
            return 0;
        }
        finally
        {
            foreach (var (name, version) in agents.AsEnumerable().Reverse())
                await Delete(http, credential, project, $"/agents/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(version)}");
            if (skillName is not null)
                await Delete(http, credential, project, $"/skills/{Uri.EscapeDataString(skillName)}");
        }
    }

    static Dictionary<string, string?> Parse(string[] args)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--debug-response") { values[args[i]] = null; continue; }
            if (args[i] is not ("--project-endpoint" or "--deployment" or "--title") || i + 1 == args.Length)
                throw new ArgumentException($"Unknown or incomplete option: {args[i]}");
            values[args[i]] = args[++i];
        }
        return values;
    }

    static (byte[] Zip, byte[] Content) MakePackage(string name)
    {
        var path = Path.Combine(Environment.CurrentDirectory, "skills", "workshop-brief", "SKILL.md");
        var content = File.ReadAllText(path, Encoding.UTF8).Replace("name: workshop-brief\n", $"name: {name}\n", StringComparison.Ordinal);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry("SKILL.md").Open(), new UTF8Encoding(false)))
            writer.Write(content);
        return (buffer.ToArray(), Encoding.UTF8.GetBytes(content));
    }

    static string ReadVerifiedSkill(byte[] zip, byte[] expected)
    {
        using var buffer = new MemoryStream(zip);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        var files = archive.Entries.Where(e => e.Length != 0 || !e.FullName.EndsWith('/')).ToArray();
        if (files.Length != 1 || Path.GetFileName(files[0].FullName) != "SKILL.md")
            throw new InvalidDataException("Downloaded skill must contain exactly one SKILL.md file.");
        using var stream = files[0].Open();
        using var content = new MemoryStream();
        stream.CopyTo(content);
        if (!CryptographicOperations.FixedTimeEquals(content.ToArray(), expected))
            throw new InvalidDataException("Downloaded skill content differs from the uploaded version.");
        return Encoding.UTF8.GetString(content.ToArray());
    }

    static bool MatchesContract(string text, string title)
    {
        try
        {
            var value = JsonNode.Parse(text) as JsonObject;
            var expected = new JsonObject
            {
                ["title"] = title, ["before"] = "Sign in with Azure CLI.",
                ["during"] = "Use synthetic data only.", ["after"] = "Delete the demo resource group.",
                ["skill_code"] = ContractCode
            };
            return value is not null && JsonNode.DeepEquals(value, expected);
        }
        catch (JsonException) { return false; }
    }

    static async Task<JsonObject> UploadSkill(HttpClient http, TokenCredential credential, string project, string name, byte[] zip)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "files", "workshop-brief.zip");
        using var response = await Send(http, credential, HttpMethod.Post,
            $"{project}/skills/{Uri.EscapeDataString(name)}/versions?api-version=v1", content, "Skills=V1Preview");
        return await ReadJson(response);
    }

    static async Task<byte[]> DownloadSkill(HttpClient http, TokenCredential credential, string project, string name, string version)
    {
        using var response = await Send(http, credential, HttpMethod.Get,
            $"{project}/skills/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(version)}/files?api-version=v1", null, "Skills=V1Preview");
        return await response.Content.ReadAsByteArrayAsync();
    }

    static async Task<JsonObject> CreateAgent(HttpClient http, TokenCredential credential, string project, string name, JsonObject definition)
    {
        using var response = await Send(http, credential, HttpMethod.Post,
            $"{project}/agents/{Uri.EscapeDataString(name)}/versions?api-version=v1",
            new StringContent(new JsonObject { ["definition"] = definition }.ToJsonString(), Encoding.UTF8, "application/json"), "Skills=V1Preview");
        return await ReadJson(response);
    }

    static async Task Delete(HttpClient http, TokenCredential credential, string project, string path)
    {
        using var response = await Send(http, credential, HttpMethod.Delete, project + path + "?api-version=v1", null, "Skills=V1Preview");
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    static async Task<HttpResponseMessage> Send(HttpClient http, TokenCredential credential, HttpMethod method, string url, HttpContent? content, string? features = null)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), CancellationToken.None);
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.TryAddWithoutValidation("Foundry-Features", features);
        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new HttpRequestException($"Foundry request failed ({(int)response.StatusCode}): {detail}");
        }
        return response;
    }

    static async Task<JsonObject> ReadJson(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject ??
               throw new InvalidDataException("Foundry returned an empty JSON object.");
    }

    static string ResponseText(JsonObject response)
    {
        foreach (var item in response["output"]?.AsArray() ?? [])
            foreach (var content in item?["content"]?.AsArray() ?? [])
                if ((string?)content?["type"] == "output_text") return (string?)content["text"] ?? "";
        return "";
    }

    static int SelfTest()
    {
        var answer = new JsonObject
        {
            ["title"] = "Demo", ["before"] = "Sign in with Azure CLI.", ["during"] = "Use synthetic data only.",
            ["after"] = "Delete the demo resource group.", ["skill_code"] = ContractCode
        }.ToJsonString();
        if (!MatchesContract(answer, "Demo") || MatchesContract(answer, "wrong") ||
            MatchesContract(answer[..^1] + ",\"extra\":true}", "Demo"))
            throw new InvalidOperationException("Exact skill contract regression failed.");
        Console.WriteLine("PASS: exact JSON contract accepts only the expected fields and values.");
        return 0;
    }

    static int UsageError(string message) { Console.Error.WriteLine(message); return 2; }
}
