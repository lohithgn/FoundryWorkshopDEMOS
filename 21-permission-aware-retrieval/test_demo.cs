#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Diagnostics;

var start = new ProcessStartInfo("dotnet")
{
    UseShellExecute = false
};
start.ArgumentList.Add("run");
start.ArgumentList.Add("--file");
start.ArgumentList.Add(Path.Combine(Environment.CurrentDirectory, "demo.cs"));
start.ArgumentList.Add("--no-build");
start.ArgumentList.Add("--no-restore");
start.ArgumentList.Add("--");
start.ArgumentList.Add("--self-test");

using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start offline test runner.");
await process.WaitForExitAsync();
return process.ExitCode;
