#:package Azure.Identity@1.17.2

using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

return await ReleaseDemo.Run(args);

static class ReleaseDemo
{
    const string Prefix = "Answer the user's synthetic workshop pricing question using this procedure:\n";
    static readonly string Skills = Path.Combine(Environment.CurrentDirectory, "skills");

    public static async Task<int> Run(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file demo.cs -- [--project-endpoint URL] [--deployment NAME] [--regression]");
            return 0;
        }
        if (args.Contains("--self-test")) return SelfTest();
        var endpoint = Value(args, "--project-endpoint") ?? Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        var deployment = Value(args, "--deployment") ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
        if (endpoint is null || !endpoint.StartsWith("https://", StringComparison.Ordinal))
            return Error("Set FOUNDRY_PROJECT_ENDPOINT to the deployment's projectEndpoint HTTPS URL.");
        if (string.IsNullOrWhiteSpace(deployment)) return Error("--deployment must not be empty.");

        var project = endpoint.TrimEnd('/');
        var name = "pricing-release-" + Guid.NewGuid().ToString("N")[..12];
        var firstBody = Body("v1");
        var secondBody = Body("v2");
        if (args.Contains("--regression")) secondBody = secondBody.Replace("intro workshop: USD 25", "intro workshop: USD 40", StringComparison.Ordinal);
        var bodies = new Dictionary<string, string>();
        var codes = new Dictionary<string, string>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var credential = new AzureCliCredential();
        string? originalVersion = null;
        try
        {
            var first = await CreateSkill(http, credential, project, name, firstBody, true);
            originalVersion = Required(first, "version");
            bodies[originalVersion] = firstBody;
            codes[originalVersion] = "PRICING-V1";
            await SetDefault(http, credential, project, name, originalVersion);
            await Consume(http, credential, project, deployment, name, "floating", "intro", bodies, codes, null);
            await Consume(http, credential, project, deployment, name, "pinned", "intro", bodies, codes, originalVersion);

            var second = await CreateSkill(http, credential, project, name, secondBody, false);
            var secondVersion = Required(second, "version");
            if (secondVersion == originalVersion) throw new InvalidOperationException("Skill versions must be distinct immutable snapshots.");
            bodies[secondVersion] = secondBody;
            codes[secondVersion] = "PRICING-V2";
            if (await GetDefault(http, credential, project, name) != originalVersion)
                throw new InvalidOperationException("Creating the candidate unexpectedly changed the active default.");
            foreach (var workshop in new[] { "intro", "advanced" })
                await Consume(http, credential, project, deployment, name, "candidate gate", workshop, bodies, codes, secondVersion);
            await SetDefault(http, credential, project, name, secondVersion);
            var floating = await Consume(http, credential, project, deployment, name, "floating", "intro", bodies, codes, null);
            var pinned = await Consume(http, credential, project, deployment, name, "pinned", "intro", bodies, codes, originalVersion);
            if (floating != secondVersion || pinned != originalVersion)
                throw new InvalidOperationException("Default and pinned consumers did not resolve the expected versions.");
            await SetDefault(http, credential, project, name, originalVersion);
            foreach (var consumer in new[] { ("floating after rollback", (string?)null), ("pinned after rollback", originalVersion) })
                if (await Consume(http, credential, project, deployment, name, consumer.Item1, "intro", bodies, codes, consumer.Item2) != originalVersion)
                    throw new InvalidOperationException("Rollback did not restore the original skill version.");
            Console.WriteLine("Verified candidate gate, default promotion, pinned isolation and rollback.");
            return 0;
        }
        finally
        {
            if (originalVersion is not null)
                await SetDefault(http, credential, project, name, originalVersion);
            await Delete(http, credential, project, "/skills/" + Uri.EscapeDataString(name));
        }
    }

    static string Body(string version)
    {
        var text = File.ReadAllText(Path.Combine(Skills, $"pricing-{version}", "SKILL.md"));
        var parts = text.Split("---\n", 3);
        if (parts.Length != 3) throw new InvalidDataException("Pricing skill frontmatter is malformed.");
        return parts[2].Trim();
    }

    static async Task<JsonObject> CreateSkill(HttpClient http, TokenCredential credential, string project, string name, string instructions, bool isDefault)
    {
        var body = new JsonObject
        {
            ["inline_content"] = new JsonObject { ["description"] = "Synthetic workshop pricing procedure.", ["instructions"] = instructions },
            ["default"] = isDefault
        };
        return await Json(http, credential, HttpMethod.Post, $"{project}/skills/{Uri.EscapeDataString(name)}/versions?api-version=v1", body);
    }

    static async Task SetDefault(HttpClient http, TokenCredential credential, string project, string name, string version)
    {
        await Json(http, credential, HttpMethod.Patch, $"{project}/skills/{Uri.EscapeDataString(name)}?api-version=v1",
            new JsonObject { ["default_version"] = version });
        if (await GetDefault(http, credential, project, name) != version)
            throw new InvalidOperationException($"Skill default readback mismatch: expected {version}.");
        Console.WriteLine($"Skill default read back: {name} / {version}");
    }

    static async Task<string> GetDefault(HttpClient http, TokenCredential credential, string project, string name) =>
        Required(await Json(http, credential, HttpMethod.Get, $"{project}/skills/{Uri.EscapeDataString(name)}?api-version=v1"), "default_version");

    static async Task<string> Consume(HttpClient http, TokenCredential credential, string project, string deployment,
        string skill, string consumer, string workshop, Dictionary<string, string> bodies, Dictionary<string, string> codes, string? pinned)
    {
        var resolved = pinned ?? await GetDefault(http, credential, project, skill);
        if (!bodies.TryGetValue(resolved, out var expectedInstructions)) throw new InvalidOperationException($"Consumer resolved an unrecognized skill version: {resolved}");
        var instructions = await DownloadBody(http, credential, project, skill, resolved, expectedInstructions);
        var agentName = "pricing-" + Guid.NewGuid().ToString("N")[..12];
        var agent = await Json(http, credential, HttpMethod.Post, $"{project}/agents/{agentName}/versions?api-version=v1",
            new JsonObject { ["definition"] = new JsonObject { ["kind"] = "prompt", ["model"] = deployment, ["instructions"] = Prefix + instructions, ["tools"] = new JsonArray() } });
        var agentVersion = Required(agent, "version");
        try
        {
            var response = await Json(http, credential, HttpMethod.Post, project + "/openai/v1/responses",
                new JsonObject
                {
                    ["input"] = $"The workshop ID is \"{workshop}\". What is its unit price? Echo the workshop ID exactly in the workshop field.",
                    ["tool_choice"] = "none",
                    ["agent_reference"] = new JsonObject { ["name"] = agentName, ["version"] = agentVersion, ["type"] = "agent_reference" }
                });
            var text = Text(response);
            if ((string?)response["status"] != "completed" || string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"Pricing agent failed: {response["status"]}; {response["error"]}");
            var value = JsonNode.Parse(text) as JsonObject;
            var price = workshop == "intro" ? 25 : 50;
            if (value is null || (string?)value["workshop"] != workshop ||
                (int?)value["unit_price"] != price || (string?)value["currency"] != "USD" ||
                (string?)value["skill_code"] != codes[resolved] || value.Count != 4)
                throw new InvalidOperationException($"Release gate failed: unexpected response {text}.");
            Console.WriteLine($"PASS {consumer}: {resolved} {workshop} {text}");
            return resolved;
        }
        finally { await Delete(http, credential, project, $"/agents/{Uri.EscapeDataString(agentName)}/versions/{Uri.EscapeDataString(agentVersion)}"); }
    }

    static string Text(JsonObject response)
    {
        foreach (var item in response["output"]?.AsArray() ?? [])
            foreach (var content in item?["content"]?.AsArray() ?? [])
                if ((string?)content?["type"] == "output_text") return (string?)content["text"] ?? "";
        return "";
    }

    static async Task<string> DownloadBody(HttpClient http, TokenCredential credential, string project, string name, string version, string expected)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{project}/skills/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(version)}/files?api-version=v1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.TryAddWithoutValidation("Foundry-Features", "Skills=V1Preview");
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Skill version download failed ({(int)response.StatusCode}).");
        using var buffer = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        var files = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToArray();
        if (files.Length != 1 || Path.GetFileName(files[0].FullName) != "SKILL.md")
            throw new InvalidDataException("Expected an instruction-only skill archive containing one SKILL.md.");
        using var reader = new StreamReader(files[0].Open(), Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        if (!text.Contains(expected, StringComparison.Ordinal))
            throw new InvalidDataException("Downloaded skill differs from the tested immutable instructions.");
        return text;
    }

    static async Task<JsonObject> Json(HttpClient http, TokenCredential credential, HttpMethod method, string url, JsonObject? body = null)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), CancellationToken.None);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.TryAddWithoutValidation("Foundry-Features", "Skills=V1Preview");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Foundry request failed ({(int)response.StatusCode}): {text}");
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    static async Task Delete(HttpClient http, TokenCredential credential, string project, string path)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Delete, project + path + "?api-version=v1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.TryAddWithoutValidation("Foundry-Features", "Skills=V1Preview");
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new HttpRequestException($"Foundry cleanup failed ({(int)response.StatusCode}).");
    }

    static string Required(JsonObject node, string key) =>
        node[key]?.GetValue<string>() ?? throw new InvalidDataException($"Foundry response is missing {key}.");

    static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    static int Error(string message) { Console.Error.WriteLine(message); return 2; }

    static int SelfTest()
    {
        if (!Body("v1").Contains("USD 25", StringComparison.Ordinal) ||
            !Body("v2").Contains("USD 25", StringComparison.Ordinal) ||
            !Body("v1").Contains("PRICING-V1", StringComparison.Ordinal) ||
            !Body("v2").Contains("PRICING-V2", StringComparison.Ordinal))
            throw new InvalidOperationException("Versioned pricing skill content changed unexpectedly.");
        Console.WriteLine("PASS: original pricing versions remain distinct and contain the expected release gate.");
        return 0;
    }
}
