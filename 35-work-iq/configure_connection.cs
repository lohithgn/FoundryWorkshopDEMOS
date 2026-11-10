#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property DefineConstants=CONFIGURE_CONNECTION
#:include demo.cs

internal static class ConfigureConnectionApp
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (WorkIq.Demo.ConfigureHelpRequested(args))
            {
                Console.WriteLine("""
                    Configure the Work IQ OAuth2 RemoteA2A connection.
                    Usage: dotnet run --file configure_connection.cs
                    Requires an existing lab deployment, Azure CLI sign-in, and:
                      WORK_IQ_CLIENT_ID
                      WORK_IQ_CLIENT_SECRET (load from an approved secret store; never echo it)
                    This writes the named Foundry connection and prints its OAuth redirect URI.
                    """);
                return 0;
            }
            var config = WorkIq.Demo.LoadConfig();
            var clientId = Environment.GetEnvironmentVariable("WORK_IQ_CLIENT_ID") ?? "";
            var secret = Environment.GetEnvironmentVariable("WORK_IQ_CLIENT_SECRET") ?? "";
            var body = WorkIq.Demo.ConnectionBody(config.TenantId, clientId, secret);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var request = WorkIq.Demo.Request(HttpMethod.Put,
                WorkIq.Demo.Arm + config.ProjectResourceId + "/connections/" + WorkIq.Demo.Connection + "?api-version=2025-04-01-preview",
                WorkIq.Demo.GetToken(config.TenantId), body);
            using var response = await http.SendAsync(request);
            if (response.StatusCode is not (System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Created))
                throw new InvalidOperationException($"Connection creation failed: HTTP {(int)response.StatusCode}; request ID {RequestId(response)}.");
            var json = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var redirect = json?["properties"]?["oauthRedirectUrl"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(redirect) || !Uri.TryCreate(redirect, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new InvalidOperationException("Connection created but no valid OAuth redirect URL returned; inspect it in Foundry.");
            Console.WriteLine("OAuth2 connection created. Add this exact Web redirect URI to the Entra app:");
            Console.WriteLine(redirect);
            Console.WriteLine("Each caller must still authorize their OWN Microsoft 365 account when using the agent.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static string RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ms-request-id", out var ids) ? ids.FirstOrDefault() ?? "unavailable" : "unavailable";
}
