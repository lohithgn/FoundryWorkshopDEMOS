#!/usr/bin/env dotnet
#:package Azure.Identity@1.21.0
#:include demo.cs
#:property StartupObject=MultimodalTests

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class MultimodalTests
{
    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected {expected}, got {actual}");
    }
    private static void True(bool value, string label)
    {
        if (!value) throw new Exception(label);
    }

    private sealed record DecodedPng(int Width, int Height, byte[] Pixels)
    {
        public (byte R, byte G, byte B) Pixel(int x, int y)
        {
            var i = (y * Width + x) * 3;
            return (Pixels[i], Pixels[i + 1], Pixels[i + 2]);
        }
    }

    private static DecodedPng Decode(byte[] png)
    {
        if (!png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new Exception("Invalid PNG signature");
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        Equal((byte)8, png[24], "PNG bit depth");
        Equal((byte)2, png[25], "PNG RGB color type");
        using var compressed = new MemoryStream();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            var kind = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (kind == "IDAT") compressed.Write(png, offset + 8, length);
            offset += 12 + length;
            if (kind == "IEND") break;
        }
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflater.CopyTo(raw);
        var bytes = raw.ToArray();
        var stride = width * 3;
        var pixels = new byte[height * stride];
        var input = 0;
        for (var y = 0; y < height; y++)
        {
            var filter = bytes[input++];
            for (var x = 0; x < stride; x++)
            {
                var rawByte = bytes[input++];
                var left = x >= 3 ? pixels[y * stride + x - 3] : 0;
                var above = y > 0 ? pixels[(y - 1) * stride + x] : 0;
                var upperLeft = y > 0 && x >= 3 ? pixels[(y - 1) * stride + x - 3] : 0;
                pixels[y * stride + x] = filter switch
                {
                    0 => rawByte,
                    1 => unchecked((byte)(rawByte + left)),
                    2 => unchecked((byte)(rawByte + above)),
                    3 => unchecked((byte)(rawByte + ((left + above) / 2))),
                    4 => unchecked((byte)(rawByte + Paeth(left, above, upperLeft))),
                    _ => throw new Exception($"Unsupported PNG filter {filter}")
                };
            }
        }
        return new DecodedPng(width, height, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static Task TestSuppliedAssetsAndTruth()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "images");
        var truthJson = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "truth.json")));
        True(truthJson is JsonObject, "truth manifest object");
        var manifest = truthJson!.AsObject();
        Equal(Multimodal.Truth.Count, manifest.Count, "truth manifest count");
        foreach (var (name, value) in Multimodal.Truth)
            True(JsonNode.DeepEquals(manifest[name], value), $"truth entry {name}");
        foreach (var name in Multimodal.Truth.Keys)
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, name));
            var image = Decode(bytes);
            Equal(640, image.Width, $"{name} width");
            Equal(400, image.Height, $"{name} height");
            Equal(bytes.Length, Multimodal.ImageUrlBytes(Multimodal.ImageUrl(bytes)).Length, $"{name} base64 length");
            True(bytes.SequenceEqual(Multimodal.ImageUrlBytes(Multimodal.ImageUrl(bytes))), $"{name} data URL payload");
        }
        return Task.CompletedTask;
    }

    private static Task TestMaskAndLocalGenerator()
    {
        var supplied = Decode(File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "images", "ambiguous.png")));
        Equal((0, 0, 0), supplied.Pixel(300, 265), "supplied opaque amount mask");
        Equal((255, 255, 255), supplied.Pixel(40, 190), "no currency printed");
        var generatedAmbiguous = Decode(Multimodal.GenerateImage(true));
        Equal(640, generatedAmbiguous.Width, "generated PNG width");
        Equal((0, 0, 0), generatedAmbiguous.Pixel(300, 265), "generated opaque mask");
        Equal((255, 255, 255), generatedAmbiguous.Pixel(40, 190), "generated currency remains unmarked");
        var generatedClear = Decode(Multimodal.GenerateImage(false));
        Equal((0, 0, 0), generatedClear.Pixel(37, 190), "generated visible currency mark");
        Equal((0, 0, 0), generatedClear.Pixel(43, 260), "generated visible amount mark");
        return Task.CompletedTask;
    }

    private static Task TestTruthEvaluatesExactly()
    {
        foreach (var truth in Multimodal.Truth.Values)
        {
            var result = Multimodal.Evaluate(truth.ToJsonString(), truth);
            Equal("complete", (string?)result["status"], "truth completion");
            True((bool)result["correct"]!, "truth correctness");
        }
        return Task.CompletedTask;
    }

    private static Task TestValidGuessCanBeWrong()
    {
        var guess = new JsonObject
        {
            ["merchant"] = "WORKSHOP CAFE", ["currency"] = "USD", ["total"] = 12.5,
            ["uncertainty"] = new JsonArray()
        };
        var result = Multimodal.Evaluate(guess.ToJsonString(), Multimodal.Truth["ambiguous.png"]);
        True((bool)result["schema_ok"]!, "guess conforms to schema");
        True(!(bool)result["correct"]!, "guess fails visual truth");
        return Task.CompletedTask;
    }

    private static Task TestRefusalTruncationAndSchemaFailures()
    {
        var truth = Multimodal.Truth["clear.png"];
        Equal("refusal", (string?)Multimodal.Evaluate("", truth, "No")["status"], "refusal");
        Equal("incomplete", (string?)Multimodal.Evaluate(truth.ToJsonString(), truth, finishReason: "length")["status"], "truncation");
        Equal("invalid", (string?)Multimodal.Evaluate("{}", truth)["status"], "missing fields");
        Equal("invalid", (string?)Multimodal.Evaluate("broken", truth)["status"], "invalid JSON");
        var extra = JsonNode.Parse(truth.ToJsonString())!.AsObject();
        extra["extra"] = true;
        Equal("invalid", (string?)Multimodal.Evaluate(extra.ToJsonString(), truth)["status"], "additional property");
        return Task.CompletedTask;
    }

    private static Task TestTimeoutIsNotPrediction()
    {
        var result = Multimodal.Extract(() => throw new TimeoutException("offline fixture"), Multimodal.Truth["clear.png"]);
        Equal("request_error", (string?)result["status"], "request error status");
        True(result["usage"] is null, "unknown usage not fabricated");
        True(!(bool)result["correct"]!, "error is not a prediction");
        return Task.CompletedTask;
    }

    private static Task TestMissingUsageAndCost()
    {
        True(Multimodal.EstimatedCost(null, .4, 1.6) is null, "unknown cost remains null");
        var usage = JsonNode.Parse("""{"prompt_tokens":1000,"completion_tokens":100}""");
        Equal(.00056, Multimodal.EstimatedCost(usage, .4, 1.6)!.Value, "illustrative cost");
        return Task.CompletedTask;
    }

    private static Task TestStructuredVisionRequestContract()
    {
        var request = Multimodal.BuildRequest("receipt-vision", Multimodal.GenerateImage(false));
        Equal("receipt-vision", (string?)request["model"], "deployment name");
        Equal("json_schema", (string?)request["response_format"]!["type"], "structured output format");
        True((bool)request["response_format"]!["json_schema"]!["strict"]!, "strict schema");
        Equal("high", (string?)request["messages"]![1]!["content"]![1]!["image_url"]!["detail"], "high detail");
        var url = (string)request["messages"]![1]!["content"]![1]!["image_url"]!["url"]!;
        True(url.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "inline PNG data URL");
        True(Multimodal.ImageUrlBytes(url).SequenceEqual(Multimodal.GenerateImage(false)), "exact inline image bytes");
        return Task.CompletedTask;
    }

    private static Task TestGeneratorWritesAllOutputArtifacts()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".offline-generated-" + Guid.NewGuid().ToString("N"));
        try
        {
            Multimodal.GenerateReceipts(directory);
            var truth = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "truth.json")))!.AsObject();
            Equal(Multimodal.Truth.Count, truth.Count, "generated truth entries");
            foreach (var (name, value) in Multimodal.Truth)
            {
                True(JsonNode.DeepEquals(truth[name], value), $"generated truth {name}");
                var image = Decode(File.ReadAllBytes(Path.Combine(directory, name)));
                Equal(640, image.Width, $"{name} generated width");
                Equal(400, image.Height, $"{name} generated height");
                if (name == "ambiguous.png")
                {
                    Equal((0, 0, 0), image.Pixel(300, 265), "generated covered amount");
                    Equal((255, 255, 255), image.Pixel(40, 190), "generated unmarked currency");
                }
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        return Task.CompletedTask;
    }

    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("supplied PNGs and truth manifest", TestSuppliedAssetsAndTruth),
            ("opaque mask and local PNG generator", TestMaskAndLocalGenerator),
            ("explicit missing-value truth", TestTruthEvaluatesExactly),
            ("schema-valid visual guess can fail", TestValidGuessCanBeWrong),
            ("refusal, truncation, and schema errors", TestRefusalTruncationAndSchemaFailures),
            ("request timeout is not a prediction", TestTimeoutIsNotPrediction),
            ("unknown usage and cost calculation", TestMissingUsageAndCost),
            ("strict structured vision request", TestStructuredVisionRequestContract),
            ("generated PNG and truth artifacts", TestGeneratorWritesAllOutputArtifacts)
        };
        foreach (var test in tests)
        {
            await test.Run();
            Console.WriteLine($"PASS {test.Name}");
        }
        Console.WriteLine($"Passed {tests.Length} offline multimodal tests.");
        return 0;
    }
}
