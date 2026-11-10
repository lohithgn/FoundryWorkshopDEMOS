#:property TargetFramework=net10.0
using System.Diagnostics;
using System.Text;

var lab = Environment.CurrentDirectory;
if (!File.Exists(Path.Combine(lab, "assets", "spoken.txt")))
    throw new DirectoryNotFoundException("Run this file from 41-content-understanding.");
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Regenerate the deterministic PDF and synthesized audio fixture. Audio regeneration requires macOS say.");
    return;
}

var assets = Path.Combine(lab, "assets");
var text = await File.ReadAllTextAsync(Path.Combine(assets, "notice.txt"));
await File.WriteAllBytesAsync(Path.Combine(assets, "notice.pdf"), PdfBytes(text));
if (!OperatingSystem.IsMacOS())
    throw new InvalidOperationException("Audio regeneration needs macOS say; use committed WAV on other OSes.");
var start = new ProcessStartInfo("say") { UseShellExecute = false };
foreach (var value in new[] { "-v", "Samantha", "-r", "145", "-f", Path.Combine(assets, "spoken.txt"),
    "-o", Path.Combine(assets, "notice.wav"), "--file-format=WAVE", "--data-format=LEI16@24000" })
    start.ArgumentList.Add(value);
using (var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start macOS say."))
{
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException($"say failed with exit code {process.ExitCode}.");
}
var wav = await File.ReadAllBytesAsync(Path.Combine(assets, "notice.wav"));
if (Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || BitConverter.ToInt16(wav, 22) != 1
    || BitConverter.ToInt16(wav, 34) != 16 || BitConverter.ToInt32(wav, 24) != 24000)
    throw new InvalidDataException("Generated audio must be mono, 16-bit, 24000Hz WAVE.");
Console.WriteLine("Regenerated notice.pdf and notice.wav.");

static byte[] PdfBytes(string text)
{
    var lines = text.Split('\n');
    if (text.EndsWith('\n')) lines = lines[..^1];
    var commands = new List<string> { "BT /F1 18 Tf 72 740 Td" };
    for (var index = 0; index < lines.Length; index++)
    {
        var escaped = lines[index].TrimEnd('\r').Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        if (index > 0) commands.Add("0 -30 Td");
        commands.Add($"({escaped}) Tj");
    }
    var stream = Encoding.ASCII.GetBytes(string.Join('\n', commands.Append("ET")) + "\n");
    var objects = new[] {
        Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
        Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
        Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        Encoding.ASCII.GetBytes($"<< /Length {stream.Length} >>\nstream\n").Concat(stream).Concat(Encoding.ASCII.GetBytes("endstream")).ToArray()
    };
    using var output = new MemoryStream();
    output.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
    var offsets = new List<int> { 0 };
    for (var index = 0; index < objects.Length; index++)
    {
        offsets.Add((int)output.Position);
        output.Write(Encoding.ASCII.GetBytes($"{index + 1} 0 obj\n"));
        output.Write(objects[index]);
        output.Write(Encoding.ASCII.GetBytes("\nendobj\n"));
    }
    var start = output.Position;
    output.Write(Encoding.ASCII.GetBytes($"xref\n0 {offsets.Count}\n0000000000 65535 f \n"));
    foreach (var offset in offsets.Skip(1)) output.Write(Encoding.ASCII.GetBytes($"{offset:0000000000} 00000 n \n"));
    output.Write(Encoding.ASCII.GetBytes($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF\n"));
    return output.ToArray();
}
