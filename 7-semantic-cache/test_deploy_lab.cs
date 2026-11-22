#:include deploy_lab.cs
static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

foreach (var sku in SemanticCacheDeployLab.RedisSkus)
{
    var options = SemanticCacheDeployLab.Parse(["--redis-sku", sku]);
    Check(options.RedisSku == sku, $"Redis SKU {sku} was not retained.");
    var command = SemanticCacheDeployLab.DeploymentArguments("rg-student", "semantic-cache-demo", "sub",
        "/lab/infra/main.bicep", "principal", "eastus2", options.RedisSku);
    Check(command.Contains($"redisSku={sku}") && command.Contains("location=eastus2"),
        $"Deployment parameters omitted the selected SKU or location for {sku}.");
}
var invalidSku = false;
try { SemanticCacheDeployLab.Parse(["--redis-sku", "Balanced_B2"]); }
catch (ArgumentException) { invalidSku = true; }
Check(invalidSku, "An unsupported Redis SKU was accepted.");
var invalidPrefix = false;
try { SemanticCacheDeployLab.Parse(["--prefix", "../bad"]); }
catch (ArgumentException) { invalidPrefix = true; }
Check(invalidPrefix, "An unsafe resource-group prefix was accepted.");
Console.WriteLine("PASS: semantic-cache deployment offline regression checks");
