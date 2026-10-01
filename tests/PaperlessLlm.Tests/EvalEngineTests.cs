using PaperlessLlm.Eval;

namespace PaperlessLlm.Tests;

public sealed class EvalEngineTests
{
    [Fact]
    public void GradesTitlePreservationSeparatelyFromUnnecessaryRewriteAndChecksResultingOcr()
    {
        var testCase = Case(critical: true, mustReplace: true) with
        {
            Document = new EvalDocument { Id = 1, Title = "Acme receipt", Content = "Old OCR", Created = "2026-05-02" },
            Expected = Case(critical: true, mustReplace: true).Expected with
            {
                Date = new ExpectedField { Action = "set", Value = "2026-05-02" }
            }
        };
        var report = EvalEngine.Score("candidate", "train", "test-model", "test-harness",
            [testCase], new Dictionary<string, string> { [testCase.CaseId] = ValidIntent(titleAction: "set", titleValue: "Acme receipt", ocrAction: "set", ocrText: "Invoice INV-1042 Amount $30.00") });

        Assert.Equal("test-model", report.Model);
        Assert.True(report.Cases[0].Checks["title.correct"]);
        Assert.False(report.Cases[0].Checks["title.no_unnecessary_write"]);
        Assert.True(report.Cases[0].Checks["date.correct"]);
        Assert.True(report.Cases[0].Checks["date.no_unnecessary_write"]);
        Assert.True(report.Cases[0].Checks["ocr.must_replace"]);
        Assert.True(report.Cases[0].Checks["ocr.key_facts"]);
        Assert.Equal(2, report.OcrKeyFactsMatched);
        Assert.Equal(2, report.OcrKeyFactsTotal);
        Assert.Equal(0, report.CriticalFailures);
    }

    [Fact]
    public void ProductionValidatorRejectsProtectedTagAdditionsAndCountsCriticalFailure()
    {
        var testCase = Case(critical: true, mustReplace: false) with
        {
            Taxonomy = new EvalTaxonomy { Tags = [new EvalNamedEntity(9, "inbox", true)] }
        };
        var intent = ValidIntent(addTag: 9);
        var report = EvalEngine.Score("bad", "train", null, null, [testCase], new Dictionary<string, string> { [testCase.CaseId] = intent });

        Assert.False(report.Cases[0].Valid);
        Assert.True(report.Cases[0].Checks["output.present"]);
        Assert.Equal("protected_tag", report.Cases[0].Failure);
        Assert.False(report.Cases[0].Checks["protected_tags.not_added"]);
        Assert.Equal(1, report.ValidatorFailures);
        Assert.Equal(1, report.CriticalFailures);
    }

    [Fact]
    public void HoldoutScoringRequiresAnExplicitMatchingSplitAndFiltersTrainingCases()
    {
        var train = Case(critical: false, mustReplace: false);
        var holdout = train with { CaseId = "holdout-1", Split = "holdout" };

        var report = EvalEngine.Score("baseline", "train", null, null, [train, holdout],
            new Dictionary<string, string> { [train.CaseId] = ValidIntent() });

        Assert.Equal(1, report.TotalCases);
        Assert.Equal("train", report.Split);
    }

    private static EvalCase Case(bool critical, bool mustReplace) => new()
    {
        CaseId = "synthetic-1", Split = "train", PageCount = 1,
        Document = new EvalDocument { Id = 1, Title = "Acme receipt", Content = "Old OCR", PageImages = ["private\\page1.png"] },
        Taxonomy = new EvalTaxonomy(),
        Expected = new EvalExpected
        {
            Title = new ExpectedField { Action = "preserve" },
            Date = new ExpectedField { Action = "ignore" },
            Correspondent = new ExpectedField { Action = "ignore" },
            DocumentType = new ExpectedField { Action = "ignore" },
            Ocr = new EvalOcrExpected { MustReplace = mustReplace, KeyFacts = ["INV-1042", "$30.00"] },
            Critical = critical
        }
    };

    private static string ValidIntent(string titleAction = "keep", string? titleValue = null,
        string ocrAction = "keep", string? ocrText = null, int? addTag = null)
    {
        var tag = addTag is null ? "[]" : $"[{{\"id\":{addTag},\"evidence\":[\"Page 1 visible label\"]}}]";
        var pages = ocrAction == "set" ? $"[{{\"page\":1,\"text\":{System.Text.Json.JsonSerializer.Serialize(ocrText)},\"complete\":true,\"uncertainty\":[]}}]" : "[]";
        return $$"""
            {"schema_version":"1","title":{"action":"{{titleAction}}","value":{{System.Text.Json.JsonSerializer.Serialize(titleValue)}},"evidence":{{(titleAction == "set" ? "[\"Page 1 issuer and purpose\"]" : "[]")}}},"date":{"action":"keep","value":null,"evidence":[]},"correspondent":{"action":"keep","value":null,"evidence":[]},"document_type":{"action":"keep","value":null,"evidence":[]},"add_tags":{{tag}},"ocr":{"action":"{{ocrAction}}","pages":{{pages}},"evidence":{{(ocrAction == "set" ? "[\"Page 1 transcription\"]" : "[]")}}},"uncertainty":[]}
            """;
    }
}
