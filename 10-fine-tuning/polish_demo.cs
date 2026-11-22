#!/usr/bin/env dotnet
#:property JsonSerializerIsReflectionEnabledByDefault=true
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class PolishFineTuningDemo
{
    public static readonly string Root = Path.GetFullPath(
        Environment.GetEnvironmentVariable("POLISH_DEMO_ROOT") ?? Directory.GetCurrentDirectory());
    public static readonly string DataPath = Path.Combine(Root, "polish_culture.json");
    public static readonly string ConfigPath = Path.Combine(Root, "polish_lora.json");
    public static readonly string ReplayPath = Path.Combine(Root, "polish_replay.json");
    public static readonly string ConfirmationPath = Path.Combine(Root, "polish_confirmation.json");
    public const string Model = "mlx-community/Phi-3.5-mini-instruct-4bit";
    public const string Revision = "7b2052fd882fe017300d4d42a4eb06a27b816af4";
    public const int MaxTokens = 100;
    public const string StopToken = "<|end|>";
    private const string EvaluationSuite = "post-exploratory frozen confirmation";
    private const string NativeBlocker =
        "Native MLX language-model inference/training is unavailable from this C# file app. " +
        "The available Itexoft.MLX NuGet binding (including its .NET 10 package) exposes the MLX array/C API, " +
        "not the mlx-lm model loader, Phi tokenizer/chat-template generation, or mlx_lm.lora trainer used here. " +
        "No Python subprocess or substitute inference/training is used. Add a supported .NET model/tokenizer/LoRA " +
        "integration (or an explicitly supported native bridge) before running baseline, train, or evaluate.";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string PythonStyleJson(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(", ", obj.Select(pair =>
            QuoteJson(pair.Key) + ": " + PythonStyleJson(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(", ", array.Select(PythonStyleJson)) + "]",
        JsonValue value when value.TryGetValue<string>(out var text) =>
            QuoteJson(text),
        _ => node?.ToJsonString(CompactJsonOptions) ?? "null",
    };

    private static string QuoteJson(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break;
                case '\f': result.Append("\\f"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (character < 0x20)
                        result.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        result.Append(character);
                    break;
            }
        }
        return result.Append('"').ToString();
    }

    public static JsonNode ReadJson(string path) =>
        JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))
        ?? throw new JsonException($"Expected JSON value in {path}.");

    public static void WriteJson(string path, JsonNode value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(value.ToJsonString(JsonOptions));
        writer.Write('\n');
    }

    public static string Digest(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public static string Normalize(string text)
    {
        var decomposed = text.Replace('\u0142', 'l').Replace('\u0141', 'L')
            .ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
                result.Append(character);
            else
                result.Append(' ');
        }
        return Regex.Replace(result.ToString(), @"\s+", " ").Trim();
    }

    private static bool Contains(string text, string term)
    {
        var normalized = Normalize(term);
        if (normalized.Length == 0) return false;
        var end = char.IsDigit(normalized[^1]) ? @"\b" : "";
        return Regex.IsMatch(text, @"\b" + Regex.Escape(normalized) + end, RegexOptions.CultureInvariant);
    }

    public static JsonObject Score(string answer, JsonArray required, JsonArray forbidden)
    {
        var text = Normalize(answer);
        var missing = new JsonArray();
        var matched = 0;
        foreach (var groupNode in required)
        {
            var group = groupNode?.AsArray() ?? throw new ArgumentException("Rubric groups must be arrays.");
            var hit = group.Any(term => term is not null && Contains(text, term.GetValue<string>()));
            if (hit) matched++;
            else missing.Add((JsonNode)group.DeepClone());
        }
        var forbiddenMatches = new JsonArray();
        foreach (var termNode in forbidden)
        {
            if (termNode is not null && Contains(text, termNode.GetValue<string>()))
                forbiddenMatches.Add(termNode.DeepClone());
        }
        return new JsonObject
        {
            ["facts_matched"] = matched,
            ["facts_total"] = required.Count,
            ["missing"] = missing,
            ["forbidden_matches"] = forbiddenMatches,
            ["passed"] = matched == required.Count && forbiddenMatches.Count == 0,
        };
    }

    private static string String(JsonNode? node) =>
        node?.GetValue<string>() ?? throw new ArgumentException("Expected a non-null JSON string.");

    private static JsonArray Array(JsonObject obj, string key) =>
        obj[key] as JsonArray ?? throw new ArgumentException($"Expected '{key}' to be an array.");

    public static JsonObject LoadData() => ValidateData((JsonObject)ReadJson(DataPath));

    public static JsonObject ValidateData(JsonObject data)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var prompts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Array(data, "topics"))
        {
            var topic = node?.AsObject() ?? throw new ArgumentException("Topic must be an object.");
            var id = String(topic["id"]);
            if (!ids.Add(id)) throw new ArgumentException($"Duplicate topic: {id}");
            if (!String(topic["source"]).StartsWith("https://", StringComparison.Ordinal) ||
                Array(topic, "required").Count == 0)
                throw new ArgumentException($"Missing source or rubric: {id}");
            if (Score(String(topic["answer"]), Array(topic, "required"), Array(topic, "forbidden"))["passed"]!
                .GetValue<bool>() is false)
                throw new ArgumentException($"Reference answer fails rubric: {id}");
            var questions = Array(topic, "train").Select(String).Append(String(topic["valid"]))
                .Append(String(topic["test"]));
            foreach (var prompt in questions)
            {
                var key = Normalize(prompt);
                if (key.Length == 0 || !prompts.Add(key))
                    throw new ArgumentException($"Empty or overlapping split prompt: {prompt}");
            }
        }
        return data;
    }

    public static JsonArray EvaluationItems(JsonObject? dataOverride = null,
        JsonObject? confirmationOverride = null, JsonObject? replayOverride = null)
    {
        var data = dataOverride ?? LoadData();
        var confirmation = confirmationOverride ?? (JsonObject)ReadJson(ConfirmationPath);
        var replay = replayOverride ?? (JsonObject)ReadJson(ReplayPath);
        var topics = Array(data, "topics");
        var questions = Array(confirmation, "questions");
        if (!questions.Select(row => String(row!["id"]))
                .SequenceEqual(topics.Select(topic => String(topic!["id"])), StringComparer.Ordinal))
            throw new ArgumentException("Confirmation questions must cover every domain topic exactly once.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topicNode in topics)
        {
            var topic = topicNode!.AsObject();
            foreach (var prompt in Array(topic, "train").Select(String)
                         .Append(String(topic["valid"])).Append(String(topic["test"])))
                seen.Add(Normalize(prompt));
        }
        foreach (var controlNode in Array(data, "controls"))
            seen.Add(Normalize(String(controlNode!["prompt"])));
        foreach (var split in new[] { "train", "valid" })
        {
            foreach (var itemNode in Array(replay, split))
            {
                var item = itemNode!.AsObject();
                var key = Normalize(String(item["question"]));
                if (key.Length == 0 || seen.Contains(key) || string.IsNullOrWhiteSpace(String(item["answer"])))
                    throw new ArgumentException("Replay question overlaps an existing split or is empty.");
                seen.Add(key);
            }
        }

        var result = new JsonArray();
        for (var index = 0; index < topics.Count; index++)
        {
            var item = (JsonObject)topics[index]!.DeepClone();
            item["test"] = String(questions[index]!["question"]);
            result.Add((JsonNode)item);
        }
        foreach (var control in Array(confirmation, "controls"))
            result.Add(control!.DeepClone());

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var itemNode in result)
        {
            var item = itemNode!.AsObject();
            var prompt = Normalize(item["test"] is not null ? String(item["test"]) : String(item["prompt"]));
            if (prompt.Length == 0 || !seen.Add(prompt) || !ids.Add(String(item["id"])))
                throw new ArgumentException("Confirmation question or ID is empty, duplicated, or leaks into another split.");
            var required = Array(item, "required");
            if (required.Count == 0 || required.Any(groupNode =>
                    groupNode is not JsonArray group || group.Count == 0 ||
                    group.Any(term => term is null || Normalize(String(term)).Length == 0)))
                throw new ArgumentException("Confirmation rubric must contain nonempty concept groups.");
        }
        return result;
    }

    private static JsonObject BuildManifest(string output, bool includeOutputHashes)
    {
        var dataDir = Path.Combine(output, "data");
        var manifest = new JsonObject
        {
            ["model"] = Model,
            ["revision"] = Revision,
            ["dataset_sha256"] = Digest(DataPath),
            ["config_sha256"] = Digest(ConfigPath),
            ["replay_sha256"] = Digest(ReplayPath),
            ["confirmation_sha256"] = Digest(ConfirmationPath),
            ["evaluation_suite"] = EvaluationSuite,
        };
        if (includeOutputHashes)
        {
            manifest["train_sha256"] = Digest(Path.Combine(dataDir, "train.jsonl"));
            manifest["valid_sha256"] = Digest(Path.Combine(dataDir, "valid.jsonl"));
        }
        var data = LoadData();
        manifest["minimum_accuracy_gain"] = data["minimum_accuracy_gain"]!.DeepClone();
        manifest["max_tokens"] = MaxTokens;
        manifest["temperature"] = JsonNode.Parse("0.0");
        manifest["seed"] = 42;
        manifest["additional_stop_token"] = StopToken;
        if (includeOutputHashes)
            manifest["split_scope"] = "Unseen questions about taught facts; not unseen facts or entities.";
        return manifest;
    }

    public static void Prepare(string output)
    {
        var data = LoadData();
        _ = EvaluationItems(data);
        var directory = Path.Combine(output, "data");
        if (Directory.Exists(directory))
            throw new IOException($"{directory} exists; use a new output directory.");
        Directory.CreateDirectory(directory);
        var replay = (JsonObject)ReadJson(ReplayPath);
        foreach (var split in new[] { "train", "valid" })
        {
            var path = Path.Combine(directory, $"{split}.jsonl");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var topicNode in Array(data, "topics"))
            {
                var topic = topicNode!.AsObject();
                var prompts = split == "train"
                    ? Array(topic, "train").Select(String)
                    : [String(topic["valid"])];
                foreach (var prompt in prompts)
                    WriteTrainingRow(writer, String(data["system"]), prompt, String(topic["answer"]));
            }
            foreach (var itemNode in Array(replay, split))
            {
                var item = itemNode!.AsObject();
                WriteTrainingRow(writer, String(data["system"]), String(item["question"]), String(item["answer"]));
            }
        }
        WriteJson(Path.Combine(output, "dataset.json"), ReadJson(DataPath));
        WriteJson(Path.Combine(output, "replay.json"), ReadJson(ReplayPath));
        WriteJson(Path.Combine(output, "confirmation.json"), ReadJson(ConfirmationPath));
        WriteJson(Path.Combine(output, "lora-config.json"), ReadJson(ConfigPath));
        WriteJson(Path.Combine(output, "manifest.json"), BuildManifest(output, includeOutputHashes: true));
    }

    private static void WriteTrainingRow(StreamWriter writer, string system, string question, string answer)
    {
        var row = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = question },
                new JsonObject { ["role"] = "assistant", ["content"] = answer }),
        };
        writer.WriteLine(PythonStyleJson(row));
    }

    public static JsonObject CheckManifest(string output)
    {
        var manifest = (JsonObject)ReadJson(Path.Combine(output, "manifest.json"));
        var current = BuildManifest(output, includeOutputHashes: true);
        foreach (var key in current.Select(pair => pair.Key))
        {
            if (!JsonNode.DeepEquals(manifest[key], current[key]))
                throw new ArgumentException("Experiment inputs changed. Start a new output directory.");
        }
        return manifest;
    }

    public static void Evaluate(string output, bool tuned)
    {
        var manifest = CheckManifest(output);
        var target = Path.Combine(output, tuned ? "tuned.json" : "baseline.json");
        if (File.Exists(target)) throw new IOException($"Refusing to overwrite {target}");
        var adapter = Path.Combine(output, "adapters", "adapters.safetensors");
        if (tuned)
        {
            var training = (JsonObject)ReadJson(Path.Combine(output, "training.json"));
            if (String(training["adapter_sha256"]) != Digest(adapter))
                throw new ArgumentException("Adapter differs from the completed training run.");
        }
        throw new NativeMlxUnavailableException(NativeBlocker);
    }

    public static void Train(string output)
    {
        var manifest = CheckManifest(output);
        var baseline = (JsonObject)ReadJson(Path.Combine(output, "baseline.json"));
        if (!JsonNode.DeepEquals(baseline["manifest"], ReadJson(Path.Combine(output, "manifest.json"))) ||
            baseline["adapter_sha256"] is not null)
            throw new ArgumentException("A matching unadapted baseline is required before training.");
        if (Directory.Exists(Path.Combine(output, "adapters")))
            throw new IOException("Adapter directory exists; refusing to overwrite training.");
        _ = manifest;
        throw new NativeMlxUnavailableException(NativeBlocker);
    }

    public static JsonObject Metrics(JsonArray rows, bool control)
    {
        var selected = rows.Select(node => node!.AsObject())
            .Where(row => (String(row["category"]) == "control") == control).ToArray();
        if (selected.Length == 0) throw new ArgumentException("Missing evaluation rows.");
        var passed = selected.Count(row => row["passed"]!.GetValue<bool>());
        var factsMatched = selected.Sum(row => row["facts_matched"]!.GetValue<int>());
        var factsTotal = selected.Sum(row => row["facts_total"]!.GetValue<int>());
        return new JsonObject
        {
            ["passed"] = passed,
            ["total"] = selected.Length,
            ["accuracy"] = (double)passed / selected.Length,
            ["fact_coverage"] = (double)factsMatched / factsTotal,
        };
    }

    public static JsonObject Compare(string output)
    {
        var manifest = CheckManifest(output);
        var before = (JsonObject)ReadJson(Path.Combine(output, "baseline.json"));
        var after = (JsonObject)ReadJson(Path.Combine(output, "tuned.json"));
        if (!JsonNode.DeepEquals(before["manifest"], manifest) || !JsonNode.DeepEquals(after["manifest"], manifest))
            throw new ArgumentException("Baseline and tuned experiment inputs differ.");
        if (!JsonNode.DeepEquals(before["runtime"], after["runtime"]))
            throw new ArgumentException("Baseline and tuned library versions differ.");
        if (before["adapter_sha256"] is not null || after["adapter_sha256"] is null)
            throw new ArgumentException("Expected unadapted baseline and adapted final evaluation.");
        var training = (JsonObject)ReadJson(Path.Combine(output, "training.json"));
        if (!JsonNode.DeepEquals(after["adapter_sha256"], training["adapter_sha256"]))
            throw new ArgumentException("Evaluation used a different adapter.");

        var items = EvaluationItems();
        var beforeRows = Array(before, "results");
        var afterRows = Array(after, "results");
        var expected = items.Select(item => (
            Id: String(item!["id"]),
            Question: String(item!["test"] ?? item["prompt"]))).ToArray();
        foreach (var reportRows in new[] { beforeRows, afterRows })
        {
            if (reportRows.Count != expected.Length)
                throw new ArgumentException("Evaluation questions are missing or mismatched.");
            for (var index = 0; index < reportRows.Count; index++)
            {
                var row = reportRows[index]!.AsObject();
                var item = items[index]!.AsObject();
                if (String(row["id"]) != expected[index].Id || String(row["question"]) != expected[index].Question)
                    throw new ArgumentException("Evaluation questions are missing or mismatched.");
                if (String(row["category"]) != (String(item["category"] ?? JsonValue.Create("control"))))
                    throw new ArgumentException("Evaluation category differs from the frozen dataset.");
                var rescored = Score(String(row["answer"]), Array(item, "required"), Array(item, "forbidden"));
                foreach (var pair in rescored)
                    if (!JsonNode.DeepEquals(row[pair.Key], pair.Value))
                        throw new ArgumentException("Stored score differs from the frozen rubric.");
            }
        }

        var baseMetrics = Metrics(beforeRows, control: false);
        var tunedMetrics = Metrics(afterRows, control: false);
        var baseControls = Metrics(beforeRows, control: true);
        var tunedControls = Metrics(afterRows, control: true);
        var gain = tunedMetrics["accuracy"]!.GetValue<double>() - baseMetrics["accuracy"]!.GetValue<double>();
        var minimumGain = manifest["minimum_accuracy_gain"]!.GetValue<double>();
        var passedComparison = gain >= minimumGain &&
                               tunedControls["accuracy"]!.GetValue<double>() >= baseControls["accuracy"]!.GetValue<double>();
        var result = new JsonObject
        {
            ["baseline"] = baseMetrics,
            ["tuned"] = tunedMetrics,
            ["accuracy_gain"] = gain,
            ["baseline_controls"] = baseControls,
            ["tuned_controls"] = tunedControls,
            ["minimum_accuracy_gain"] = minimumGain,
            ["verified_improvement"] = passedComparison,
            ["limitations"] = new JsonArray(
                "Small lexical concept rubric, not an independent semantic judge.",
                "Held-out phrasing of taught facts, not held-out knowledge.",
                "Six controls cannot establish absence of general capability regressions.",
                "Inspect full answers for contradictions not covered by the rubric."),
        };
        var reportPath = Path.Combine(output, "comparison.json");
        if (File.Exists(reportPath))
        {
            if (!JsonNode.DeepEquals(ReadJson(reportPath), result))
                throw new ArgumentException("Existing comparison differs from recomputed results.");
        }
        else
            WriteJson(reportPath, result);
        Console.WriteLine(result.ToJsonString(JsonOptions));
        if (!passedComparison)
            throw new InvalidOperationException("Required domain gain or control non-regression threshold was not met.");
        return result;
    }

    public static int Run(string[] args)
    {
        try
        {
            string? stage = null;
            var output = Path.Combine(Root, ".azure", "polish-phi");
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--help":
                        Console.WriteLine("Usage: dotnet run --file polish_demo.cs -- --stage prepare|baseline|train|evaluate|compare|all [--output PATH]");
                        Console.WriteLine(NativeBlocker);
                        return 0;
                    case "--stage" when index + 1 < args.Length: stage = args[++index]; break;
                    case "--output" when index + 1 < args.Length: output = args[++index]; break;
                    default: throw new ArgumentException($"Unknown or incomplete option: {args[index]}");
                }
            }
            if (stage is not ("prepare" or "baseline" or "train" or "evaluate" or "compare" or "all"))
                throw new ArgumentException("--stage is required and must be prepare, baseline, train, evaluate, compare, or all.");
            output = Path.GetFullPath(output, Root);
            var stages = stage == "all" ? new[] { "prepare", "baseline", "train", "evaluate", "compare" } : [stage];
            foreach (var current in stages)
            {
                switch (current)
                {
                    case "prepare": Prepare(output); break;
                    case "baseline": Evaluate(output, tuned: false); break;
                    case "train": Train(output); break;
                    case "evaluate": Evaluate(output, tuned: true); break;
                    case "compare": Compare(output); break;
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.Message}");
            return 1;
        }
    }

#pragma warning disable CS7022
    public static int Main(string[] args) => Run(args);
#pragma warning restore CS7022

    public sealed class NativeMlxUnavailableException(string message) : InvalidOperationException(message);
}
