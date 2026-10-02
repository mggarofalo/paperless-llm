using System.Text.Json;
using PaperlessLlm.Eval;

namespace PaperlessLlm.Tests;

public sealed class EvalCliTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-eval-cli-" + Guid.NewGuid().ToString("N"));

    public EvalCliTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task ExportKeepsHoldoutAndReferenceAnswersOutOfTrainingTasks()
    {
        var train = Case("train-case");
        var cases = await Cases(train, Case("holdout-case") with { Split = "holdout" });
        var output = Path.Combine(directory, "export");
        Assert.Equal(0, await EvalCli.RunAsync(["export", cases, output, "--split", "train"]));
        var rows = await File.ReadAllLinesAsync(Path.Combine(output, "tasks.jsonl"));
        var row = Assert.Single(rows);
        using var doc = JsonDocument.Parse(row);
        Assert.Equal("train-case", doc.RootElement.GetProperty("case_id").GetString());
        Assert.Contains("Acme synthetic receipt", row);
        Assert.DoesNotContain("curator-only reference", row);
        Assert.DoesNotContain("holdout-case", row);
    }

    [Fact]
    public async Task JsonLinesScoringCountsMalformedAndDuplicateImportsWithoutReplacingFirstResult()
    {
        var cases = await Cases(Case("good"), Case("string-intent"), Case("absent"));
        var outputs = await Write("outputs.jsonl", string.Join('\n',
            JsonSerializer.Serialize(new { case_id = "good", intent = JsonDocument.Parse(Keep).RootElement }),
            JsonSerializer.Serialize(new { case_id = "good", intent_json = "{}" }),
            JsonSerializer.Serialize(new { case_id = "string-intent", intent_json = Keep }),
            "not-json", "{\"case_id\":\"\"}", "{\"case_id\":\"good\"}",
            "{\"case_id\":\"unrelated\",\"intent\":{}}"));
        var report = await Score(cases, outputs);
        Assert.Equal(3, report.TotalCases);
        Assert.Equal(2, report.ImportedOutputs);
        Assert.Equal(1, report.MissingOutputs);
        Assert.Equal(4, report.InvalidJsonOutputs);
        Assert.Equal(0, report.ValidatorFailures);
        Assert.Equal(new FieldScore(2, 3), report.Checks["validator.valid"]);
        Assert.True(report.Cases.Single(c => c.CaseId == "good").Valid);
        Assert.Equal("missing_output", report.Cases.Single(c => c.CaseId == "absent").Failure);
    }

    [Fact]
    public async Task DirectoryScoringIgnoresProvenanceAndUnrelatedFilesButCountsInvalidIntent()
    {
        var cases = await Cases(Case("good"), Case("invalid"), Case("absent"));
        var outputs = Path.Combine(directory, "outputs");
        Directory.CreateDirectory(outputs);
        await File.WriteAllTextAsync(Path.Combine(outputs, "good.json"), Keep);
        await File.WriteAllTextAsync(Path.Combine(outputs, "invalid.json"), "not-json");
        await File.WriteAllTextAsync(Path.Combine(outputs, "unrelated.json"), "not-json");
        await File.WriteAllTextAsync(Path.Combine(outputs, "provenance.json"), "not-json");
        var report = await Score(cases, outputs);
        Assert.Equal(2, report.ImportedOutputs);
        Assert.Equal(1, report.MissingOutputs);
        Assert.Equal(1, report.SchemaFailures);
        Assert.Equal(1, report.ValidatorFailures);
        Assert.Equal(0, report.InvalidJsonOutputs);
        Assert.Equal("invalid_intent_json", report.Cases.Single(c => c.CaseId == "invalid").Failure);
    }

    [Fact]
    public async Task InvalidReferencesAndDuplicateCaseIdsAreRejectedBeforeExport()
    {
        var source = Case("invalid-reference");
        var invalid = source with { Expected = source.Expected with { Correspondent = new() { Action = "set", Value = 999 } } };
        var cases = await Cases(invalid);
        var output = Path.Combine(directory, "rejected");
        Assert.Equal(2, await EvalCli.RunAsync(["export", cases, output, "--split", "train"]));
        Assert.False(Directory.Exists(output));
        cases = await Cases(source, source);
        Assert.Equal(2, await EvalCli.RunAsync(["export", cases, output, "--split", "train"]));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task ComparisonRejectsDifferentCaseSetsEvenWhenAggregateCountsMatch()
    {
        var cases = await Cases(Case("first"));
        var outputs = await Write("outputs.jsonl", "");
        var baseline = await Score(cases, outputs);
        var candidate = baseline with { Cases = [baseline.Cases[0] with { CaseId = "different" }] };
        var baselineFile = await Write("baseline.json", JsonSerializer.Serialize(baseline, EvalJson.Options));
        var candidateFile = await Write("candidate.json", JsonSerializer.Serialize(candidate, EvalJson.Options));
        var target = Path.Combine(directory, "comparison.json");
        Assert.Equal(2, await EvalCli.RunAsync(["compare", "--baseline", baselineFile, "--candidate", candidateFile, "--out", target]));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ComparisonRecordsActualFieldAndCriticalFailureDeltas()
    {
        var cases = await Cases(Case("first"));
        var outputs = await Write("outputs.jsonl", "");
        var baseline = await Score(cases, outputs);
        var candidate = baseline with
        {
            Version = "candidate", CriticalFailures = baseline.CriticalFailures - 1,
            Checks = new Dictionary<string, FieldScore>(baseline.Checks) { ["validator.valid"] = new(1, 1) }
        };
        var baselineFile = await Write("baseline.json", JsonSerializer.Serialize(baseline, EvalJson.Options));
        var candidateFile = await Write("candidate.json", JsonSerializer.Serialize(candidate, EvalJson.Options));
        var target = Path.Combine(directory, "comparison.json");
        Assert.Equal(0, await EvalCli.RunAsync(["compare", "--baseline", baselineFile, "--candidate", candidateFile, "--out", target]));
        using var comparison = JsonDocument.Parse(await File.ReadAllTextAsync(target));
        Assert.Equal(-1, comparison.RootElement.GetProperty("critical_failure_delta").GetInt32());
        var validator = comparison.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("check").GetString() == "validator.valid");
        Assert.Equal(1, validator.GetProperty("delta_passes").GetInt32());
    }

    private async Task<EvalReport> Score(string cases, string outputs)
    {
        var report = Path.Combine(directory, "report.json");
        Assert.Equal(0, await EvalCli.RunAsync(["score", cases, outputs, "--split", "train", "--version", "test", "--out", report]));
        return EvalJson.Read<EvalReport>(await File.ReadAllTextAsync(report));
    }

    private Task<string> Cases(params EvalCase[] cases) => Write("cases.jsonl", string.Join('\n', cases.Select(c => JsonSerializer.Serialize(c, EvalJson.Options))));

    private async Task<string> Write(string name, string contents)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, contents);
        return path;
    }

    private static EvalCase Case(string id) => new()
    {
        CaseId = id, Split = "train", PageCount = 0,
        Document = new() { Id = 1, Title = "Acme synthetic receipt", Content = "Acme synthetic receipt" },
        Taxonomy = new(),
        Expected = new()
        {
            Title = new() { Action = "ignore", Value = "curator-only reference" }, Date = new() { Action = "keep" },
            Correspondent = new() { Action = "keep" }, DocumentType = new() { Action = "keep" }, Critical = true
        }
    };

    private const string Keep = """
        {"schema_version":"1","title":{"action":"keep","value":null,"evidence":[]},"date":{"action":"keep","value":null,"evidence":[]},"correspondent":{"action":"keep","value":null,"evidence":[]},"document_type":{"action":"keep","value":null,"evidence":[]},"add_tags":[],"ocr":{"action":"keep","pages":[],"evidence":[]},"uncertainty":[]}
        """;

    public void Dispose() => Directory.Delete(directory, true);
}
