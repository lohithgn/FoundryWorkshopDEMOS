#!/usr/bin/env dotnet
#:property DefineConstants=TEST_HARNESS
#:include demo.cs
using System.Text.Json.Nodes;

public static class Program
{
    private static int assertions;
    private static void Equal<T>(T expected, T actual)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private static void True(bool value) => Equal(true, value);

    public static async Task<int> Main()
    {
        Equal("https://abc-deny.openai.azure.com/", Demo.DeniedEndpoint("https://abc.openai.azure.com/").ToString());
        foreach (var endpoint in new[] { "http://abc.openai.azure.com", "https://example.org" })
        {
            try { Demo.DeniedEndpoint(endpoint); throw new InvalidOperationException("Accepted unsafe endpoint."); }
            catch (ArgumentException) { assertions++; }
        }

        var written = new List<string>();
        var usage = Demo.StreamResponse([
            JsonNode.Parse("""{"choices":[{"delta":{"content":"Hello"}}]}"""),
            JsonNode.Parse("""{"choices":[{"delta":{"content":null}}]}"""),
            JsonNode.Parse("""{"choices":[],"usage":{"total_tokens":7}}""")
        ], written.Add);
        Equal("Hello", string.Concat(written));
        Equal(7, usage?["total_tokens"]?.GetValue<int>());
        Equal<JsonObject?>(null, Demo.StreamResponse([], _ => { }));

        True(Demo.IsExpectedFailure(404, "{}", 404));
        True(!Demo.IsExpectedFailure(403, """{"error":{"code":"Forbidden","message":"network"}}""", null));
        True(Demo.IsExpectedFailure(401,
            """{"error":{"code":"PermissionDenied","message":"Principal does not have access to API/Operation."}}""", null));
        True(Demo.IsExpectedFailure(403,
            """{"code":"PermissionDenied","message":"principal lacks the required data action Microsoft.CognitiveServices/accounts/OpenAI/deployments/chat/completions/action"}""", null));
        True(!Demo.IsExpectedFailure(401, """{"code":"Unauthorized","message":"expired"}""", null));
        True(!Demo.IsExpectedFailure(429, """{"code":"PermissionDenied","message":"Principal does not have access to API/Operation."}""", null));
        True(!Demo.IsExpectedFailure(403, "invalid", null));

        try { Demo.ValidateIdentity("tenant", null, "cert"); throw new InvalidOperationException("Accepted partial identity."); }
        catch (ArgumentException) { assertions++; }
        Demo.ValidateIdentity(null, null, null);

        Console.WriteLine($"14-foundry-fundamentals: {assertions} offline assertions passed.");
        await Task.CompletedTask;
        return 0;
    }
}
