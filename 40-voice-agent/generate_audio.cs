#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Diagnostics;
using System.Text;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Usage: dotnet run --file generate_audio.cs\nRegenerates assets/turn.wav with macOS say (Samantha, 145 wpm, PCM16 mono 24 kHz).");
    return 0;
}
var hasSay = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
    .Any(directory => File.Exists(Path.Combine(directory, "say"))) == true;
if (!OperatingSystem.IsMacOS() || !hasSay)
{
    Console.Error.WriteLine("Regeneration needs macOS say; the committed WAV works on other OSes.");
    return 1;
}
var root = Directory.GetCurrentDirectory();
var wav = Path.Combine(root, "assets", "turn.wav");
var start = new ProcessStartInfo("say") { WorkingDirectory = root, UseShellExecute = false };
foreach (var value in new[] { "-v", "Samantha", "-r", "145", "-f", Path.Combine(root, "assets", "turn.txt"),
    "-o", wav, "--file-format=WAVE", "--data-format=LEI16@24000" })
    start.ArgumentList.Add(value);
using (var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start macOS say."))
{
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException($"macOS say exited with status {process.ExitCode}.");
}
using var stream = File.OpenRead(wav);
using var reader = new BinaryReader(stream, Encoding.ASCII);
if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Generated output is not WAV.");
_ = reader.ReadUInt32();
if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Generated output is not WAV.");
var channels = 0;
var sampleRate = 0;
var bitsPerSample = 0;
while (stream.Position + 8 <= stream.Length)
{
    var chunk = new string(reader.ReadChars(4));
    var size = reader.ReadUInt32();
    if (chunk == "fmt ")
    {
        var format = reader.ReadUInt16();
        channels = reader.ReadUInt16();
        sampleRate = checked((int)reader.ReadUInt32());
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt16();
        bitsPerSample = reader.ReadUInt16();
        if (format != 1) throw new InvalidDataException("Generated output must use uncompressed PCM.");
    }
    stream.Position += size + (size & 1);
}
if ((channels, bitsPerSample, sampleRate) != (1, 16, 24000))
    throw new InvalidDataException("Generated output must be mono PCM16 at 24000 Hz.");
Console.WriteLine($"Regenerated {wav}.");
return 0;
