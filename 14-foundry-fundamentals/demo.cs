#!/usr/bin/env dotnet
#:package Azure.Identity@1.21.0
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

public static class Demo
{
    private const string Scope = "https://cognitiveservices.azure.com/.default";

    public static Uri DeniedEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pass --denied-endpoint for a nonstandard inference endpoint.");

        var host = uri.Host[..^".openai.azure.com".Length] + "-deny.openai.azure.com";
        return new UriBuilder(Uri.UriSchemeHttps, host).Uri;
    }

    public static JsonObject? StreamResponse(IEnumerable<JsonNode?> chunks, Action<string> write)
    {
        JsonObject? usage = null;
        foreach (var chunk in chunks)
        {
            if (chunk is not JsonObject obj)
                continue;
            if (obj["choices"] is JsonArray choices && choices.FirstOrDefault() is JsonObject choice &&
                choice["delta"] is JsonObject delta && delta["content"] is JsonValue content &&
                content.TryGetValue<string>(out var text) && text is not null)
                write(text);
            if (obj["usage"] is JsonObject chunkUsage)
                usage = (JsonObject)chunkUsage.DeepClone();
        }
        return usage;
    }

    public static bool IsExpectedFailure(int status, string body, int? expectedStatus)
    {
        if (expectedStatus is not null)
            return status == expectedStatus;
        if (status is not (401 or 403))
            return false;
        try
        {
            var root = JsonNode.Parse(body);
            var error = root?["error"] as JsonObject ?? root as JsonObject;
            if (error?["code"]?.GetValue<string>() != "PermissionDenied")
                return false;
            var message = error["message"]?.GetValue<string>();
            return message == "Principal does not have access to API/Operation." ||
                (message?.Contains("lacks the required data action", StringComparison.Ordinal) == true &&
                 message.Contains("Microsoft.CognitiveServices/accounts/OpenAI/deployments/chat/completions/action",
                     StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static void ValidateIdentity(string? tenant, string? client, string? certificate)
    {
        var values = new[] { tenant, client, certificate };
        if (values.Any(value => value is not null) &&
            values.Any(value => string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException(
                "Restricted inference requires --tenant-id, --client-id, and --certificate-path together.");
    }

    public static async Task<(int Status, string Body)> ProbeAsync(
        Func<string, CancellationToken, Task<(int Status, string Body)>> send,
        string deployment, int? expectedStatus, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await send(deployment, cancellationToken);
            Console.WriteLine("Probe succeeded unexpectedly: check inherited inference roles or deployment names.");
            return (response.Status, response.Body);
        }
        catch (ServiceException error)
        {
            Console.WriteLine($"Actual HTTP {error.Status}: {error.Body}");
            var matched = IsExpectedFailure(error.Status, error.Body, expectedStatus);
            if (!matched)
                Console.WriteLine(expectedStatus is null
                    ? "Expected an explicit inference RBAC PermissionDenied (HTTP 401 or 403)."
                    : $"Expected HTTP {expectedStatus}.");
            return (matched ? error.Status : 0, error.Body);
        }
    }

    public static TokenCredential CreateCredential(string? tenant, string? client, string? certificate)
    {
        ValidateIdentity(tenant, client, certificate);
        return !string.IsNullOrWhiteSpace(certificate)
            ? new ClientCertificateCredential(tenant!, client!, certificate!)
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions
              { ExcludeInteractiveBrowserCredential = true });
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        if (options.Help)
        {
            options.Usage();
            return 0;
        }
        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new ArgumentException("Set AZURE_OPENAI_ENDPOINT or pass --endpoint.");
        ValidateIdentity(options.TenantId, options.ClientId, options.CertificatePath);

        var credential = CreateCredential(options.TenantId, options.ClientId, options.CertificatePath);
        Console.WriteLine("Inference identity: " +
            (options.CertificatePath is not null
                ? $"certificate service principal {options.ClientId}"
                : "DefaultAzureCredential (check inherited roles)"));
        using var client = new OpenAiClient(options.Endpoint, credential, TimeSpan.FromSeconds(60));
        Console.WriteLine($"Project: {Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT") ?? "(not supplied)"}");
        Console.WriteLine($"Inference: {options.Endpoint} | Deployment: {options.Deployment}");
        Console.WriteLine("A project organizes assets; inference uses /openai/v1/.");
        Console.WriteLine("The deployment name is a user-chosen alias, not the catalog model name.");
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a workshop tutor. Answer in two short sentences." },
            new JsonObject { ["role"] = "user", ["content"] = "What is the difference between a project and a model deployment?" }
        };
        var response = await client.PostAsync("chat/completions", new JsonObject
        {
            ["model"] = options.Deployment, ["messages"] = messages.DeepClone(), ["max_tokens"] = 160
        });
        Console.WriteLine($"First response: {response["choices"]?[0]?["message"]?["content"]}");
        Console.WriteLine($"Serving model: {response["model"]} Usage: {response["usage"]?.ToJsonString() ?? "null"}");

        Console.Write("Stream: ");
        var streamedUsage = await client.StreamAsync(new JsonObject
        {
            ["model"] = options.Deployment, ["messages"] = messages.DeepClone(), ["max_tokens"] = 160,
            ["stream"] = true, ["stream_options"] = new JsonObject { ["include_usage"] = true }
        }, Console.Write);
        Console.WriteLine($"\nStream usage: {streamedUsage?.ToJsonString() ?? "Not returned by the service"}");

        if (options.SkipNegativeProbes)
            return 0;

        Console.WriteLine("\nWrong deployment (expected HTTP 404):");
        var wrong = await client.ProbeAsync("deliberately-missing-deployment", 404);
        Console.WriteLine("Fix: use the Bicep deployment output, not gpt-4.1-mini.");
        var target = options.DeniedEndpoint is null
            ? DeniedEndpoint(options.Endpoint).ToString()
            : options.DeniedEndpoint;
        Console.WriteLine($"\nMissing inference permission (expected RBAC denial; HTTP 401 or 403): {target}");
        using var denied = new OpenAiClient(target, credential, TimeSpan.FromSeconds(60));
        var deniedResult = await denied.ProbeAsync(options.Deployment, null);
        Console.WriteLine("Fix: assign Cognitive Services OpenAI User at the target account; allow RBAC propagation.");
        if (!wrong || !deniedResult)
        {
            Console.WriteLine("Negative demonstration not reproduced; inherited roles can bypass the denial.");
            return 1;
        }
        return 0;
    }

    public sealed class Options
    {
        public string? Endpoint { get; private set; } = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        public string Deployment { get; private set; } =
            Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "tutor-mini";
        public string? DeniedEndpoint { get; private set; } = Environment.GetEnvironmentVariable("WORKSHOP_DENIED_ENDPOINT");
        public string? TenantId { get; private set; } = Environment.GetEnvironmentVariable("WORKSHOP_INFERENCE_TENANT_ID");
        public string? ClientId { get; private set; } = Environment.GetEnvironmentVariable("WORKSHOP_INFERENCE_CLIENT_ID");
        public string? CertificatePath { get; private set; } = Environment.GetEnvironmentVariable("WORKSHOP_INFERENCE_CERTIFICATE_PATH");
        public bool SkipNegativeProbes { get; private set; }
        public bool Help { get; }

        public Options(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var name = args[i];
                if (name is "-h" or "--help") { Help = true; continue; }
                if (name == "--skip-negative-probes") { SkipNegativeProbes = true; continue; }
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i] : throw new ArgumentException($"Missing value for {name}.");
                switch (name)
                {
                    case "--endpoint": Endpoint = value; break;
                    case "--deployment": Deployment = value; break;
                    case "--denied-endpoint": DeniedEndpoint = value; break;
                    case "--tenant-id": TenantId = value; break;
                    case "--client-id": ClientId = value; break;
                    case "--certificate-path": CertificatePath = value; break;
                    default: throw new ArgumentException($"Unknown argument: {name}");
                }
            }
        }

        public void Usage() => Console.WriteLine(
            "Usage: dotnet run --file demo.cs -- [--endpoint URL] [--deployment NAME] " +
            "[--denied-endpoint URL] [--tenant-id ID --client-id ID --certificate-path FILE] [--skip-negative-probes]");
    }
}

public sealed class ServiceException(int status, string body) : Exception($"HTTP {status}: {body}")
{
    public int Status { get; } = status;
    public string Body { get; } = body;
}

public sealed class OpenAiClient : IDisposable
{
    private readonly TokenCredential credential;
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Uri baseUri;
    private readonly TimeSpan timeout;

    public OpenAiClient(string endpoint, TokenCredential credential, TimeSpan timeout)
    {
        this.credential = credential;
        this.timeout = timeout;
        baseUri = new Uri(endpoint.TrimEnd('/') + "/openai/v1/");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([ "https://cognitiveservices.azure.com/.default" ]),
            cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
    }

    public async Task<JsonObject> PostAsync(string path, JsonObject body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var response = await SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ServiceException((int)response.StatusCode, text);
        return JsonNode.Parse(text)?.AsObject() ?? throw new JsonException("Service returned an empty JSON object.");
    }

    public async Task<JsonObject?> StreamAsync(JsonObject body, Action<string> write,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ServiceException((int)response.StatusCode,
                await response.Content.ReadAsStringAsync(cancellationToken));
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        JsonObject? usage = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var data = line[5..].Trim();
            if (data == "[DONE]")
                break;
            var chunk = JsonNode.Parse(data);
            usage = Demo.StreamResponse([chunk], write) ?? usage;
        }
        return usage;
    }

    public async Task<bool> ProbeAsync(string deployment, int? expectedStatus)
    {
        try
        {
            await PostAsync("chat/completions", new JsonObject
            {
                ["model"] = deployment,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Say hello." }),
                ["max_tokens"] = 16
            });
            Console.WriteLine("Probe succeeded unexpectedly: check inherited inference roles or deployment names.");
            return false;
        }
        catch (ServiceException error)
        {
            Console.WriteLine($"Actual HTTP {error.Status}: {error.Body}");
            var matched = Demo.IsExpectedFailure(error.Status, error.Body, expectedStatus);
            if (!matched)
                Console.WriteLine(expectedStatus is null
                    ? "Expected an explicit inference RBAC PermissionDenied (HTTP 401 or 403); check authentication, RBAC propagation, and region."
                    : $"Expected HTTP {expectedStatus}; check authentication, RBAC propagation, and region.");
            return matched;
        }
    }

    public void Dispose() => http.Dispose();
}

#if !TEST_HARNESS
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { return await Demo.RunAsync(args); }
        catch (Exception error) { Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}"); return 1; }
    }
}
#endif
