using PaperlessLlm.Eval;
using System.Text.Json;

namespace PaperlessLlm.Tests;

public sealed class ExperimentRunnerTests
{
    private static ExperimentRecipe Recipe(string pipeline = "single") => new()
    {
        Id = "synthetic", PromptFile = "prompt.txt", Pipeline = pipeline
    };

    [Theory]
    [InlineData("single")]
    [InlineData("ocr-first")]
    [InlineData("pagewise")]
    [InlineData("refine")]
    [InlineData("ledger")]
    [InlineData("dual")]
    public void AcceptsSupportedPipelines(string pipeline) => ExperimentRunner.ValidateRecipe(Recipe(pipeline));

    [Fact]
    public void RejectsUnknownPipelineAndReasoning()
    {
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateRecipe(Recipe("agent-loop")));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateRecipe(Recipe() with { Reasoning = "xhigh" }));
    }

    [Fact]
    public void FinalAssemblyLabelsEarlierStageAsUntrustedAndPreservesInstructionOrder()
    {
        var text = ExperimentRunner.AssembleFinal("Follow policy.", "{\"id\":1}", "instructions-first", ["remove the inbox tag"]);
        Assert.StartsWith("Follow policy.", text);
        Assert.Contains("Untrusted prior-stage draft 1", text);
        Assert.Contains("remove the inbox tag", text);
        Assert.Contains("exact schema", text);
    }

    [Fact]
    public void ExtractsOnlyCompletedAgentMessageAndRejectsToolEvents()
    {
        const string good = "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{}\"}}";
        Assert.Equal("{}", ExperimentRunner.ExtractFinalMessage(good));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ExtractFinalMessage("{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\"}}\n" + good));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ExtractFinalMessage("{\"type\":\"turn.completed\"}"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ExperimentRunner.ExtractFinalMessage("not-json"));
    }

    [Fact]
    public void CandidatePayloadCanRemoveOcrAndShortlistByExistingIdsWithoutLeakingLabels()
    {
        var c = SyntheticCase();
        var payload = ExperimentRunner.BuildCandidatePayload(c, Recipe() with { OcrContext = "none", Taxonomy = "shortlist" });
        using var parsed = JsonDocument.Parse(payload);
        var root = parsed.RootElement;
        Assert.Equal("", root.GetProperty("document").GetProperty("content").GetString());
        Assert.Contains(root.GetProperty("allowed_taxonomy").GetProperty("correspondents").EnumerateArray(), x => x.GetProperty("id").GetInt32() == 11);
        Assert.DoesNotContain("secret expected label", payload);
        Assert.DoesNotContain("curator-only note", payload);
    }

    [Fact]
    public void ScoringKeepsMissingAndInvalidOutputsVisible()
    {
        var c = SyntheticCase();
        var missing = EvalEngine.Score("v", "train", null, null, [c], new Dictionary<string, string>());
        Assert.Equal(1, missing.MissingOutputs);
        var invalid = EvalEngine.Score("v", "train", null, null, [c], new Dictionary<string, string> { [c.CaseId] = "not-json" });
        Assert.Equal(1, invalid.ValidatorFailures);
        Assert.Equal("invalid_intent_json", invalid.Cases[0].Failure);
    }

    private static EvalCase SyntheticCase() => new()
    {
        CaseId = "synthetic-01", Split = "train", PageCount = 0,
        Document = new EvalDocument { Id = 1, Title = "Acme receipt", Content = "private OCR", CorrespondentId = 11, Tags = [7] },
        Taxonomy = new EvalTaxonomy { Tags = [new EvalNamedEntity(7, "inbox", true)],
            Correspondents = [new EvalNamedEntity(11, "Acme"), new EvalNamedEntity(12, "Unrelated")],
            DocumentTypes = [new EvalNamedEntity(21, "Receipt"), new EvalNamedEntity(22, "Statement")] },
        Expected = new EvalExpected { Title = new ExpectedField { Action = "ignore", Value = "secret expected label" },
            Date = new ExpectedField { Action = "ignore" }, Correspondent = new ExpectedField { Action = "ignore" },
            DocumentType = new ExpectedField { Action = "ignore" } }, Notes = "curator-only note"
    };
}
