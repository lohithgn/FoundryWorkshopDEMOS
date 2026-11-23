#:property TargetFramework=net10.0
using System.Diagnostics;

var lab = Environment.CurrentDirectory;
if (!File.Exists(Path.Combine(lab, "demo.cs")))
    throw new DirectoryNotFoundException("Run this file from 41-content-understanding.");
var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
foreach (var value in new[] { "run", "--file", "demo.cs", "--", "--self-test" })
    start.ArgumentList.Add(value);
start.RedirectStandardOutput = true;
start.RedirectStandardError = true;
using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the file-based app test harness.");
var stdout = process.StandardOutput.ReadToEnd();
var stderr = process.StandardError.ReadToEnd();
await process.WaitForExitAsync();
Console.Write(stdout);
Console.Error.Write(stderr);
if (process.ExitCode != 0) Environment.ExitCode = process.ExitCode;
