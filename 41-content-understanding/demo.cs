#:property TargetFramework=net10.0
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const string ApiVersion = "2025-11-01";
var scriptRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory));
if (!File.Exists(Path.Combine(scriptRoot, "assets", "notice.pdf")))
    scriptRoot = Path.GetDirectoryName(Path.GetFullPath(Environment.ProcessPath!)) ?? Environment.CurrentDirectory;
var state = Path.Combine(scriptRoot, ".azure");
var supportedRegions = new[] { "australiaeast", "canadacentral", "eastus", "eastus2", "japaneast", "southcentralus",
    "southeastasia", "swedencentral", "uksouth", "westeurope", "westus", "westus3" };
var expected = new Dictionary<string, object> { ["Workshop"] = "Cedar Workshop", ["Room"] = "Blue Room", ["Seats"] = 12 };

if (args.Contains("--self-test"))
{
    RunOfflineTests();
    return;
}

var help = args.Contains("--help") || args.Contains("-h");
if (help)
{
    Console.WriteLine("GA Content Understanding extraction\nUsage: dotnet run --file demo.cs -- [--preflight] [--location REGION]");
    return;
}

var preflight = args.Contains("--preflight");
var location = Option(args, "--location") ?? "eastus2";
Directory.CreateDirectory(state);
try
{
    var report = preflight
        ? await Preflight(location, Environment.GetEnvironmentVariable("CU_COMPLETION_MODEL") ?? "gpt-5.2",
            Environment.GetEnvironmentVariable("CU_EMBEDDING_MODEL") ?? "text-embedding-3-large")
        : await Live();
    await WriteJson(Path.Combine(state, preflight ? "preflight.json" : "live-report.json"), report);
    Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
catch (Exception error)
{
    var reportPath = Path.Combine(state, preflight ? "preflight.json" : "live-report.json");
    var report = !preflight && File.Exists(reportPath) ? JsonNode.Parse(await File.ReadAllTextAsync(reportPath))!.AsObject() : new JsonObject();
    report["status"] = "BLOCKED";
    report["stage"] = preflight ? "preflight" : "GA-native-CU";
    report["errorType"] = error.GetType().Name;
    report["error"] = error.Message;
    await WriteJson(reportPath, report);
    Console.Error.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = 1;
}

static string? Option(string[] arguments, string option)
{
    var index = Array.IndexOf(arguments, option);
    if (index < 0) return null;
    if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"{option} requires a value.");
    return arguments[index + 1];
}

async Task<JsonObject> Preflight(string region, string completion, string embedding)
{
    if (!supportedRegions.Contains(region, StringComparer.Ordinal))
        throw new InvalidOperationException($"BLOCKED: GA Content Understanding is not documented in {region}; choose {string.Join(", ", supportedRegions.Order())}.");
    var account = await AzCli.Run("account", "show", "--query", "{subscription:id,tenant:tenantId}", "-o", "json");
    var catalog = await AzCli.Run("cognitiveservices", "model", "list", "--location", region, "-o", "json");
    var usage = await AzCli.Run("cognitiveservices", "usage", "list", "--location", region, "-o", "json");
    using var catalogDoc = JsonDocument.Parse(catalog);
    using var usageDoc = JsonDocument.Parse(usage);
    var models = new JsonArray();
    foreach (var model in new[] { completion, embedding })
    {
        var versions = catalogDoc.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("kind", out var kind) && kind.GetString() == "AIServices"
                && item.TryGetProperty("model", out var mdl) && mdl.TryGetProperty("name", out var name) && name.GetString() == model)
            .Select(item => item.GetProperty("model").GetProperty("version").GetString() ?? "")
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        var required = new Dictionary<string, string> { ["gpt-5.2"] = "2025-12-11", ["gpt-4o-mini"] = "2024-07-18",
            ["gpt-5.4-mini"] = "2026-03-17", ["text-embedding-3-large"] = "1" }.GetValueOrDefault(model);
        var quota = usageDoc.RootElement.EnumerateArray().FirstOrDefault(item =>
            item.TryGetProperty("name", out var name) && name.TryGetProperty("value", out var value)
            && value.GetString() == $"OpenAI.GlobalStandard.{model}");
        if (versions.Length == 0 || (required is not null && !versions.Contains(required, StringComparer.Ordinal))
            || quota.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"BLOCKED: {model} GlobalStandard capacity-10 deployment/version is unavailable in {region}; no resources provisioned by preflight.");
        var remaining = quota.GetProperty("limit").GetInt32() - quota.GetProperty("currentValue").GetInt32();
        if (remaining < 10)
            throw new InvalidOperationException($"BLOCKED: {model} GlobalStandard capacity-10 deployment/version is unavailable in {region}; no resources provisioned by preflight.");
        var versionNodes = new JsonArray();
        foreach (var version in versions) versionNodes.Add(version);
        models.Add(new JsonObject { ["name"] = model, ["versions"] = versionNodes, ["remainingCapacity"] = remaining });
    }
    var result = JsonNode.Parse(account)!.AsObject();
    result["status"] = "PASS";
    result["stage"] = "local-model-quota-preflight";
    result["apiVersion"] = ApiVersion;
    result["location"] = region;
    result["models"] = models;
    return result;
}

async Task<JsonObject> Live()
{
    var endpoint = Setting("CONTENT_UNDERSTANDING_ENDPOINT", "endpoint")
        ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")?.Trim();
    if (string.IsNullOrWhiteSpace(endpoint))
        throw new InvalidOperationException("Deploy infra/main.bicep first; .azure/deployment.json or CONTENT_UNDERSTANDING_ENDPOINT is required.");
    var completion = Setting("CU_COMPLETION_MODEL", "completionModelName") ?? "gpt-5.2";
    var embedding = Setting("CU_EMBEDDING_MODEL", "embeddingModelName") ?? "text-embedding-3-large";
    var completionDeployment = Setting("AZURE_OPENAI_DEPLOYMENT", "deployment") ?? "cu-ga-completion";
    var embeddingDeployment = Setting("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "embeddingDeployment") ?? "cu-embedding";
    var report = new JsonObject { ["status"] = "RUNNING", ["stage"] = "GA-native-CU", ["apiVersion"] = ApiVersion, ["modalities"] = new JsonObject() };
    await WriteJson(Path.Combine(state, "live-report.json"), report);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    var client = new CuClient(endpoint, http);
    Console.WriteLine("Configuring native CU model deployment defaults.");
    var defaults = await ConfigureDefaults(client, new JsonObject {
        [completion] = completionDeployment, [embedding] = embeddingDeployment,
        ["prebuilt-analyzer-completion"] = completionDeployment, ["prebuilt-analyzer-completion-mini"] = completionDeployment,
        ["prebuilt-analyzer-embedding"] = embeddingDeployment
    });
    await WriteJson(Path.Combine(state, "defaults.json"), defaults);
    foreach (var (modality, asset, contentType) in new[] {
        ("document", "notice.pdf", "application/pdf"), ("audio", "notice.wav", "audio/wav")
    })
    {
        var modelSlug = completion.Replace("-", "_").Replace(".", "_");
        var analyzerId = $"workshop_{modality}_{modelSlug}";
        var schema = AnalyzerSchema(modality, completion, embedding);
        Console.WriteLine($"Checking native {modality} analyzer {analyzerId}.");
        var analyzer = await EnsureAnalyzer(client, analyzerId, schema);
        await WriteJson(Path.Combine(state, $"{modality}-analyzer.json"), analyzer);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(scriptRoot, "assets", asset));
        Console.WriteLine($"Submitting real {modality} bytes to GA analyzeBinary.");
        using var submitted = await client.Request(HttpMethod.Post, $"analyzers/{analyzerId}:analyzeBinary",
            bytes, contentType);
        await WriteJson(Path.Combine(state, $"{modality}-operation.json"), new JsonObject {
            ["statusCode"] = (int)submitted.StatusCode,
            ["operationLocation"] = submitted.Headers.Location?.ToString() ?? submitted.Headers.GetValues("Operation-Location").FirstOrDefault(),
            ["analyzerId"] = analyzerId, ["assetSha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        });
        var operation = await Poll(client, submitted, 600, Path.Combine(state, $"{modality}-poll.json"));
        await WriteJson(Path.Combine(state, $"{modality}-response.json"), operation);
        var facts = ValidateResult(operation, modality);
        ((JsonObject)report["modalities"]!)[modality] = new JsonObject {
            ["status"] = "PASS", ["analyzerId"] = analyzerId, ["operationId"] = operation["id"]?.DeepClone(),
            ["assetSha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), ["fields"] = facts
        };
        await WriteJson(Path.Combine(state, "live-report.json"), report);
    }
    report["status"] = "PASS";
    return report;
}

string? Setting(string environment, string output)
{
    var value = Environment.GetEnvironmentVariable(environment)?.Trim();
    if (!string.IsNullOrEmpty(value)) return value;
    var file = Path.Combine(state, "deployment.json");
    if (!File.Exists(file)) return null;
    var outputs = JsonNode.Parse(File.ReadAllText(file))?["outputs"]?[output]?["value"];
    return outputs?.GetValue<string>();
}

static JsonObject AnalyzerSchema(string modality, string completion, string embedding)
{
    if (modality is not ("document" or "audio")) throw new ArgumentException("Only document and audio analyzers are supported.");
    var method = modality == "document" ? "extract" : "generate";
    var fields = new JsonObject();
    foreach (var (name, type, description) in new[] {
        ("Workshop", "string", "Workshop name explicitly stated in the content, without labels."),
        ("Room", "string", "Room name explicitly stated in the content, without labels."),
        ("Seats", "integer", "Number of available seats explicitly stated in the content.")
    })
        fields[name] = new JsonObject { ["type"] = type, ["method"] = method,
            ["description"] = description + (modality == "audio" ? " Do not infer missing facts." : "") };
    var config = new JsonObject { ["returnDetails"] = true };
    if (modality == "document") config["estimateFieldSourceAndConfidence"] = true;
    else config["locales"] = new JsonArray("en-US");
    return new JsonObject {
        ["description"] = $"Synthetic workshop {modality} schema extraction; no personal data.",
        ["baseAnalyzerId"] = $"prebuilt-{modality}",
        ["models"] = new JsonObject { ["completion"] = completion, ["embedding"] = embedding },
        ["config"] = config,
        ["fieldSchema"] = new JsonObject { ["name"] = "WorkshopNotice", ["fields"] = fields }
    };
}

static void ValidateAnalyzer(JsonObject stored, JsonObject requested)
{
    if (stored["baseAnalyzerId"]?.ToString() != requested["baseAnalyzerId"]?.ToString()
        || !JsonNode.DeepEquals(stored["models"], requested["models"]))
        throw new InvalidDataException("Persisted native analyzer base/model configuration differs from requested schema.");
    var fields = stored["fieldSchema"]?["fields"] as JsonObject;
    var wanted = requested["fieldSchema"]!["fields"]!.AsObject();
    if (fields is null || !fields.Select(pair => pair.Key).ToHashSet().SetEquals(wanted.Select(pair => pair.Key))
        || wanted.Any(pair => fields[pair.Key]?["method"]?.ToString() != pair.Value?["method"]?.ToString()
            || fields[pair.Key]?["type"]?.ToString() != pair.Value?["type"]?.ToString()))
        throw new InvalidDataException("Persisted analyzer must contain the exact custom extraction field schema.");
    if (requested["baseAnalyzerId"]!.ToString() == "prebuilt-document"
        && stored["config"]?["estimateFieldSourceAndConfidence"]?.GetValue<bool>() != true)
        throw new InvalidDataException("Persisted document analyzer has no confidence/source estimation enabled.");
}

JsonObject ValidateResult(JsonObject body, string modality)
{
    if (!string.Equals(body["status"]?.ToString(), "Succeeded", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"Analysis did not succeed: {body["status"]}");
    if (string.IsNullOrEmpty(body["id"]?.ToString())) throw new InvalidDataException("Service operation ID is missing.");
    var result = body["result"]?.AsObject() ?? throw new InvalidDataException("Analysis result is missing.");
    if (result["apiVersion"]?.ToString() != ApiVersion) throw new InvalidDataException("Result must be from the GA API version.");
    var contents = result["contents"]?.AsArray();
    if (contents?.Count != 1) throw new InvalidDataException("Expected exactly one content segment.");
    var content = contents[0]!.AsObject();
    var markdown = content["markdown"]?.ToString();
    if (string.IsNullOrWhiteSpace(markdown)) throw new InvalidDataException("Service content evidence is missing.");
    var facts = new JsonObject();
    foreach (var (name, expectedValue) in expected)
    {
        var field = content["fields"]?[name]?.AsObject() ?? throw new InvalidDataException($"{name}: service field missing.");
        var value = name == "Seats" ? field["valueInteger"]?.GetValue<int>().ToString(CultureInfo.InvariantCulture) : field["valueString"]?.ToString();
        if (value is null || Normalize(value) != Normalize(Convert.ToString(expectedValue, CultureInfo.InvariantCulture)!))
            throw new InvalidDataException($"{modality} {name}: expected '{expectedValue}', extracted {value ?? "null"}.");
        var confidence = field["confidence"];
        var source = field["source"]?.ToString();
        if (modality == "document")
        {
            if (confidence is null || !double.TryParse(confidence.ToString(), CultureInfo.InvariantCulture, out var score)
                || !double.IsFinite(score) || score is < 0 or > 1)
                throw new InvalidDataException($"{name}: missing or invalid real service confidence.");
            if (score < .8) throw new InvalidDataException($"{name}: confidence {score} requires human review.");
            if (source is null || !Regex.IsMatch(source, @"^D\([0-9., -]+\)(?:;\s*D\([0-9., -]+\))*$"))
                throw new InvalidDataException($"{name}: document source grounding is missing or invalid.");
            var pages = content["pages"]?.AsArray().Select(p => p?["pageNumber"]?.GetValue<int>()).ToHashSet() ?? [];
            foreach (Match page in Regex.Matches(source, @"D\((\d+),"))
                if (!pages.Contains(int.Parse(page.Groups[1].Value, CultureInfo.InvariantCulture)))
                    throw new InvalidDataException($"{name}: source references a missing service page.");
            foreach (Match polygon in Regex.Matches(source, @"D\(([^)]+)\)"))
            {
                var coordinates = polygon.Groups[1].Value.Split(',');
                if (coordinates.Length != 9 || coordinates.Any(n => !double.TryParse(n, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)))
                    throw new InvalidDataException($"{name}: source must contain a real page and eight finite polygon coordinates.");
            }
            if (!Normalize(markdown).Contains(Normalize(Convert.ToString(expectedValue, CultureInfo.InvariantCulture)!), StringComparison.Ordinal))
                throw new InvalidDataException($"{name}: value has no matching service document text.");
            facts[name] = new JsonObject { ["value"] = name == "Seats" ? JsonValue.Create(int.Parse(value, CultureInfo.InvariantCulture)) : JsonValue.Create(value),
                ["confidence"] = score, ["source"] = source };
        }
        else
        {
            var phrases = content["transcriptPhrases"]?.AsArray() ?? throw new InvalidDataException($"{name}: no matching time-grounded service transcript phrase.");
            var matches = new JsonArray();
            foreach (var phrase in phrases)
            {
                var text = phrase?["text"]?.ToString() ?? "";
                var start = Number(phrase?["startTimeMs"]);
                var end = Number(phrase?["endTimeMs"]);
                var target = name == "Seats" ? new[] { Normalize(value), "twelve" } : new[] { Normalize(value) };
                if (target.Any(t => Normalize(text).Contains(t, StringComparison.Ordinal)) && start is >= 0 && end > start)
                    matches.Add(phrase!.DeepClone());
            }
            if (matches.Count == 0) throw new InvalidDataException($"{name}: no matching time-grounded service transcript phrase.");
            double? score = confidence is not null && double.TryParse(confidence.ToString(), CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            if (confidence is not null && (score is null || !double.IsFinite(score.Value) || score is < 0 or > 1))
                throw new InvalidDataException($"{name}: invalid service audio confidence.");
            facts[name] = new JsonObject { ["value"] = name == "Seats" ? JsonValue.Create(int.Parse(value, CultureInfo.InvariantCulture)) : JsonValue.Create(value),
                ["confidence"] = score, ["source"] = source, ["confidenceAvailability"] = score is null ? "not-emitted-by-GA-audio" : "service-emitted",
                ["transcriptPhrases"] = matches };
        }
    }
    return facts;
}

static double? Number(JsonNode? node) =>
    node is not null && double.TryParse(node.ToString(), CultureInfo.InvariantCulture, out var n) ? n : null;

static string Normalize(string value) => string.Join(' ', Regex.Matches(value.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value));

static async Task<JsonObject> EnsureAnalyzer(CuClient client, string id, JsonObject requested)
{
    using var existing = await client.Request(HttpMethod.Get, $"analyzers/{id}", allow: [HttpStatusCode.NotFound]);
    if (existing.StatusCode == HttpStatusCode.NotFound)
    {
        using var created = await client.Request(HttpMethod.Put, $"analyzers/{id}", Encoding.UTF8.GetBytes(requested.ToJsonString()), "application/json");
        await Poll(client, created, 600, null);
        using var readback = await client.Request(HttpMethod.Get, $"analyzers/{id}");
        var stored = (JsonObject)JsonNode.Parse(await readback.Content.ReadAsStringAsync())!;
        ValidateAnalyzer(stored, requested);
        return stored;
    }
    var analyzer = (JsonObject)JsonNode.Parse(await existing.Content.ReadAsStringAsync())!;
    ValidateAnalyzer(analyzer, requested);
    return analyzer;
}

static async Task<JsonObject> ConfigureDefaults(CuClient client, JsonObject deployments)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(180);
    while (true)
    {
        using var response = await client.Request(HttpMethod.Patch, "defaults", Encoding.UTF8.GetBytes(new JsonObject { ["modelDeployments"] = deployments }.ToJsonString()), "application/json", [HttpStatusCode.BadRequest]);
        if (!response.IsSuccessStatusCode) {
            var body = await response.Content.ReadAsStringAsync();
            var error = JsonNode.Parse(body)?["error"]?["innererror"];
            var message = error?["message"]?.ToString() ?? "";
            var match = Regex.Match(message, @"OpenAI deployment '([^']+)' does not exist");
            if (error?["code"]?.ToString() != "DeploymentIdNotFound" || !match.Success
                || !deployments.AsObject().Any(p => p.Value?.ToString() == match.Groups[1].Value)
                || DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException($"CU HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 2500)]}");
            Console.WriteLine($"Waiting for deployment {match.Groups[1].Value} to become visible to Content Understanding.");
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, (deadline - DateTimeOffset.UtcNow).TotalSeconds)));
            continue;
        }
        return (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }
}

static async Task<JsonObject> Poll(CuClient client, HttpResponseMessage submitted, int timeout, string? evidencePath)
{
    var operation = submitted.Headers.TryGetValues("Operation-Location", out var values) ? values.FirstOrDefault() : null;
    operation ??= submitted.Headers.Location?.ToString();
    if (operation is null)
    {
        if (submitted.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            return (JsonObject)JsonNode.Parse(await submitted.Content.ReadAsStringAsync())!;
        throw new InvalidOperationException("Missing Content Understanding Operation-Location.");
    }
    var uri = new Uri(operation, UriKind.Absolute);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
    while (DateTimeOffset.UtcNow < deadline)
    {
        using var response = await client.Request(HttpMethod.Get, uri.ToString());
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        if (evidencePath is not null) await WriteJson(evidencePath, body);
        var status = body["status"]?.ToString().ToLowerInvariant();
        if (status == "succeeded") return body;
        if (status is "failed" or "canceled" or "cancelled")
            throw new InvalidOperationException($"CU operation failed: {body.ToJsonString()[..Math.Min(body.ToJsonString().Length, 2500)]}");
        var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 2;
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retry, 1, 5)));
    }
    throw new TimeoutException($"Content Understanding operation exceeded bounded {timeout}-second deadline.");
}

static async Task WriteJson(string path, JsonNode value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var temporary = path + ".pending";
    await File.WriteAllTextAsync(temporary, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    File.Move(temporary, path, true);
}

void RunOfflineTests()
{
    var document = AnalyzerSchema("document", "gpt-5.2", "text-embedding-3-large");
    var audio = AnalyzerSchema("audio", "gpt-5.2", "text-embedding-3-large");
    ValidateAnalyzer(document, document);
    ValidateAnalyzer(audio, audio);
    if (document["fieldSchema"]!["fields"]!["Seats"]!["method"]!.ToString() != "extract"
        || audio["fieldSchema"]!["fields"]!["Seats"]!["method"]!.ToString() != "generate"
        || document["config"]!["estimateFieldSourceAndConfidence"]!.GetValue<bool>() != true
        || audio["config"]!["locales"]![0]!.ToString() != "en-US")
        throw new InvalidDataException("Analyzer modality contract failed.");
    var badAnalyzer = document.DeepClone().AsObject();
    badAnalyzer["fieldSchema"]!["fields"]!["Seats"]!["method"] = "generate";
    ExpectFailure(() => ValidateAnalyzer(badAnalyzer, document), "custom extraction");
    var wrongVersion = ResultFixture();
    wrongVersion["result"]!["apiVersion"] = "2026-06-01-preview";
    ExpectFailure(() => ValidateResult(wrongVersion, "document"), "GA API");
    var wrongValue = ResultFixture();
    wrongValue["result"]!["contents"]![0]!["fields"]!["Seats"]!["valueInteger"] = 13;
    ExpectFailure(() => ValidateResult(wrongValue, "document"), "expected");
    var lowConfidence = ResultFixture();
    lowConfidence["result"]!["contents"]![0]!["fields"]!["Seats"]!["confidence"] = 0.79;
    ExpectFailure(() => ValidateResult(lowConfidence, "document"), "human review");
    var shortPolygon = ResultFixture();
    shortPolygon["result"]!["contents"]![0]!["fields"]!["Seats"]!["source"] = "D(1,1,2)";
    ExpectFailure(() => ValidateResult(shortPolygon, "document"), "polygon");
    var ungroundedAudio = ResultFixture("audio");
    ungroundedAudio["result"]!["contents"]![0]!["transcriptPhrases"]![2]!["endTimeMs"] = null;
    ExpectFailure(() => ValidateResult(ungroundedAudio, "audio"), "time-grounded");
    var pdfText = File.ReadAllText(Path.Combine(scriptRoot, "assets", "notice.txt"));
    if (!File.ReadAllBytes(Path.Combine(scriptRoot, "assets", "notice.pdf")).SequenceEqual(PdfBytes(pdfText)))
        throw new InvalidDataException("Committed PDF fixture differs from its deterministic source.");
    var wav = File.ReadAllBytes(Path.Combine(scriptRoot, "assets", "notice.wav"));
    if (Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE"
        || BitConverter.ToInt16(wav, 22) != 1 || BitConverter.ToInt32(wav, 24) != 24000
        || BitConverter.ToInt16(wav, 34) != 16 || wav.Length <= 48000 || wav.Length >= 24000 * 30 * 2)
        throw new InvalidDataException("Committed audio fixture format changed.");
    Console.WriteLine("PASS: Content Understanding offline contracts and committed asset formats.");
}

static byte[] PdfBytes(string text)
{
    var commands = new List<string> { "BT /F1 18 Tf 72 740 Td" };
    var lines = text.Split('\n');
    if (text.EndsWith('\n')) lines = lines[..^1];
    for (var index = 0; index < lines.Length; index++)
    {
        var escaped = lines[index].TrimEnd('\r').Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        if (index > 0) commands.Add("0 -30 Td");
        commands.Add($"({escaped}) Tj");
    }
    var stream = Encoding.ASCII.GetBytes(string.Join('\n', commands.Append("ET")) + "\n");
    var objects = new[] {
        Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
        Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
        Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        Encoding.ASCII.GetBytes($"<< /Length {stream.Length} >>\nstream\n").Concat(stream).Concat(Encoding.ASCII.GetBytes("endstream")).ToArray()
    };
    using var output = new MemoryStream();
    output.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
    var offsets = new List<int> { 0 };
    for (var index = 0; index < objects.Length; index++)
    {
        offsets.Add((int)output.Position);
        output.Write(Encoding.ASCII.GetBytes($"{index + 1} 0 obj\n"));
        output.Write(objects[index]);
        output.Write(Encoding.ASCII.GetBytes("\nendobj\n"));
    }
    var start = output.Position;
    output.Write(Encoding.ASCII.GetBytes($"xref\n0 {offsets.Count}\n0000000000 65535 f \n"));
    foreach (var offset in offsets.Skip(1)) output.Write(Encoding.ASCII.GetBytes($"{offset:0000000000} 00000 n \n"));
    output.Write(Encoding.ASCII.GetBytes($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF\n"));
    return output.ToArray();
}

static void ExpectFailure(Action action, string message)
{
    try { action(); }
    catch (Exception error) when (error.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new InvalidDataException($"Expected failure containing '{message}'.");
}

static JsonObject ResultFixture(string modality = "document")
{
    var fields = new JsonObject {
        ["Workshop"] = new JsonObject { ["valueString"] = "Cedar Workshop", ["confidence"] = .98, ["source"] = "D(1,1,1,2,1,2,2,1,2)" },
        ["Room"] = new JsonObject { ["valueString"] = "Blue Room", ["confidence"] = .99, ["source"] = "D(1,1,2,2,2,2,3,1,3)" },
        ["Seats"] = new JsonObject { ["valueInteger"] = 12, ["confidence"] = .97, ["source"] = "D(1,1,3,2,3,2,4,1,4)" }
    };
    var content = new JsonObject { ["markdown"] = "Cedar Workshop. Blue Room. 12 seats.", ["fields"] = fields,
        ["pages"] = new JsonArray(new JsonObject { ["pageNumber"] = 1 }) };
    if (modality == "audio")
    {
        content["transcriptPhrases"] = new JsonArray(
            new JsonObject { ["startTimeMs"] = 0, ["endTimeMs"] = 3000, ["text"] = "Cedar Workshop." },
            new JsonObject { ["startTimeMs"] = 3000, ["endTimeMs"] = 5000, ["text"] = "Blue Room." },
            new JsonObject { ["startTimeMs"] = 5000, ["endTimeMs"] = 6500, ["text"] = "Twelve seats." });
        foreach (var field in fields.Select(pair => pair.Value!.AsObject()))
        { field.Remove("confidence"); field.Remove("source"); }
    }
    return new JsonObject { ["status"] = "Succeeded", ["id"] = "offline-unit-fixture-only",
        ["result"] = new JsonObject { ["apiVersion"] = ApiVersion, ["contents"] = new JsonArray(content) } };
}

sealed class CuClient(string endpoint, HttpClient http)
{
    private const string ApiVersion = "2025-11-01";
    private readonly Uri _endpoint = new(endpoint.TrimEnd('/') + "/");
    public async Task<HttpResponseMessage> Request(HttpMethod method, string route, byte[]? body = null, string? contentType = null,
        HttpStatusCode[]? allow = null)
    {
        var uriBuilder = route.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(route) : new UriBuilder(new Uri(_endpoint, $"contentunderstanding/{route.TrimStart('/')}"));
        var query = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query);
        query["api-version"] = ApiVersion;
        uriBuilder.Query = query.ToString();
        var uri = uriBuilder.Uri;
        if (uri.Scheme != "https" || !string.Equals(uri.Host, _endpoint.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to send Entra token to an untrusted operation URL.");
        var token = await AzCli.Run("account", "get-access-token", "--scope", "https://cognitiveservices.azure.com/.default", "--query", "accessToken", "-o", "tsv");
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                response?.Dispose();
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
                if (method == HttpMethod.Get && uri.AbsolutePath.Contains("/analyzerResults/", StringComparison.Ordinal))
                    request.Headers.TryAddWithoutValidation("x-ms-diagnostics", "true");
                if (body is not null)
                {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/json");
                }
                response = await http.SendAsync(request);
                if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                    || attempt == 2) break;
            }
            catch (HttpRequestException error) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
                if (attempt == 1) throw new InvalidOperationException($"CU {method} {uri.AbsolutePath}: transport failed after three real attempts: {error.Message}", error);
            }
            if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
        }
        if (response is null) throw new InvalidOperationException("Content Understanding request returned no response.");
        var responseStatus = response.StatusCode;
        if (!response.IsSuccessStatusCode && !(allow?.Contains(responseStatus) ?? false))
        {
            var text = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new InvalidOperationException($"CU HTTP {(int)responseStatus}: {text[..Math.Min(text.Length, 2500)]}");
        }
        return response;
    }
}

static class AzCli
{
    public static async Task<string> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Azure CLI.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Azure CLI failed ({process.ExitCode}): {stderr.Trim()}");
        return stdout;
    }
}
