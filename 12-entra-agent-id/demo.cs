global using System;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Linq;
global using System.Net;
global using System.Net.Http;
global using System.Text;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
using System.Net.Http.Headers;

public static class EntraAgentDemo
{
    public sealed record Options(string Endpoint, string Deployment);

    public static void ValidateIdentity(string tenant, string blueprint, string agent, string secret)
    {
        if (!Guid.TryParse(tenant, out _) || !Guid.TryParse(blueprint, out _) || !Guid.TryParse(agent, out _))
            throw new ArgumentException("Entra tenant, blueprint, and child agent IDs must be UUIDs.");
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("ENTRA_BLUEPRINT_SECRET must not be empty.");
        if (Guid.Parse(blueprint) == Guid.Parse(agent))
            throw new ArgumentException("The blueprint client ID and child agent ID must be different.");
    }

    public static Options Parse(string[] args, IReadOnlyDictionary<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(item => (string)item.Key, item => item.Value?.ToString(), StringComparer.Ordinal);
        var endpoint = environment.GetValueOrDefault("AZURE_OPENAI_ENDPOINT");
        var deployment = environment.GetValueOrDefault("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1-mini";
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--help")
                throw new HelpRequestedException();
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}.");
            var value = args[++index];
            switch (option)
            {
                case "--endpoint": endpoint = value; break;
                case "--deployment": deployment = value; break;
                default: throw new ArgumentException($"Unknown option: {option}.");
            }
        }
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Set the Bicep endpoint HTTPS URL.");
        if (string.IsNullOrWhiteSpace(deployment))
            throw new ArgumentException("Set a nonempty deployment name.");
        return new Options(endpoint.TrimEnd('/'), deployment);
    }

    public static async Task<string> AgentTokenAsync(
        HttpClient http, string tenant, string blueprint, string agent, string secret,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(tenant, blueprint, agent, secret);
        var tokenUrl = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token";
        using var firstResponse = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = blueprint,
            ["client_secret"] = secret,
            ["grant_type"] = "client_credentials",
            ["scope"] = "api://AzureADTokenExchange/.default",
            ["fmi_path"] = agent
        }), cancellationToken);
        firstResponse.EnsureSuccessStatusCode();
        using var firstDocument = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync(cancellationToken));
        var exchange = ReadToken(firstDocument.RootElement, "Entra returned no blueprint exchange token.");

        using var secondResponse = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = agent,
            ["grant_type"] = "client_credentials",
            ["scope"] = "https://cognitiveservices.azure.com/.default",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = exchange
        }), cancellationToken);
        secondResponse.EnsureSuccessStatusCode();
        using var secondDocument = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync(cancellationToken));
        return ReadToken(secondDocument.RootElement, "Entra returned no agent resource token.");
    }

    private static string ReadToken(JsonElement response, string message)
    {
        if (!response.TryGetProperty("access_token", out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException(message);
        return value.GetString()!;
    }

    private static string Quote(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

    public static async Task<string> RunAgentAsync(
        HttpClient http, string endpoint, string deployment, string token,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            endpoint.TrimEnd('/') + "/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var body = $"{{\"model\":{Quote(deployment)},\"messages\":[" +
            $"{{\"role\":\"system\",\"content\":{Quote("You are an AI workshop tutor. Answer in one short sentence.")}}}," +
            $"{{\"role\":\"user\",\"content\":{Quote("What is an AI agent?")}}}]}}";
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Foundry returned {(int)response.StatusCode}: {content}");
        using var document = JsonDocument.Parse(content);
        var text = document.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("The agent returned no text.");
        return text;
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Options options;
            try { options = Parse(args); }
            catch (HelpRequestedException)
            {
                Console.WriteLine("Usage: dotnet run --file demo.cs -- [--endpoint HTTPS_URL] [--deployment NAME]");
                return 0;
            }
            var names = new[]
            {
                "ENTRA_TENANT_ID", "ENTRA_BLUEPRINT_CLIENT_ID", "ENTRA_AGENT_ID", "ENTRA_BLUEPRINT_SECRET"
            };
            var values = names.Select(Environment.GetEnvironmentVariable).ToArray();
            var missing = names.Where((_, index) => string.IsNullOrWhiteSpace(values[index])).ToArray();
            if (missing.Length > 0)
                throw new ArgumentException($"Run setup_identity.ps1 first. Missing: {string.Join(", ", missing)}");
            ValidateIdentity(values[0]!, values[1]!, values[2]!, values[3]!);

            using var tokenClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var token = await AgentTokenAsync(tokenClient, values[0]!, values[1]!, values[2]!, values[3]!);
            Console.WriteLine($"Blueprint client ID: {values[1]}\nAgent identity: {values[2]}\n" +
                "The model call uses the child agent's token, not the presenter's Azure CLI identity.");
            using var modelClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var text = await RunAgentAsync(modelClient, options.Endpoint, options.Deployment, token);
            Console.WriteLine($"\nAuthenticated Foundry response:\n{text}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}");
            Console.Error.WriteLine("Usage: dotnet run --file demo.cs -- [--endpoint HTTPS_URL] [--deployment NAME]");
            return 1;
        }
    }

    private sealed class HelpRequestedException : Exception { }
}
