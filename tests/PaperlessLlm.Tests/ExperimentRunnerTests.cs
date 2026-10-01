using PaperlessLlm.Eval;
using System.Diagnostics;
using System.Text;
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
    [InlineData("pagewise-compose")]
    [InlineData("refine")]
    [InlineData("ledger")]
    [InlineData("dual")]
    public void AcceptsSupportedPipelines(string pipeline) => ExperimentRunner.ValidateRecipe(Recipe(pipeline));

    [Fact]
    public void RejectsUnknownPipelineAndReasoning()
    {
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateRecipe(Recipe("agent-loop")));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateRecipe(Recipe() with { Reasoning = "xhigh" }));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateRecipe(Recipe() with { AuxiliaryContext = "metadata-only" }));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateRecipe(Recipe() with { DraftContext = "first" }));
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
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ExtractFinalMessage("{\"type\":\"web_search_call\"}\n" + good));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ExtractFinalMessage("{\"type\":\"item.started\",\"item\":{\"type\":\"browser_fetch_call\"}}\n" + good));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ExtractFinalMessage("{\"type\":\"turn.completed\"}"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ExperimentRunner.ExtractFinalMessage("not-json"));
    }

    [Fact]
    public void CodexTransportUsesUtf8WithoutBomForUnicodeDrafts()
    {
        var psi = ExperimentRunner.CreateCodexStartInfo("gpt-6-luna", "medium", Path.GetTempPath(), []);
        Assert.Equal(Encoding.UTF8.CodePage, psi.StandardInputEncoding!.CodePage);
        Assert.Empty(psi.StandardInputEncoding.GetPreamble());
        Assert.Equal(Encoding.UTF8.CodePage, psi.StandardOutputEncoding!.CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, psi.StandardErrorEncoding!.CodePage);
    }

    [Fact]
    public void ImagesOnlyAuxiliaryStageOmitsDocumentPayloadAndLatestDraftKeepsOnlySynthesis()
    {
        const string privatePayload = "private OCR and taxonomy label";
        const string anchors = "Attached image 1: full page 1.";
        var context = ExperimentRunner.BuildAuxiliaryContext("images-only", privatePayload, anchors);
        Assert.DoesNotContain(privatePayload, context);
        Assert.Contains(anchors, context);
        Assert.Equal(new[] { "adjudicated summary" }, ExperimentRunner.SelectDraftContext(["draft A", "draft B", "adjudicated summary"], "latest"));
        Assert.Equal(3, ExperimentRunner.SelectDraftContext(["draft A", "draft B", "adjudicated summary"], "all").Count);
    }

    [Fact]
    public void PagewiseCompositionUsesValidatedOrderedSourceRecordsAndOverridesFinalModelOcr()
    {
        var page1 = ExperimentRunner.ParsePageTranscription("{\"page\":1,\"text\":\"First-page text\",\"complete\":true,\"uncertainty\":[]}", 1);
        var page2 = ExperimentRunner.ParsePageTranscription("{\"page\":2,\"text\":\"Second-page footer\",\"complete\":true,\"uncertainty\":[]}", 2);
        var composed = ExperimentRunner.ApplyPagewiseComposition("{\"ocr\":{\"action\":\"set\",\"pages\":[{\"page\":99,\"text\":\"rewritten by final model\",\"complete\":true,\"uncertainty\":[]}],\"evidence\":[]},\"uncertainty\":[\"metadata date unclear\"]}", [page1, page2]);
        using var doc = JsonDocument.Parse(composed);
        var ocr = doc.RootElement.GetProperty("ocr");
        Assert.Equal("set", ocr.GetProperty("action").GetString());
        Assert.Contains("source-page transcriptions", ocr.GetProperty("evidence")[0].GetString());
        Assert.Equal(1, ocr.GetProperty("pages")[0].GetProperty("page").GetInt32());
        Assert.Equal("First-page text", ocr.GetProperty("pages")[0].GetProperty("text").GetString());
        Assert.Equal(2, ocr.GetProperty("pages")[1].GetProperty("page").GetInt32());
        Assert.Equal("Second-page footer", ocr.GetProperty("pages")[1].GetProperty("text").GetString());
        Assert.Contains("metadata date unclear", doc.RootElement.GetProperty("uncertainty").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void PagewiseCompositionKeepsOcrWhenAnyPageIsIncompleteOrUncertain()
    {
        var page1 = ExperimentRunner.ParsePageTranscription("{\"page\":1,\"text\":\"part\",\"complete\":true,\"uncertainty\":[]}", 1);
        var page2 = ExperimentRunner.ParsePageTranscription("{\"page\":2,\"text\":\"partial\",\"complete\":false,\"uncertainty\":[\"footer clipped\"]}", 2);
        var composed = ExperimentRunner.ApplyPagewiseComposition("{\"ocr\":{},\"uncertainty\":[\"metadata unclear\"]}", [page1, page2]);
        using var doc = JsonDocument.Parse(composed);
        Assert.Equal("keep", doc.RootElement.GetProperty("ocr").GetProperty("action").GetString());
        Assert.Empty(doc.RootElement.GetProperty("ocr").GetProperty("pages").EnumerateArray());
        Assert.Empty(doc.RootElement.GetProperty("ocr").GetProperty("evidence").EnumerateArray());
        Assert.Contains("metadata unclear", doc.RootElement.GetProperty("uncertainty").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("Page 2: footer clipped", doc.RootElement.GetProperty("uncertainty").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("Page 2: source transcription marked incomplete.", doc.RootElement.GetProperty("uncertainty").EnumerateArray().Select(x => x.GetString()));
    }

    [Theory]
    [InlineData("{\"page\":2,\"text\":\"x\",\"complete\":true,\"uncertainty\":[]}")]
    [InlineData("{\"page\":1,\"text\":\"x\",\"complete\":\"yes\",\"uncertainty\":[]}")]
    [InlineData("{\"page\":1,\"text\":\"x\",\"complete\":true,\"uncertainty\":[],\"extra\":1}")]
    public void PagewiseTranscriptionRejectsMismatchedOrInvalidSchema(string json) =>
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ParsePageTranscription(json, 1));

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

    [Fact]
    public void ExperimentSplitSelectionExcludesHoldoutBeforeRunnerStarts()
    {
        var train = SyntheticCase();
        var holdout = train with { CaseId = "synthetic-02", Split = "holdout" };
        var selected = ExperimentRunner.SelectCasesForSplit([train, holdout], "train");
        Assert.Single(selected);
        Assert.Equal("synthetic-01", selected[0].CaseId);
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.SelectCasesForSplit([train], "holdout"));
        Assert.Throws<ArgumentException>(() => ExperimentRunner.SelectCasesForSplit([train], "all"));
    }

    [Fact]
    public void ExperimentRefusesNonemptyOutputDirectoryInsteadOfResumingStaleResults()
    {
        var output = Path.Combine(Path.GetTempPath(), "ppllm-experiment-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExperimentRunner.EnsureFreshOutputDirectory(output);
            File.WriteAllText(Path.Combine(output, "old-case.json"), "{}");
            Assert.Throws<InvalidDataException>(() => ExperimentRunner.EnsureFreshOutputDirectory(output));
            Assert.True(File.Exists(Path.Combine(output, "old-case.json")));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
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
