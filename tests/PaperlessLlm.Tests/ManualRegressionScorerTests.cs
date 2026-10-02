using PaperlessLlm.Eval;

namespace PaperlessLlm.Tests;

public sealed class ManualRegressionScorerTests
{
    [Fact]
    public void ReadsReferenceContractWithoutReportFieldsForPrivateTextOrNotes()
    {
        const string json = "{\"synthetic\":{\"origin\":\"private ref\",\"acceptanceRule\":\"private rule\",\"checks\":[{\"page\":1,\"text\":\"PRIVATE TEXT\",\"kind\":\"text\",\"note\":\"PRIVATE NOTE\"}]}}";
        var references = EvalJson.Read<Dictionary<string, ManualRegressionReference>>(json);
        Assert.Single(references["synthetic"].Checks);
        Assert.Equal("text", references["synthetic"].Checks[0].Kind);
    }

    [Fact]
    public void CheckMustAppearOnItsSpecifiedPage()
    {
        var c = Case();
        var result = Score(c, Intent((1, "TARGET LINE"), (2, "")), [new(2, "TARGET LINE", "text")]);
        Assert.Equal("failed-set", result.Cases[0].Outcome);
        Assert.Equal(new[] { 0 }, result.Cases[0].MissingCheckIndices);
        var json = System.Text.Json.JsonSerializer.Serialize(result, EvalJson.Options);
        Assert.DoesNotContain("TARGET LINE", json);
    }

    [Fact]
    public void RepeatedIdenticalChecksConsumeDistinctOccurrences()
    {
        var c = Case();
        var result = Score(c, Intent((1, ""), (2, "BLOCK-9")), [new(2, "BLOCK-9", "identifier"), new(2, "BLOCK-9", "identifier")]);
        Assert.Equal(1, result.Cases[0].Matched);
        Assert.Equal(new[] { 1 }, result.Cases[0].MissingCheckIndices);
        Assert.Equal("failed-set", result.Cases[0].Outcome);
    }

    [Fact]
    public void IdentifierBoundaryRejectsLongerIdentifierWhileTextAllowsInteriorMatch()
    {
        var c = Case();
        var result = Score(c, Intent((1, ""), (2, "INV-420")), [new(2, "INV-42", "identifier"), new(2, "INV-42", "text")]);
        Assert.Equal("failed-set", result.Cases[0].Outcome);
        Assert.Equal(1, result.Cases[0].Matched);
        Assert.Equal(new[] { 0 }, result.Cases[0].MissingCheckIndices);
    }

    [Fact]
    public void KeepIsReportedAsSafeAbstentionNotSetPass()
    {
        var c = Case();
        var result = Score(c, Intent(keep: true), [new(2, "TARGET", "text")]);
        Assert.Equal(1, result.SafeAbstentions);
        Assert.Equal(0, result.PassedSetCases);
        Assert.Equal("safe-abstention", result.Cases[0].Outcome);
        Assert.Equal(0, result.Cases[0].Total);
    }

    [Fact]
    public void InvalidIntentIsSeparateFromFailedSetAndAbstention()
    {
        var c = Case();
        var result = ManualRegressionScorer.Score("train", [c], new Dictionary<string, string> { [c.CaseId] = "not json" },
            new HashSet<string>(), new Dictionary<string, ManualRegressionReference> { [c.CaseId] = Ref(new ManualRegressionCheck(2, "TARGET", "text")) });
        Assert.Equal(1, result.InvalidCases);
        Assert.Equal("invalid", result.Cases[0].Outcome);
        Assert.Empty(result.Cases[0].MissingCheckIndices);
    }

    [Fact]
    public void TextPatternAllowsFlexibleLayoutWhitespaceAndOptionalColonSpace()
    {
        var c = Case();
        var result = Score(c, Intent((1, ""), (2, "TOTAL:   $ 12.00")), [new(2, "TOTAL:$ 12.00", "text")]);
        Assert.Equal("passed-set", result.Cases[0].Outcome);
    }

    private static ManualRegressionReport Score(EvalCase c, string intent, IReadOnlyList<ManualRegressionCheck> checks) =>
        ManualRegressionScorer.Score("train", [c], new Dictionary<string, string> { [c.CaseId] = intent },
            new HashSet<string> { c.CaseId }, new Dictionary<string, ManualRegressionReference> { [c.CaseId] = Ref(checks.ToArray()) });

    private static ManualRegressionReference Ref(params ManualRegressionCheck[] checks) => new() { Checks = checks.ToList() };

    private static EvalCase Case() => new()
    {
        CaseId = "synthetic-regression", Split = "train", PageCount = 2,
        Document = new EvalDocument { Id = 1, Title = "Synthetic", Content = "" },
        Taxonomy = new EvalTaxonomy(),
        Expected = new EvalExpected
        {
            Title = new ExpectedField { Action = "ignore" }, Date = new ExpectedField { Action = "ignore" },
            Correspondent = new ExpectedField { Action = "ignore" }, DocumentType = new ExpectedField { Action = "ignore" }
        }
    };

    private static string Intent((int Page, string Text)? first = null, (int Page, string Text)? second = null, bool keep = false)
    {
        var pages = keep ? "[]" : $$"""[{"page":{{first?.Page ?? 1}},"text":{{System.Text.Json.JsonSerializer.Serialize(first?.Text ?? "")}},"complete":true,"uncertainty":[]},{"page":{{second?.Page ?? 2}},"text":{{System.Text.Json.JsonSerializer.Serialize(second?.Text ?? "")}},"complete":true,"uncertainty":[]}]""";
        return $$"""
        {"schema_version":"1","title":{"action":"keep","value":null,"evidence":[]},"date":{"action":"keep","value":null,"evidence":[]},"correspondent":{"action":"keep","value":null,"evidence":[]},"document_type":{"action":"keep","value":null,"evidence":[]},"add_tags":[],"ocr":{"action":"{{(keep ? "keep" : "set")}}","pages":{{pages}},"evidence":[]},"uncertainty":[]}
        """;
    }
}
