using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class FineTuningDemo
{
    public const string Model = "gpt-4.1-mini-2025-04-14";
    public static readonly string DataPath = Path.Combine(Directory.GetCurrentDirectory(), "training.jsonl");
    public sealed record Options(string Endpoint, string Model, string? JobId, bool Wait, string Report);

    public static Options Parse(string[] args, IReadOnlyDictionary<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(item => (string)item.Key, item => item.Value?.ToString());
        var endpoint = environment.GetValueOrDefault("AZURE_OPENAI_ENDPOINT");
        var model = Model;
        string? job = null;
        var wait = false;
        var report = Path.Combine(Directory.GetCurrentDirectory(), ".azure", "fine-tuning-job.json");
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--help") throw new HelpRequestedException();
            if (option == "--wait") { wait = true; continue; }
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {option}.");
            var value = args[++index];
            switch (option)
            {
                case "--endpoint": endpoint = value; break;
                case "--model": model = value; break;
                case "--job-id": job = value; break;
                case "--report": report = value; break;
                default: throw new ArgumentException($"Unknown option: {option}.");
            }
        }
        if (string.IsNullOrWhiteSpace(endpoint) || !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Set AZURE_OPENAI_ENDPOINT to the Bicep endpoint HTTPS URL.");
        if (string.IsNullOrWhiteSpace(model) || (job is not null && string.IsNullOrWhiteSpace(job)))
            throw new ArgumentException("Model and job ID must not be empty.");
        return new Options(endpoint.TrimEnd('/'), model, job, wait, Path.GetFullPath(report));
    }

    public static int ValidateData(string path)
    {
        var rows = File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select((line, index) =>
            {
                try { return (Index: index + 1, Row: JsonNode.Parse(line)?.AsObject()); }
                catch (JsonException error) { throw new ArgumentException($"Row {index + 1}: invalid JSON.", error); }
            }).ToArray();
        if (rows.Length < 10)
            throw new ArgumentException("Supervised fine-tuning requires at least 10 training examples.");
        foreach (var (index, row) in rows)
        {
            var messages = row?["messages"] as JsonArray;
            if (messages is null || messages.Count != 3)
                throw new ArgumentException($"Row {index}: expected system, user, and assistant messages.");
            var roles = messages.Select(message => message?["role"]?.GetValue<string>()).ToArray();
            if (!roles.SequenceEqual(["system", "user", "assistant"]))
                throw new ArgumentException($"Row {index}: invalid message roles.");
            if (messages.Any(message => string.IsNullOrWhiteSpace(message?["content"]?.GetValue<string>())))
                throw new ArgumentException($"Row {index}: message content must be nonempty text.");
            var label = messages[2]?["content"]?.GetValue<string>();
            if (label is not ("BILLING" or "TECHNICAL" or "ACCOUNT"))
                throw new ArgumentException($"Row {index}: unsupported target label.");
        }
        return rows.Length;
    }

    public static JsonObject BuildJobPayload(string model, string trainingFileId) => new()
    {
        ["model"] = model,
        ["training_file"] = trainingFileId,
        ["suffix"] = "workshop",
        ["method"] = new JsonObject
        {
            ["type"] = "supervised",
            ["supervised"] = new JsonObject { ["hyperparameters"] = new JsonObject { ["n_epochs"] = 1 } }
        },
        ["trainingType"] = "GlobalStandard"
    };

    public static async Task<JsonDocument> SendAsync(
        HttpClient http, HttpMethod method, string uri, string token, HttpContent? content = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Azure OpenAI returned {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    public static async Task<JsonDocument> SubmitAsync(
        HttpClient http, string endpoint, string model, string dataPath, string token,
        Func<TimeSpan, Task>? delay = null, TimeSpan? fileTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var count = ValidateData(dataPath);
        delay ??= Task.Delay;
        Console.WriteLine($"Uploading {count} toy examples; submitting one epoch of Global SFT.");
        using var stream = File.OpenRead(dataPath);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");
        form.Add(file, "file", Path.GetFileName(dataPath));
        form.Add(new StringContent("fine-tune"), "purpose");
        using var uploaded = await SendAsync(http, HttpMethod.Post, $"{endpoint.TrimEnd('/')}/openai/v1/files",
            token, form, cancellationToken);
        var fileId = uploaded.RootElement.GetProperty("id").GetString()!;
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            using var fileState = await SendAsync(http, HttpMethod.Get,
                $"{endpoint.TrimEnd('/')}/openai/v1/files/{Uri.EscapeDataString(fileId)}", token,
                cancellationToken: cancellationToken);
            var status = fileState.RootElement.TryGetProperty("status", out var state) ? state.GetString() : null;
            if (status is "processed" or "processed_with_errors") break;
            if (status is "error" or "failed")
                throw new InvalidOperationException($"Training file processing failed: {fileState.RootElement.GetRawText()}");
            if (deadline.Elapsed >= (fileTimeout ?? TimeSpan.FromMinutes(10)))
                throw new TimeoutException($"Training file {fileId} is still processing.");
            await delay(TimeSpan.FromSeconds(2));
        }
        var payload = BuildJobPayload(model, fileId);
        return await SendAsync(http, HttpMethod.Post, $"{endpoint.TrimEnd('/')}/openai/v1/fine_tuning/jobs",
            token, new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken);
    }

    public static async Task<JsonDocument> RetrieveJobAsync(
        HttpClient http, string endpoint, string jobId, string token, CancellationToken cancellationToken = default) =>
        await SendAsync(http, HttpMethod.Get,
            $"{endpoint.TrimEnd('/')}/openai/v1/fine_tuning/jobs/{Uri.EscapeDataString(jobId)}", token,
            cancellationToken: cancellationToken);

    public static void ValidateJob(JsonElement job)
    {
        var status = job.GetProperty("status").GetString();
        var id = job.GetProperty("id").GetString();
        var error = job.TryGetProperty("error", out var value) ? value.ToString() : "";
        if (status is "failed" or "cancelled")
            throw new InvalidOperationException($"Fine-tuning job {id} ended with {status}: {error}");
        if (status == "succeeded" &&
            (!job.TryGetProperty("fine_tuned_model", out var model) || model.ValueKind != JsonValueKind.String ||
             string.IsNullOrWhiteSpace(model.GetString())))
            throw new InvalidOperationException("Succeeded job did not return a fine-tuned model ID.");
    }

    public static void WriteReport(string path, string endpoint, JsonElement job)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var report = new JsonObject { ["endpoint"] = endpoint, ["job"] = JsonNode.Parse(job.GetRawText()) };
        File.WriteAllText(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Options options;
            try { options = Parse(args); }
            catch (HelpRequestedException)
            {
                Console.WriteLine("Usage: dotnet run --file demo.cs -- [--endpoint HTTPS_URL] [--model MODEL_ID] [--job-id ID] [--wait] [--report PATH]");
                return 0;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var token = await FineTuningAuth.GetTokenAsync("https://cognitiveservices.azure.com/.default", timeout.Token);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            JsonDocument? job = null;
            try
            {
                job = options.JobId is not null
                    ? await RetrieveJobAsync(http, options.Endpoint, options.JobId, token, timeout.Token)
                    : await SubmitAsync(http, options.Endpoint, options.Model, DataPath, token, cancellationToken: timeout.Token);
                var id = job.RootElement.GetProperty("id").GetString()!;
                Console.WriteLine($"Job ID: {id}");
                var deadline = Stopwatch.StartNew();
                while (true)
                {
                    WriteReport(options.Report, options.Endpoint, job.RootElement);
                    var status = job.RootElement.GetProperty("status").GetString();
                    if (!options.Wait || status is "succeeded" or "failed" or "cancelled")
                        break;
                    if (deadline.Elapsed >= TimeSpan.FromMinutes(30))
                        throw new TimeoutException($"Job {id} is still running. Resume with --job-id {id}.");
                    Console.WriteLine($"Status: {status}");
                    await Task.Delay(TimeSpan.FromSeconds(15), timeout.Token);
                    var next = await RetrieveJobAsync(http, options.Endpoint, id, token, timeout.Token);
                    job.Dispose();
                    job = next;
                }
                ValidateJob(job.RootElement);
                Console.WriteLine(job.RootElement.ToString());
                if (job.RootElement.GetProperty("status").GetString() == "succeeded")
                    Console.WriteLine($"Training complete. Deploy this model ID for inference: {job.RootElement.GetProperty("fine_tuned_model").GetString()}");
                else
                    Console.WriteLine($"Training is NOT complete. Resume with: dotnet run --file demo.cs -- --job-id {id} --wait");
                return 0;
            }
            finally { job?.Dispose(); }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}");
            return 1;
        }
    }

    private sealed class HelpRequestedException : Exception { }
}

public static class FineTuningAuth
{
    public static async Task<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "account", "get-access-token", "--scope", scope, "--query", "accessToken", "-o", "tsv" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Azure CLI.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"Azure CLI token acquisition failed: {error.Trim()}");
        return output.Trim();
    }
}
