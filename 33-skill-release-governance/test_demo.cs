var start = new System.Diagnostics.ProcessStartInfo("dotnet")
{
    WorkingDirectory = Environment.CurrentDirectory,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
start.ArgumentList.Add("run");
start.ArgumentList.Add("--file");
start.ArgumentList.Add("demo.cs");
start.ArgumentList.Add("--");
start.ArgumentList.Add("--self-test");
using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start offline checks.");
var output = await process.StandardOutput.ReadToEndAsync();
var error = await process.StandardError.ReadToEndAsync();
await process.WaitForExitAsync();
if (process.ExitCode != 0 || !output.Contains("PASS:", StringComparison.Ordinal))
    throw new Exception($"Offline release regression failed ({process.ExitCode}): {output}\n{error}");
Console.WriteLine(output.Trim());
