#!/usr/bin/env dotnet
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:include polish_demo.cs
using System.Text.Json.Nodes;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static JsonArray Items(JsonObject data, string key) => data[key]!.AsArray();
static string Text(JsonNode? node) => node!.GetValue<string>();

var data = PolishFineTuningDemo.LoadData();
Check(Items(data, "topics").Count == 16, "Expected sixteen sourced topics.");
Check(Items(data, "topics").Sum(topic => Items(topic!.AsObject(), "train").Count) == 64,
    "Expected sixty-four domain training prompts.");
Check(Items(data, "controls").Count == 4, "Expected four original controls.");
Check(PolishFineTuningDemo.EvaluationItems().Count == 22, "Confirmation suite must contain 22 items.");
Check(Items(PolishFineTuningDemo.EvaluationItems().AsArray()[0]!.AsObject(), "required").Count > 0,
    "Domain evaluation rubrics disappeared.");

Check(PolishFineTuningDemo.Normalize("Kraków, Łódź, Śląsk!") == "krakow lodz slask",
    "Polish diacritic normalization changed.");
Check(PolishFineTuningDemo.Score("Poznań: white poppy seeds.",
        new JsonArray(new JsonArray("poznan"), new JsonArray("white poppy")), new JsonArray())["passed"]!
        .GetValue<bool>(),
    "Diacritic-insensitive rubric matching failed.");
var rubric = new JsonArray(new JsonArray("pancake", "fritter"), new JsonArray("sauerkraut"));
Check(PolishFineTuningDemo.Score("Sauerkraut pancakes.", rubric, new JsonArray())["passed"]!.GetValue<bool>(),
    "Prefix matching should allow inflections.");
Check(!PolishFineTuningDemo.Score("Pancakes.", rubric, new JsonArray())["passed"]!.GetValue<bool>(),
    "Missing concepts were accepted.");
Check(!PolishFineTuningDemo.Score("Sauerkraut pancakes, a sweet dessert.", rubric,
        new JsonArray("sweet dessert"))["passed"]!.GetValue<bool>(),
    "Forbidden concepts were accepted.");
Check(!PolishFineTuningDemo.Score("560", new JsonArray(new JsonArray("56")), new JsonArray())["passed"]!
        .GetValue<bool>(),
    "Digit-prefix matching should require a word boundary.");
Check(!PolishFineTuningDemo.Score("hempstead", new JsonArray(new JsonArray("hemp seed")), new JsonArray())["passed"]!
        .GetValue<bool>(),
    "Partial phrase matching was unexpectedly accepted.");

var overlapping = (JsonObject)data.DeepClone();
overlapping["topics"]![0]!["test"] = Text(overlapping["topics"]![0]!["train"]![0]);
var overlapRejected = false;
try { PolishFineTuningDemo.ValidateData(overlapping); }
catch (ArgumentException error) { overlapRejected = error.Message.Contains("overlapping split"); }
Check(overlapRejected, "Overlapping train/test prompts were accepted.");

var leakedConfirmation = (JsonObject)PolishFineTuningDemo.ReadJson(PolishFineTuningDemo.ConfirmationPath);
leakedConfirmation["questions"]![0]!["question"] = Text(data["topics"]![0]!["train"]![0]);
var leakRejected = false;
try { PolishFineTuningDemo.EvaluationItems(data, leakedConfirmation); }
catch (ArgumentException error) { leakRejected = error.Message.Contains("leaks"); }
Check(leakRejected, "Confirmation question leakage was accepted.");

var work = Path.Combine(PolishFineTuningDemo.Root, $".polish-demo-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(work);
try
{
    var output = Path.Combine(work, "prepared");
    PolishFineTuningDemo.Prepare(output);
    Check(Directory.GetFiles(Path.Combine(output, "data")).Select(Path.GetFileName).Order()
            .SequenceEqual(new[] { "train.jsonl", "valid.jsonl" }),
        "Prepare emitted unexpected split files.");
    var train = File.ReadAllLines(Path.Combine(output, "data", "train.jsonl"))
        .Where(line => line.Length > 0).Select(line => JsonNode.Parse(line)).ToArray();
    var valid = File.ReadAllLines(Path.Combine(output, "data", "valid.jsonl"))
        .Where(line => line.Length > 0).Select(line => JsonNode.Parse(line)).ToArray();
    Check(train.Length == 80 && valid.Length == 20, "Prepared train/valid row counts changed.");
    var preparedPrompts = train.Concat(valid)
        .Select(row => Text(row!["messages"]![1]!["content"])).ToHashSet(StringComparer.Ordinal);
    foreach (var topic in Items(data, "topics"))
        Check(!preparedPrompts.Contains(Text(topic!["test"])), "A confirmation domain prompt leaked into training.");
    foreach (var control in Items(data, "controls"))
        Check(!preparedPrompts.Contains(Text(control!["prompt"])), "A control prompt leaked into training.");
    foreach (var item in PolishFineTuningDemo.EvaluationItems())
        Check(!preparedPrompts.Contains(Text(item!["test"] ?? item["prompt"])),
            "A frozen evaluation prompt leaked into training.");
    _ = PolishFineTuningDemo.CheckManifest(output);
    var repeatedPrepareRejected = false;
    try { PolishFineTuningDemo.Prepare(output); }
    catch (IOException) { repeatedPrepareRejected = true; }
    Check(repeatedPrepareRejected, "Prepare overwrote an existing run.");

    File.AppendAllText(Path.Combine(output, "data", "train.jsonl"), "{}\n");
    var changedInputRejected = false;
    try { _ = PolishFineTuningDemo.CheckManifest(output); }
    catch (ArgumentException error) { changedInputRejected = error.Message.Contains("inputs changed"); }
    Check(changedInputRejected, "Changed training data did not invalidate the manifest.");

    var nativeFailure = false;
    try { PolishFineTuningDemo.Evaluate(output, tuned: false); }
    catch (ArgumentException) { }
    catch (PolishFineTuningDemo.NativeMlxUnavailableException error)
    {
        nativeFailure = error.Message.Contains("mlx-lm model loader") &&
                        error.Message.Contains("No Python subprocess");
    }
    Check(!nativeFailure, "A corrupted manifest should be rejected before attempting native inference.");

    var reportOutput = Path.Combine(work, "comparison");
    PolishFineTuningDemo.Prepare(reportOutput);
    WriteSyntheticReports(reportOutput, improve: true, regressControl: false);
    var comparison = PolishFineTuningDemo.Compare(reportOutput);
    Check(comparison["verified_improvement"]!.GetValue<bool>() &&
          comparison["accuracy_gain"]!.GetValue<double>() == 1.0,
        "Offline report comparison did not detect the expected rubric score gain.");
    Check(JsonNode.DeepEquals(PolishFineTuningDemo.ReadJson(Path.Combine(reportOutput, "comparison.json")), comparison),
        "Comparison report was not retained consistently.");
    Check(JsonNode.DeepEquals(PolishFineTuningDemo.Compare(reportOutput), comparison),
        "Repeated comparison changed the retained report.");

    foreach (var (improve, regress) in new[] { (false, false), (true, true) })
    {
        var caseOutput = Path.Combine(work, $"comparison-{improve}-{regress}");
        PolishFineTuningDemo.Prepare(caseOutput);
        WriteSyntheticReports(caseOutput, improve, regress);
        var thresholdRejected = false;
        try { _ = PolishFineTuningDemo.Compare(caseOutput); }
        catch (InvalidOperationException error) { thresholdRejected = error.Message.Contains("threshold"); }
        Check(thresholdRejected, "No-gain or control-regression comparison passed.");
        Check(!PolishFineTuningDemo.ReadJson(Path.Combine(caseOutput, "comparison.json"))["verified_improvement"]!
                .GetValue<bool>(),
            "Failed comparison evidence was not retained.");
    }

    var corruptOutput = Path.Combine(work, "comparison-corrupt");
    PolishFineTuningDemo.Prepare(corruptOutput);
    WriteSyntheticReports(corruptOutput, improve: true, regressControl: false);
    var tunedReportPath = Path.Combine(corruptOutput, "tuned.json");
    var tunedReport = (JsonObject)PolishFineTuningDemo.ReadJson(tunedReportPath);
    tunedReport["results"]![0]!["passed"] = false;
    File.WriteAllText(tunedReportPath, tunedReport.ToJsonString());
    var scoreMismatchRejected = false;
    try { _ = PolishFineTuningDemo.Compare(corruptOutput); }
    catch (ArgumentException error) { scoreMismatchRejected = error.Message.Contains("Stored score"); }
    Check(scoreMismatchRejected, "Corrupted stored rubric scores were accepted.");

    var nativeOutput = Path.Combine(work, "native-blocker");
    PolishFineTuningDemo.Prepare(nativeOutput);
    var blockerVisible = false;
    try { PolishFineTuningDemo.Evaluate(nativeOutput, tuned: false); }
    catch (PolishFineTuningDemo.NativeMlxUnavailableException error)
    {
        blockerVisible = error.Message.Contains("mlx-lm model loader") &&
                         error.Message.Contains("No Python subprocess");
    }
    Check(blockerVisible, "Baseline evaluation did not fail with the explicit native API blocker.");

    var missingBaselineRejected = false;
    try { PolishFineTuningDemo.Train(nativeOutput); }
    catch (FileNotFoundException) { missingBaselineRejected = true; }
    Check(missingBaselineRejected, "Training proceeded without the required baseline.");

    var trainingOutput = Path.Combine(work, "training-blocker");
    PolishFineTuningDemo.Prepare(trainingOutput);
    PolishFineTuningDemo.WriteJson(Path.Combine(trainingOutput, "baseline.json"), new JsonObject
    {
        ["manifest"] = PolishFineTuningDemo.ReadJson(Path.Combine(trainingOutput, "manifest.json")),
        ["adapter_sha256"] = null,
    });
    blockerVisible = false;
    try { PolishFineTuningDemo.Train(trainingOutput); }
    catch (PolishFineTuningDemo.NativeMlxUnavailableException error)
    {
        blockerVisible = error.Message.Contains("mlx_lm.lora trainer") &&
                         error.Message.Contains("No Python subprocess");
    }
    Check(blockerVisible, "Training did not fail visibly at the MLX integration blocker.");

    var existingAdapterRejected = false;
    Directory.CreateDirectory(Path.Combine(trainingOutput, "adapters"));
    try { PolishFineTuningDemo.Train(trainingOutput); }
    catch (IOException error) { existingAdapterRejected = error.Message.Contains("refusing to overwrite"); }
    Check(existingAdapterRejected, "Training overwrote an existing adapter directory.");
}
finally
{
    if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
}

CheckRetainedEvidence();
Console.WriteLine("PASS: Polish fine-tuning offline regression checks (no model inference/training performed)");

static void WriteSyntheticReports(string output, bool improve, bool regressControl)
{
    var manifest = PolishFineTuningDemo.ReadJson(Path.Combine(output, "manifest.json"));
    foreach (var (tuned, fileName) in new[] { (false, "baseline.json"), (true, "tuned.json") })
    {
        var rows = new JsonArray();
        foreach (var itemNode in PolishFineTuningDemo.EvaluationItems())
        {
            var item = itemNode!.AsObject();
            var control = item["prompt"] is not null;
            var answer = control
                ? (tuned && regressControl ? "unknown" : Text(item["required"]![0]![0]))
                : (tuned && improve ? Text(item["answer"]) : "unknown");
            var score = PolishFineTuningDemo.Score(answer, Items(item, "required"), Items(item, "forbidden"));
            var row = new JsonObject
            {
                ["id"] = Text(item["id"]),
                ["category"] = Text(item["category"] ?? JsonValue.Create("control")),
                ["question"] = Text(item["test"] ?? item["prompt"]),
                ["answer"] = answer,
                ["facts_matched"] = score["facts_matched"]!.GetValue<int>(),
                ["facts_total"] = score["facts_total"]!.GetValue<int>(),
                ["missing"] = score["missing"]!.DeepClone(),
                ["forbidden_matches"] = score["forbidden_matches"]!.DeepClone(),
                ["passed"] = score["passed"]!.GetValue<bool>(),
            };
            if (item["answer"] is not null) row["reference"] = item["answer"]!.DeepClone();
            rows.Add((JsonNode)row);
        }
        PolishFineTuningDemo.WriteJson(Path.Combine(output, fileName), new JsonObject
        {
            ["manifest"] = manifest.DeepClone(),
            ["adapter_sha256"] = tuned ? "offline-test-only-adapter" : null,
            ["runtime"] = new JsonObject { ["test-fixture"] = "not-runtime-evidence" },
            ["results"] = rows,
        });
    }
    PolishFineTuningDemo.WriteJson(Path.Combine(output, "training.json"),
        new JsonObject { ["adapter_sha256"] = "offline-test-only-adapter" });
}

static void CheckRetainedEvidence()
{
    var evidence = Path.Combine(PolishFineTuningDemo.Root, "polish-results");
    var integration = Path.Combine(PolishFineTuningDemo.Root, $".polish-evidence-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(integration);
    try
    {
        var output = Path.Combine(integration, "run");
        PolishFineTuningDemo.Prepare(output);
        Check(JsonNode.DeepEquals(PolishFineTuningDemo.ReadJson(Path.Combine(output, "manifest.json")),
                PolishFineTuningDemo.ReadJson(Path.Combine(evidence, "manifest.json"))),
            "C# preparation manifest differs from the frozen Python experiment manifest.");
        foreach (var name in new[] { "baseline.json", "tuned.json", "training.json", "comparison.json" })
            File.Copy(Path.Combine(evidence, name), Path.Combine(output, name));
        Check(JsonNode.DeepEquals(PolishFineTuningDemo.Compare(output),
                PolishFineTuningDemo.ReadJson(Path.Combine(evidence, "comparison.json"))),
            "C# comparison does not reproduce the retained Python experiment report.");
    }
    finally
    {
        if (Directory.Exists(integration)) Directory.Delete(integration, recursive: true);
    }

    var audit = (JsonObject)PolishFineTuningDemo.ReadJson(Path.Combine(evidence, "answer-audit.json"));
    foreach (var label in new[] { "baseline", "tuned" })
    {
        Check(Text(audit[$"{label}_sha256"]) ==
              PolishFineTuningDemo.Digest(Path.Combine(evidence, $"{label}.json")),
            $"Retained {label} audit hash does not match its transcript.");
        var passed = Items(audit, "results").Count(row => row![$"{label}_passed"]!.GetValue<bool>());
        Check(audit[$"{label}_passed"]!.GetValue<int>() == passed,
            $"Retained {label} audit pass count is inconsistent.");
    }
    Check(audit["total"]!.GetValue<int>() == Items(audit, "results").Count,
        "Retained answer-audit total is inconsistent.");
    Check((audit["baseline_passed"]!.GetValue<int>(), audit["tuned_passed"]!.GetValue<int>()) == (1, 11),
        "Retained answer-audit counts changed.");

    var training = PolishFineTuningDemo.ReadJson(Path.Combine(evidence, "training.json"));
    var recheck = PolishFineTuningDemo.ReadJson(Path.Combine(evidence, "exploratory-control-recheck.json"));
    Check(JsonNode.DeepEquals(recheck["adapter_sha256"], training["adapter_sha256"]),
        "Retained control recheck refers to a different adapter.");
    var controls = Items(PolishFineTuningDemo.LoadData(), "controls");
    var results = Items(recheck.AsObject(), "results");
    Check(controls.Count == results.Count, "Retained control recheck is incomplete.");
    for (var index = 0; index < controls.Count; index++)
    {
        Check(Text(controls[index]!["id"]) == Text(results[index]!["id"]),
            "Retained control IDs differ from the frozen source.");
        Check(PolishFineTuningDemo.Score(Text(results[index]!["answer"]), Items(controls[index]!.AsObject(), "required"),
                Items(controls[index]!.AsObject(), "forbidden"))["passed"]!.GetValue<bool>(),
            "A retained final-adapter control does not pass its rubric.");
    }
}
