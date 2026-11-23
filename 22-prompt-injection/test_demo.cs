#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Diagnostics;

var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
foreach (var value in new[] { "run", "--file", Path.Combine(Environment.CurrentDirectory, "demo.cs"), "--no-build", "--no-restore", "--", "--self-test" })
    start.ArgumentList.Add(value);
using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start offline test runner.");
await process.WaitForExitAsync();
return process.ExitCode;
