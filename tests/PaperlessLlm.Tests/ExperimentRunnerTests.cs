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
    public void PerPageImageMappingsSupportSevenOrderedRegionsAndAnchors()
    {
        var c = CaseWithPages();
        var variants = RegionVariants(7);
        var map = new Dictionary<string, ExperimentRunner.ImageVariantCase> { [c.CaseId] = variants };
        var regionRecipe = Recipe("pagewise") with { ImageMode = "regions" };
        var regions = ExperimentRunner.SelectImages(c, regionRecipe, map, Path.GetTempPath());
        Assert.Equal(14, regions.Count);
        Assert.Equal("p1-r1.png", Path.GetFileName(regions[0]));
        Assert.Equal("p1-r7.png", Path.GetFileName(regions[6]));
        Assert.Equal("p2-r1.png", Path.GetFileName(regions[7]));
        var anchors = ExperimentRunner.BuildImageAnchors(c, regionRecipe, regions);
        Assert.Contains("Attached image 8: region 1 of page 2.", anchors);
        Assert.Contains("Attached image 14: region 7 of page 2.", anchors);

        var pageGroups = ExperimentRunner.SelectPageImages(c, regionRecipe, map, Path.GetTempPath());
        Assert.Equal(7, pageGroups[0].Count);
        Assert.Equal("p2-r7.png", Path.GetFileName(pageGroups[1][6]));
    }

    [Fact]
    public void FullAndRegionsMappingUsesAllPagesThenVariableRegionGroups()
    {
        var c = CaseWithPages();
        var variants = RegionVariants(7);
        var recipe = Recipe() with { ImageMode = "full-and-regions" };
        var images = ExperimentRunner.SelectImages(c, recipe,
            new Dictionary<string, ExperimentRunner.ImageVariantCase> { [c.CaseId] = variants }, Path.GetTempPath());
        Assert.Equal(16, images.Count);
        Assert.Equal(new[] { "p1-full.png", "p2-full.png", "p1-r1.png", "p1-r2.png", "p1-r3.png", "p1-r4.png", "p1-r5.png", "p1-r6.png", "p1-r7.png" },
            images.Take(9).Select(Path.GetFileName));
        var anchors = ExperimentRunner.BuildImageAnchors(c, recipe, images);
        Assert.Contains("Attached image 2: full page 2.", anchors);
        Assert.Contains("Attached image 3: region 1 of page 1.", anchors);
        Assert.Contains("Attached image 16: region 7 of page 2.", anchors);
    }

    [Fact]
    public void VariableRegionMapRejectsPerPageCountAndDuplicateMappingDrift()
    {
        var c = CaseWithPages();
        var inconsistent = RegionVariants(7) with { Pages = [RegionPage(1, 7), RegionPage(2, 6)] };
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.SelectImages(c, Recipe() with { ImageMode = "regions" },
            new Dictionary<string, ExperimentRunner.ImageVariantCase> { [c.CaseId] = inconsistent }, Path.GetTempPath()));
        var drifted = RegionVariants(7) with { Regions = ["other.png", .. RegionVariants(7).Regions.Skip(1)] };
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.SelectImages(c, Recipe() with { ImageMode = "regions" },
            new Dictionary<string, ExperimentRunner.ImageVariantCase> { [c.CaseId] = drifted }, Path.GetTempPath()));
    }

    [Fact]
    public void VariableRegionCountRequiresPageGroupingWhileLegacyThreeRegionFlatMapStillWorks()
    {
        var c = CaseWithPages();
        var variableFlat = RegionVariants(7) with { Pages = [] };
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.SelectImages(c, Recipe() with { ImageMode = "regions" },
            new Dictionary<string, ExperimentRunner.ImageVariantCase> { [c.CaseId] = variableFlat }, Path.GetTempPath()));

        var legacy = RegionVariants(3) with { Pages = [] };
        var selected = ExperimentRunner.SelectImages(c, Recipe() with { ImageMode = "regions" },
            new Dictionary<string, ExperimentRunner.ImageVariantCase> { [c.CaseId] = legacy }, Path.GetTempPath());
        Assert.Equal(6, selected.Count);
    }

    [Fact]
    public void HighImageMappingsRequireAndCrossCheckPerPageVariants()
    {
        var c = CaseWithPages();
        var mappedPages = new[]
        {
            RegionPage(1, 3) with { High = "p1-high.png" },
            RegionPage(2, 3) with { High = "p2-high.png" }
        };
        var variants = RegionVariants(3) with
        {
            High = ["p1-high.png", "p2-high.png"], Pages = mappedPages.ToList()
        };
        var map = new Dictionary<string, ExperimentRunner.ImageVariantCase> { [c.CaseId] = variants };
        var recipe = Recipe("pagewise") with { ImageMode = "high" };

        var selected = ExperimentRunner.SelectImages(c, recipe, map, Path.GetTempPath());
        Assert.Equal(new[] { "p1-high.png", "p2-high.png" }, selected.Select(Path.GetFileName));
        var pageGroups = ExperimentRunner.SelectPageImages(c, recipe, map, Path.GetTempPath());
        Assert.Equal("p2-high.png", Path.GetFileName(pageGroups[1][0]));

        var globalDrift = variants with { High = ["wrong.png", "p2-high.png"] };
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.SelectImages(c, recipe,
            new Dictionary<string, ExperimentRunner.ImageVariantCase> { [c.CaseId] = globalDrift }, Path.GetTempPath()));
        var missingPerPage = variants with { Pages = [mappedPages[0], mappedPages[1] with { High = null }] };
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.SelectImages(c, recipe,
            new Dictionary<string, ExperimentRunner.ImageVariantCase> { [c.CaseId] = missingPerPage }, Path.GetTempPath()));
    }

    [Fact]
    public void OutputCaseIdsAreValidatedBeforeInferenceAndOnlyForSelectedSplit()
    {
        var train = SyntheticCase();
        var unsafeHoldout = train with { CaseId = "../holdout", Split = "holdout" };
        var selectedTrain = ExperimentRunner.SelectCasesForSplit([train, unsafeHoldout], "train");
        ExperimentRunner.ValidateOutputCaseIds(selectedTrain);

        var unsafeTrain = train with { CaseId = "bad:name" };
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateOutputCaseIds([unsafeTrain]));
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ValidateOutputCaseIds([train with { CaseId = "NUL" }]));
    }

    private static EvalCase CaseWithPages() => SyntheticCase() with
    {
        CaseId = "synthetic-images", PageCount = 2,
        Document = new EvalDocument { Id = 1, Title = "Synthetic", Content = "", PageImages = ["source-1.png", "source-2.png"] }
    };

    private static ExperimentRunner.ImageVariantCase RegionVariants(int perPage)
    {
        var pages = Enumerable.Range(1, 2).Select(page => RegionPage(page, perPage)).ToList();
        var full = pages.Select(p => p.Full).ToList();
        var regions = pages.SelectMany(p => p.Regions).ToList();
        return new ExperimentRunner.ImageVariantCase
        {
            Full = full, Regions = regions, FullAndRegions = full.Concat(regions).ToList(), Pages = pages
        };
    }

    private static ExperimentRunner.ImageVariantPage RegionPage(int page, int count) => new()
    {
        Page = page, Full = $"p{page}-full.png", Regions = Enumerable.Range(1, count).Select(region => $"p{page}-r{region}.png").ToList()
    };

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

    [Fact]
    public async Task StageArtifactsPersistEvenWhenCallerTokenIsCancelled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ppllm-cancel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await ExperimentRunner.PersistStageArtifactsAsync(directory, "final", "bounded stdout", "bounded stderr", "{\"cancelled\":true}", cancellation.Token);
            Assert.Equal("bounded stdout", File.ReadAllText(Path.Combine(directory, "final.stdout.jsonl")));
            Assert.Equal("bounded stderr", File.ReadAllText(Path.Combine(directory, "final.stderr.log")));
            Assert.Contains("cancelled", File.ReadAllText(Path.Combine(directory, "final.provenance.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StopProcessTreeTerminatesStartedChildWithoutNetworkOrCodex()
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add("Start-Sleep -Seconds 60");
        }
        else { start.ArgumentList.Add("-c"); start.ArgumentList.Add("sleep 60"); }
        using var process = Process.Start(start)!;
        await ExperimentRunner.StopProcessTreeAsync(process);
        Assert.True(process.HasExited);
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
