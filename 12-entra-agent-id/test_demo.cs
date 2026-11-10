#:property TargetFramework=net10.0

#:include demo.cs

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

const string tenant = "11111111-1111-1111-1111-111111111111";
const string blueprint = "22222222-2222-2222-2222-222222222222";
const string agent = "33333333-3333-3333-3333-333333333333";

EntraAgentDemo.ValidateIdentity(tenant, blueprint, agent, "test-secret");
Throws<ArgumentException>(() => EntraAgentDemo.ValidateIdentity("bad", blueprint, agent, "secret"),
    "Invalid identity IDs were accepted.");
Throws<ArgumentException>(() => EntraAgentDemo.ValidateIdentity(tenant, blueprint, blueprint, "secret"),
    "A reused blueprint/agent ID was accepted.");
Throws<ArgumentException>(() => EntraAgentDemo.ValidateIdentity(tenant, blueprint, agent, " "),
    "An empty blueprint secret was accepted.");

var tokenRequests = new List<(string Url, Dictionary<string, string> Form)>();
var tokenReplies = new Queue<string>(["""{"access_token":"exchange"}""", """{"access_token":"agent-token"}"""]);
using (var tokenHttp = new HttpClient(new MockHandler(async request =>
{
    var form = await request.Content!.ReadAsStringAsync();
    tokenRequests.Add((request.RequestUri!.ToString(),
        System.Web.HttpUtility.ParseQueryString(form).AllKeys
            .Where(key => key is not null).ToDictionary(key => key!, key => System.Web.HttpUtility.ParseQueryString(form)[key]!)));
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(tokenReplies.Dequeue(), Encoding.UTF8, "application/json")
    };
})))
{
    var token = await EntraAgentDemo.AgentTokenAsync(tokenHttp, tenant, blueprint, agent, "test-secret");
    Check(token == "agent-token", "The agent resource token was not returned.");
}
Check(tokenRequests.Count == 2, "The Entra exchange must make exactly two requests.");
Check(tokenRequests.All(request => request.Url == $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token"),
    "The Entra token endpoint changed.");
Check(tokenRequests[0].Form["client_id"] == blueprint &&
      tokenRequests[0].Form["client_secret"] == "test-secret" &&
      tokenRequests[0].Form["fmi_path"] == agent &&
      tokenRequests[0].Form["scope"] == "api://AzureADTokenExchange/.default",
    "Blueprint exchange parameters changed.");
Check(tokenRequests[1].Form["client_id"] == agent &&
      tokenRequests[1].Form["client_assertion"] == "exchange" &&
      tokenRequests[1].Form["scope"] == "https://cognitiveservices.azure.com/.default" &&
      !tokenRequests[1].Form.ContainsKey("client_secret"),
    "Child-agent token exchange parameters changed.");
Check(tokenRequests.All(request => !request.Url.Contains("test-secret", StringComparison.Ordinal)),
    "The blueprint secret appeared in a URL.");

foreach (var (status, payload) in new[]
{
    (HttpStatusCode.OK, "{}"),
    (HttpStatusCode.Forbidden, """{"error":"denied"}""")
})
{
    using var failedHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(status)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    })));
    var failedAsExpected = false;
    try
    {
        await EntraAgentDemo.AgentTokenAsync(failedHttp, tenant, blueprint, agent, "test-secret");
    }
    catch (Exception error) when (error is InvalidOperationException or HttpRequestException) { failedAsExpected = true; }
    Check(failedAsExpected, "Empty token or failed authentication was accepted.");
}

(string Method, string Url, string? Body, string? Authorization) modelCall = default;
using (var modelHttp = new HttpClient(new MockHandler(async request =>
{
    modelCall = (request.Method.Method, request.RequestUri!.ToString(),
        await request.Content!.ReadAsStringAsync(), request.Headers.Authorization?.ToString());
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"choices":[{"message":{"content":"A helpful agent."}}]}""",
            Encoding.UTF8, "application/json")
    };
})))
{
    var response = await EntraAgentDemo.RunAgentAsync(modelHttp, "https://model.openai.azure.com/", "chat", "agent-token");
    Check(response == "A helpful agent.", "The model response was not returned.");
}
Check(modelCall.Method == "POST" && modelCall.Url == "https://model.openai.azure.com/openai/v1/chat/completions" &&
      modelCall.Authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true &&
      modelCall.Authorization.EndsWith("agent-token", StringComparison.Ordinal) &&
      modelCall.Body!.Contains("\"model\":\"chat\"", StringComparison.Ordinal) &&
      modelCall.Body.Contains("What is an AI agent?", StringComparison.Ordinal),
    "The Foundry request did not use the child agent's bearer token and expected tutor prompt.");

using (var emptyResponseHttp = new HttpClient(new MockHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("""{"choices":[{"message":{"content":" "}}]}""", Encoding.UTF8, "application/json")
}))))
{
    var failed = false;
    try
    {
        await EntraAgentDemo.RunAgentAsync(emptyResponseHttp, "https://model.example", "chat", "token");
    }
    catch (InvalidOperationException) { failed = true; }
    Check(failed, "An empty agent response was accepted.");
}

var parsedOptions = EntraAgentDemo.Parse(["--endpoint", "https://model.example/"],
    new Dictionary<string, string?> { ["AZURE_OPENAI_DEPLOYMENT"] = "gpt-4.1-mini" });
Check(parsedOptions.Endpoint == "https://model.example" && parsedOptions.Deployment == "gpt-4.1-mini",
    "Endpoint trimming or the deployment default changed.");
Throws<ArgumentException>(() => EntraAgentDemo.Parse(["--endpoint", "http://localhost"]),
    "An insecure endpoint was accepted.");
Throws<ArgumentException>(() => EntraAgentDemo.Parse(["--endpoint", "https://model.example", "--deployment", " "]),
    "An empty deployment was accepted.");
Throws<ArgumentException>(() => EntraAgentDemo.Parse(["--unknown", "value"]),
    "An unknown option was accepted.");

await CheckCommand("demo.cs", "--help", 0, "Usage:");
await CheckCommandWithValue("demo.cs", "--deployment", "chat", 1, "HTTPS URL");
await CheckCommand("deploy_lab.cs", "--help", 0, "Usage:");
await CheckCommandWithValue("deploy_lab.cs", "--prefix", "!invalid", 1, "prefix");
await CheckCommand("cleanup.cs", "--help", 0, "Usage:");
await CheckCommand("cleanup.cs", "--unknown", 1, "Unknown option");

var pwsh = FindCommand("pwsh");
if (pwsh is not null)
    await TestPowerShellSetup(pwsh);

Console.WriteLine("PASS: entra-agent-id .NET offline regression checks");

static async Task CheckCommand(string script, string first, int expectedCode, string expectedText)
{
    await CheckCommandWithValue(script, first, null, expectedCode, expectedText);
}

static async Task CheckCommandWithValue(string script, string first, string? second, int expectedCode, string expectedText)
{
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = Directory.GetCurrentDirectory(), RedirectStandardOutput = true,
        RedirectStandardError = true, UseShellExecute = false
    };
    foreach (var argument in new[] { "run", "--file", script, "--", first })
        start.ArgumentList.Add(argument);
    if (second is not null) start.ArgumentList.Add(second);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await stdout + await stderr;
    Check(process.ExitCode == expectedCode && output.Contains(expectedText, StringComparison.OrdinalIgnoreCase),
        $"{script} {first} returned {process.ExitCode}: {output}");
}

static string? FindCommand(string name)
{
    var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
    return paths.Select(path => Path.Combine(path, name)).FirstOrDefault(File.Exists);
}

static async Task TestPowerShellSetup(string pwsh)
{
    var directory = Path.Combine(Directory.GetCurrentDirectory(), ".azure",
        ".offline-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var scriptPath = Path.Combine(directory, "setup_identity.ps1");
        File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "setup_identity.ps1"), scriptPath);
        const string command = """
            $ErrorActionPreference = "Stop"
            $global:graphCalls = [System.Collections.Generic.List[object]]::new()
            function Import-Module { param($Name, $ErrorAction) }
            function Connect-MgGraph {
                param($TenantId, $ContextScope, [switch] $NoWelcome, $Scopes)
                if ($Scopes -contains "Directory.AccessAsUser.All") { throw "Overbroad scope" }
                if ($Scopes.Count -ne 6) { throw "Unexpected delegated scope set" }
            }
            function Invoke-MgGraphRequest {
                param($Method, $Uri, $Headers, $ContentType, $Body)
                if ($Method -eq "GET") { return @{ id = "sponsor" } }
                if ($Headers["OData-Version"] -ne "4.0") { throw "Missing OData header" }
                $global:graphCalls.Add(@{ Method = $Method; Uri = $Uri; Body = ($Body | ConvertFrom-Json) })
                if ($Uri -match "agentIdentityBlueprint$") {
                    return @{ id = "blueprint-object"; appId = "22222222-2222-2222-2222-222222222222" }
                }
                if ($Uri -match "agentIdentityBlueprintPrincipal$") { return @{ id = "blueprint-principal" } }
                if ($Uri -match "Microsoft.Graph.AgentIdentity$") { return @{ id = "33333333-3333-3333-3333-333333333333" } }
                if ($Uri -match "/addPassword$") { return @{ secretText = "unit-test-secret" } }
                if ($Method -ne "PATCH") { throw "Unexpected Graph call: $Uri" }
            }
            & $env:TEST_SETUP_SCRIPT -TenantId "11111111-1111-1111-1111-111111111111"
            $state = Get-Content (Join-Path (Split-Path $env:TEST_SETUP_SCRIPT) ".azure/identity.json") -Raw
            if ($state -match "unit-test-secret") { throw "Secret persisted" }
            if (($state | ConvertFrom-Json).agentIdentityId -ne "33333333-3333-3333-3333-333333333333") { throw "Missing agent ID" }
            if ($env:ENTRA_BLUEPRINT_SECRET -ne "unit-test-secret") { throw "Secret missing from environment" }
            $agentCall = $global:graphCalls | Where-Object { $_.Uri -match "Microsoft.Graph.AgentIdentity$" }
            if ($agentCall.Body.agentIdentityBlueprintId -ne "22222222-2222-2222-2222-222222222222") { throw "Wrong parent blueprint" }
            if ($global:graphCalls.Count -ne 5) { throw "Unexpected provisioning step count" }
            """;
        var start = new ProcessStartInfo(pwsh)
        {
            WorkingDirectory = Directory.GetCurrentDirectory(), RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
            start.ArgumentList.Add(argument);
        start.Environment["TEST_SETUP_SCRIPT"] = scriptPath;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout + await stderr;
        Check(process.ExitCode == 0 && !output.Contains("unit-test-secret", StringComparison.Ordinal),
            $"Mocked PowerShell setup failed or disclosed the secret: {output}");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

sealed class MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request);
}
