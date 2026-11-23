using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class VoiceDemo
{
    private static readonly string Lab = Directory.GetCurrentDirectory();
    private static readonly string State = Path.Combine(Lab, ".azure");
    private const int SampleRate = 24000;
    private const string ApiVersion = "v1";
    private const string Feature = "VoiceAgents=V1Preview";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record AudioData(byte[] Pcm, int Channels, int Bits, int Rate);
    private sealed record ServiceResponse(int Status, JsonElement Body, byte[] Bytes, Dictionary<string, string> Headers);

    private static JsonElement ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static JsonElement Outputs()
    {
        var path = Path.Combine(State, "deployment.json");
        return File.Exists(path) ? ReadJson(path).GetProperty("outputs") : JsonDocument.Parse("{}").RootElement.Clone();
    }
    private static string Output(JsonElement outputs, string key) =>
        outputs.TryGetProperty(key, out var item) && item.TryGetProperty("value", out var value) ? value.GetString() ?? "" : "";
    private static string SelectSetting(string environmentValue, string outputValue, string fallback = "") =>
        !string.IsNullOrWhiteSpace(environmentValue) ? environmentValue.Trim()
        : outputValue.Length > 0 ? outputValue : fallback;
    private static string Setting(string environment, string output, string fallback = "")
        => SelectSetting(Environment.GetEnvironmentVariable(environment) ?? "", Output(Outputs(), output), fallback);

    private static AudioData ReadPcm(string path)
    {
        using var stream = File.OpenRead(path);
        return ParsePcm(stream);
    }

    private static AudioData ParsePcm(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Input must be a RIFF WAV file.");
        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Input must be a WAV file.");
        int channels = 0, bits = 0, rate = 0;
        byte[]? pcm = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunk = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            if (size > stream.Length - stream.Position) throw new InvalidDataException("Input WAV has a malformed chunk.");
            var end = stream.Position + size;
            if (chunk == "fmt ")
            {
                var format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = checked((int)reader.ReadUInt32());
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format != 1) throw new InvalidDataException("Input must use uncompressed PCM.");
            }
            else if (chunk == "data") pcm = reader.ReadBytes(checked((int)size));
            stream.Position = end + (size & 1);
        }
        if ((channels, bits, rate) != (1, 16, SampleRate)) throw new InvalidDataException("Input must be mono PCM16 WAV at 24000 Hz.");
        if (pcm is null || pcm.Length == 0 || (pcm.Length & 1) != 0 || !pcm.Any(value => value != 0))
            throw new InvalidDataException("Spoken fixture is empty, malformed or silent.");
        if (pcm.Length > SampleRate * 2 * 30) throw new InvalidDataException("Spoken smoke fixture must be at most 30 seconds.");
        return new AudioData(pcm, channels, bits, rate);
    }

    private static bool ContainsCode(string transcript) => Regex.IsMatch(transcript, @"\b(?:forty|40)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static byte[] MakeWav(byte[] pcm)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Ascii("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Ascii("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Ascii("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        return stream.ToArray();
    }

    private static string Token(string resource)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "account", "get-access-token", "--resource", resource, "-o", "json" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Azure CLI.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Azure CLI token acquisition failed: {stderr.Trim()}");
        return JsonDocument.Parse(stdout).RootElement.GetProperty("accessToken").GetString()!;
    }

    private static HttpClient NewProjectClient(string token)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("Foundry-Features", Feature);
        return client;
    }

    private static async Task<ServiceResponse> Send(HttpClient client, HttpMethod method, string url, object? body = null, int timeoutSeconds = 60)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellation.Token);
        JsonElement json;
        try { json = JsonDocument.Parse(bytes).RootElement.Clone(); }
        catch (JsonException) { json = JsonSerializer.SerializeToElement(new { non_json_body = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4000)) }); }
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(item => item.Key, item => string.Join(",", item.Value), StringComparer.OrdinalIgnoreCase);
        return new ServiceResponse((int)response.StatusCode, json, bytes, headers);
    }

    private static void RequireSuccess(ServiceResponse response, string action)
    {
        if (response.Status is < 200 or >= 300)
            throw new HttpRequestException($"{action} failed with HTTP {response.Status}: {response.Body}",
                null, (HttpStatusCode)response.Status);
    }

    private static object VoiceDefinition(string model) => new
    {
        kind = "voice",
        model_type = "managed",
        model,
        instructions = "You are a friendly workshop voice assistant. Repeat the workshop code the caller tells you, then say a short greeting. Reply in one short sentence.",
        audio = new
        {
            input = new
            {
                format = new { type = "audio/pcm", rate = SampleRate },
                turn_detection = new { type = "server_vad", threshold = 0.5, prefix_padding_ms = 300, silence_duration_ms = 700, create_response = true, interrupt_response = false },
                transcription = new { model = "azure-speech" }
            },
            output = new { voice = "en-US-AvaNeural", voice_type = "azure-standard" }
        },
        output_modalities = new[] { "audio" },
        store = true
    };

    private static async Task<JsonElement> Preflight(string location, string model)
    {
        if (model is not ("gpt-realtime" or "gpt-realtime-2.1"))
            throw new ArgumentException("Use a documented service-managed voice model: gpt-realtime or gpt-realtime-2.1.");
        var identityText = await Az("account", "show", "--query", "{subscription:id,tenant:tenantId}", "-o", "json");
        var catalogText = await Az("cognitiveservices", "model", "list", "--location", location, "-o", "json");
        using var identity = JsonDocument.Parse(identityText);
        using var catalog = JsonDocument.Parse(catalogText);
        var versions = catalog.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("model", out var value) && value.TryGetProperty("name", out var name) && name.GetString() == model)
            .Select(item => item.TryGetProperty("model", out var itemModel) ? GetString(itemModel, "version") : "")
            .Distinct().Order().ToArray();
        if (versions.Length == 0) throw new InvalidOperationException($"BLOCKED: {model} not in the actual {location} model catalog. Choose a documented supported region/model before provisioning.");
        _ = ReadPcm(Path.Combine(Lab, "assets", "turn.wav"));
        var identityValue = identity.RootElement;
        return JsonSerializer.SerializeToElement(new
        {
            status = "PASS", stage = "local-and-model-preflight", location,
            subscription = identityValue.GetProperty("subscription").GetString(),
            tenant = identityValue.GetProperty("tenant").GetString(),
            model, versions,
            previewAccess = "NOT_VERIFIED: no subscription-wide voice access probe is documented; actual native agent creation is authoritative.",
            customerModelDeployment = false
        });
    }

    private static async Task<string> Az(params string[] args)
    {
        var start = new ProcessStartInfo("az") { WorkingDirectory = Lab, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Azure CLI.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"az {string.Join(' ', args)} failed: {stderr.Trim()}");
        return stdout;
    }

    private static async Task<JsonElement> Live()
    {
        var endpoint = Setting("FOUNDRY_PROJECT_ENDPOINT", "projectEndpoint");
        var model = Setting("FOUNDRY_VOICE_AGENT_MODEL", "deployment", "gpt-realtime");
        if (endpoint.Length == 0) throw new InvalidOperationException("Deploy infra/main.bicep first; FOUNDRY_PROJECT_ENDPOINT or .azure/deployment.json is required.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var projectUri) || projectUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT must be an absolute HTTPS endpoint.");
        var agentName = Environment.GetEnvironmentVariable("FOUNDRY_VOICE_AGENT_NAME") ?? "workshop-managed-voice";
        var pcm = ReadPcm(Path.Combine(Lab, "assets", "turn.wav")).Pcm;
        var evidence = new Dictionary<string, object?>
        {
            ["stage"] = "native-voice", ["model"] = model, ["agentName"] = agentName,
            ["inputSha256"] = Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant()
        };
        using var client = NewProjectClient(Token("https://ai.azure.com/"));
        var created = await Send(client, HttpMethod.Post,
            $"{endpoint.TrimEnd('/')}/agents/{Uri.EscapeDataString(agentName)}/versions?api-version={ApiVersion}",
            new { definition = VoiceDefinition(model) });
        RequireSuccess(created, "Native managed voice agent creation");
        var version = created.Body.TryGetProperty("version", out var v) ? v.GetString() ?? "" :
            created.Body.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array && versions.GetArrayLength() > 0
                ? versions[0].GetProperty("version").GetString() ?? "" : "";
        if (version.Length == 0) throw new InvalidOperationException($"Native agent create response omitted its version: {created.Body}");
        var savedVersion = await Send(client, HttpMethod.Get,
            $"{endpoint.TrimEnd('/')}/agents/{Uri.EscapeDataString(agentName)}/versions/{Uri.EscapeDataString(version)}?api-version={ApiVersion}");
        RequireSuccess(savedVersion, "Native voice agent version readback");
        var persistedDefinition = savedVersion.Body.GetProperty("definition");
        var kind = GetString(persistedDefinition, "kind");
        if (kind != "voice" || GetString(persistedDefinition, "model_type") != "managed"
            || GetString(persistedDefinition, "model") != model
            || !persistedDefinition.TryGetProperty("store", out var stored) || stored.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("The saved native voice agent did not persist its managed model/audio-session definition.");
        evidence["agentVersion"] = version;
        evidence["agentKind"] = kind;
        Directory.CreateDirectory(State);
        await File.WriteAllTextAsync(Path.Combine(State, "agent.json"), JsonSerializer.Serialize(evidence, JsonOptions));

        var socketUrl = new Uri($"{endpoint.TrimEnd('/')}/agents/{Uri.EscapeDataString(agentName)}/endpoint/protocols/voice?api-version={ApiVersion}&store=true");
        var builder = new UriBuilder(socketUrl) { Scheme = socketUrl.Scheme == "https" ? "wss" : "ws" };
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + Token("https://ai.azure.com/"));
        socket.Options.SetRequestHeader("Foundry-Features", Feature);
        await socket.ConnectAsync(builder.Uri, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        var events = new List<string>();
        var chunks = new List<byte[]>();
        string inputTranscript = "", outputTranscript = "", conversationId = "", sessionId = "";
        var first = await ReceiveEvent(socket, TimeSpan.FromSeconds(30));
        if (GetString(first, "type") != "session.created") throw new InvalidOperationException($"Expected session.created, received {GetString(first, "type")}.");
        events.Add(GetString(first, "type"));
        conversationId = GetString(first, "conversation_id");
        if (first.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object)
            sessionId = GetString(session, "id");
        var frameBytes = SampleRate * 2 / 10;
        for (var offset = 0; offset < pcm.Length; offset += frameBytes)
        {
            var length = Math.Min(frameBytes, pcm.Length - offset);
            await SendEvent(socket, new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(pcm, offset, length) });
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        var silence = new byte[frameBytes];
        for (var i = 0; i < 15; i++)
        {
            await SendEvent(socket, new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(silence) });
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var completed = false;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var item = await ReceiveEvent(socket, remaining);
            var type = GetString(item, "type");
            events.Add(type);
            switch (type)
            {
                case "conversation.item.input_audio_transcription.completed":
                    inputTranscript = GetString(item, "transcript");
                    break;
                case "response.audio.delta":
                    var delta = GetString(item, "delta");
                    if (delta.Length > 0) chunks.Add(Convert.FromBase64String(delta));
                    break;
                case "response.audio_transcript.done":
                    outputTranscript = GetString(item, "transcript");
                    break;
                case "error":
                    throw new InvalidOperationException("Native voice service error: " + (item.TryGetProperty("error", out var error) ? GetString(error, "message") : item.ToString()));
                case "response.done":
                    if (!item.TryGetProperty("response", out var response) || GetString(response, "status") != "completed")
                        throw new InvalidOperationException("Native voice response did not complete.");
                    completed = true;
                    if (conversationId.Length == 0) conversationId = GetString(response, "conversation_id");
                    break;
            }
            if (completed && inputTranscript.Length > 0 && outputTranscript.Length > 0) break;
        }
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "voice turn complete", CancellationToken.None);
        var outputPcm = chunks.SelectMany(chunk => chunk).ToArray();
        evidence["sessionId"] = sessionId;
        evidence["conversationId"] = conversationId;
        evidence["events"] = events;
        evidence["inputTranscript"] = inputTranscript;
        evidence["outputTranscript"] = outputTranscript;
        evidence["outputBytes"] = outputPcm.Length;
        evidence["responseCompleted"] = completed;
        await File.WriteAllTextAsync(Path.Combine(State, "session.json"), JsonSerializer.Serialize(evidence, JsonOptions));
        if (!(completed && conversationId.Length > 0 && sessionId.Length > 0 && outputPcm.Length > 0
            && ContainsCode(inputTranscript) && ContainsCode(outputTranscript)))
            throw new InvalidOperationException("Missing real session/conversation/audio evidence or spoken code mismatch; see .azure/session.json.");
        await File.WriteAllBytesAsync(Path.Combine(State, "reply.wav"), MakeWav(outputPcm));

        var conversationBase = $"{endpoint.TrimEnd('/')}/agents/{Uri.EscapeDataString(agentName)}/endpoint/protocols/voice/conversations";
        var persistDeadline = DateTime.UtcNow.AddSeconds(120);
        ServiceResponse conversation = null!, list = null!, recording = null!;
        JsonElement items = default;
        while (true)
        {
            try
            {
                conversation = await Send(client, HttpMethod.Get, $"{conversationBase}/{Uri.EscapeDataString(conversationId)}?api-version={ApiVersion}");
                RequireSuccess(conversation, "Persisted conversation read");
                list = await Send(client, HttpMethod.Get, $"{conversationBase}/{Uri.EscapeDataString(conversationId)}/items?api-version={ApiVersion}&order=asc");
                RequireSuccess(list, "Persisted conversation items read");
                items = list.Body.TryGetProperty("data", out var data) ? data.Clone() : list.Body;
                var transcripts = PersistedTranscripts(items);
                if (!transcripts.Values.All(ContainsCode)) throw new InvalidOperationException("Persisted user/assistant transcript has not finalized.");
                recording = await Send(client, HttpMethod.Get, $"{conversationBase}/{Uri.EscapeDataString(conversationId)}/audio/content?api-version={ApiVersion}");
                RequireSuccess(recording, "Persisted conversation recording download");
                if (recording.Bytes.Length <= 44 || Encoding.ASCII.GetString(recording.Bytes, 0, 4) != "RIFF")
                    throw new InvalidOperationException("No persisted recording bytes returned.");
                evidence["persistedTranscript"] = transcripts;
                break;
            }
            catch (Exception ex) when (DateTime.UtcNow < persistDeadline && (ex is HttpRequestException http && http.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                || ex is InvalidOperationException && ex.Message.Contains("transcript has not finalized", StringComparison.Ordinal)))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
        await File.WriteAllTextAsync(Path.Combine(State, "conversation.json"),
            JsonSerializer.Serialize(new { conversation = conversation.Body, items, recording = new { bytes = recording.Bytes.Length, headers = recording.Headers } }, JsonOptions));
        await File.WriteAllBytesAsync(Path.Combine(State, "conversation.wav"), recording.Bytes);
        evidence["status"] = "PASS";
        evidence["persistedRecordingBytes"] = recording.Bytes.Length;
        return JsonSerializer.SerializeToElement(evidence);
    }

    private static Dictionary<string, string> PersistedTranscripts(JsonElement items)
    {
        if (items.ValueKind == JsonValueKind.Object && items.TryGetProperty("data", out var data)) items = data;
        if (items.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Persisted conversation items response is not a list.");
        var result = new Dictionary<string, string>();
        foreach (var role in new[] { "user", "assistant" })
        {
            var parts = new List<string>();
            foreach (var item in items.EnumerateArray().Where(item => GetString(item, "role") == role))
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in content.EnumerateArray())
                {
                    var text = GetString(part, "transcript");
                    if (text.Length == 0) text = GetString(part, "text");
                    if (text.Length > 0) parts.Add(text);
                }
            }
            result[role] = string.Join(" ", parts);
        }
        return result;
    }

    private static string GetString(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static async Task SendEvent(ClientWebSocket socket, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonElement> ReceiveEvent(ClientWebSocket socket, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        using var stream = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(0, buffer.Length), cancellation.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException($"Voice WebSocket closed: {socket.CloseStatus} {socket.CloseStatusDescription}");
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Native voice event exceeded the 4 MiB safety limit.");
            } while (!result.EndOfMessage);
            return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static bool Check(string name, Func<bool> test)
    {
        try
        {
            if (!test()) throw new InvalidOperationException("assertion failed");
            Console.WriteLine($"PASS {name}");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
            return false;
        }
    }

    private static bool OfflineChecks()
    {
        var fixturePath = Path.Combine(Lab, "assets", "turn.wav");
        var fixture = ReadPcm(fixturePath);
        var text = File.ReadAllText(Path.Combine(Lab, "assets", "turn.txt"));
        var definition = JsonSerializer.SerializeToElement(VoiceDefinition("gpt-realtime"));
        var infrastructure = File.ReadAllText(Path.Combine(Lab, "infra", "main.bicep"));
        var checks = new (string Name, Func<bool> Run)[]
        {
            ("real PCM fixture", () => fixture.Pcm.Length > SampleRate),
            ("fixture bounded to 30 seconds", () => fixture.Pcm.Length < SampleRate * 2 * 30),
            ("synthetic spoken prompt", () => text.Contains("forty", StringComparison.OrdinalIgnoreCase) && !text.Contains('@')),
            ("spoken code words", () => ContainsCode("The workshop code is forty. Hello!")),
            ("spoken code digits", () => ContainsCode("Workshop code: 40. Welcome!")),
            ("wrong code rejected", () => !ContainsCode("Welcome to workshop fourteen.") && !ContainsCode("Code 400.")),
            ("native managed voice definition", () => GetString(definition, "kind") == "voice"
                && GetString(definition, "model_type") == "managed" && GetString(definition, "model") == "gpt-realtime"),
            ("conversation persistence enabled", () => definition.TryGetProperty("store", out var store) && store.ValueKind == JsonValueKind.True),
            ("server VAD and transcription", () => definition.GetProperty("audio").GetProperty("input").GetProperty("turn_detection").GetProperty("create_response").GetBoolean()
                && GetString(definition.GetProperty("audio").GetProperty("input").GetProperty("transcription"), "model") == "azure-speech"),
            ("audio output voice and modality", () => definition.GetProperty("output_modalities")[0].GetString() == "audio"
                && GetString(definition.GetProperty("audio").GetProperty("output"), "voice") == "en-US-AvaNeural"),
            ("native session event shape", () => {
                using var session = JsonDocument.Parse("""{"type":"session.created","session":{"id":"offline-session"},"conversation_id":"offline-conversation"}""");
                return GetString(session.RootElement, "type") == "session.created"
                    && GetString(session.RootElement.GetProperty("session"), "id") == "offline-session";
            }),
            ("native conversation item serialization", () => {
                using var item = JsonDocument.Parse("""{"role":"user","content":[{"type":"input_audio","transcript":"Workshop forty"}]}""");
                return GetString(item.RootElement.GetProperty("content")[0], "transcript") == "Workshop forty";
            }),
            ("project endpoint environment contract", () => SelectSetting("https://example.services.ai.azure.com/api/projects/voice", "", "")
                .Contains("/api/projects/")),
            ("saved output contract", () => {
                using var sample = JsonDocument.Parse("""{"deployment":{"value":"gpt-realtime"}}""");
                return SelectSetting("", GetString(sample.RootElement.GetProperty("deployment"), "value"), "fallback") == "gpt-realtime";
            }),
            ("empty audio rejected", () => {
                try { _ = ParsePcm(new MemoryStream(MakeWav([]))); return false; }
                catch (InvalidDataException ex) { return ex.Message.Contains("empty", StringComparison.OrdinalIgnoreCase); }
            }),
            ("no customer model deployment or keys", () => !infrastructure.Contains("accounts/deployments", StringComparison.Ordinal)
                && infrastructure.Contains("disableLocalAuth: true", StringComparison.Ordinal)
                && infrastructure.Contains("output endpoint", StringComparison.Ordinal)
                && infrastructure.Contains("output projectEndpoint", StringComparison.Ordinal))
        };
        var passed = checks.Count(check => Check(check.Name, check.Run));
        Console.WriteLine($"{passed}/{checks.Length} native managed voice offline checks passed.");
        return passed == checks.Length;
    }

    public static async Task<int> MainAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: dotnet run --file demo.cs [-- --preflight] [--location REGION]\nDefault runs native managed voice over WebSocket and verifies audio, transcripts, persistence, and recording.");
            return 0;
        }
        if (args.Contains("--self-test"))
        {
            if (!OfflineChecks()) return 1;
            Console.WriteLine("Native managed voice offline checks passed.");
            return 0;
        }
        var preflight = args.Contains("--preflight");
        var location = "eastus2";
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--location" && index + 1 < args.Length) location = args[++index];
            else if (args[index] is not ("--preflight")) { Console.Error.WriteLine($"Unknown or incomplete option: {args[index]}"); return 2; }
        }
        Directory.CreateDirectory(State);
        try
        {
            var model = Environment.GetEnvironmentVariable("FOUNDRY_VOICE_AGENT_MODEL") ?? "gpt-realtime";
            var report = preflight ? await Preflight(location, model) : await Live();
            var reportPath = Path.Combine(State, preflight ? "preflight.json" : "live-report.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        catch (Exception ex)
        {
            var report = JsonSerializer.SerializeToElement(new { status = "BLOCKED", stage = preflight ? "preflight" : "native-voice",
                errorType = ex.GetType().Name, error = ex.Message });
            await File.WriteAllTextAsync(Path.Combine(State, preflight ? "preflight.json" : "live-report.json"), JsonSerializer.Serialize(report, JsonOptions));
            Console.Error.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 1;
        }
    }
}
