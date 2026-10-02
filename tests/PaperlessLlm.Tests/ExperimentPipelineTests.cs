using System.Text.Json;
using PaperlessLlm.Eval;


namespace PaperlessLlm.Tests;

public sealed class ExperimentPipelineTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-pipeline-" + Guid.NewGuid().ToString("N"));
    public ExperimentPipelineTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("single", "final")]
    [InlineData("ocr-first", "ocr,final")]
    [InlineData("ledger", "ledger,final")]
    [InlineData("pagewise", "page-01,page-02,final")]
    [InlineData("pagewise-compose", "page-01,page-02,final")]
    [InlineData("refine", "draft,review,final")]
    [InlineData("dual", "draft-a,draft-b,adjudicate,final")]
    public async Task PipelinesPreserveStageOrderEvidenceIsolationAndRawArtifacts(string pipeline, string stageNames)
    {
        var calls = new List<(string Name, string Prompt, string[] Images)>();
        var c = Case();
        var recipe = Recipe(pipeline);
        Task<string> Invoke(string prompt, IReadOnlyList<string> images, string stage, CancellationToken ct)
        {
            calls.Add((stage, prompt, images.ToArray()));
            if (stage == "final") return Task.FromResult(Keep);
            if (pipeline == "pagewise-compose")
                return Task.FromResult(JsonSerializer.Serialize(new { page = calls.Count, text = "Synthetic page " + calls.Count, complete = true, uncertainty = Array.Empty<string>() }));
            return Task.FromResult("Synthetic evidence from " + stage);
        }
        await ExperimentRunner.RunCaseAsync(c, recipe, null, directory, "Final policy.", "Auxiliary policy.",
            Options(), directory, CancellationToken.None, Invoke);
        Assert.Equal(stageNames.Split(','), calls.Select(call => call.Name));
        var final = calls[^1];
        Assert.Contains("Final policy.", final.Prompt);
        Assert.Contains("PRODUCTION INTENT INPUT JSON", final.Prompt);
        var rawFinal = Assert.Single(Directory.GetFiles(directory, "final.txt", SearchOption.AllDirectories));
        Assert.Equal(Keep, await File.ReadAllTextAsync(rawFinal));
        Assert.Equal(calls.Count, Directory.GetFiles(Path.GetDirectoryName(rawFinal)!, "*.txt").Length);
        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, c.CaseId + ".json")));
        if (pipeline == "pagewise-compose")
        {
            var pages = output.RootElement.GetProperty("ocr").GetProperty("pages");
            Assert.Equal(2, pages.GetArrayLength());
            Assert.Equal("Synthetic page 2", pages[1].GetProperty("text").GetString());
            Assert.DoesNotContain("source OCR", calls[0].Prompt);
            Assert.DoesNotContain("Synthetic page 1", calls[1].Prompt);
        }
        else Assert.Equal("keep", output.RootElement.GetProperty("ocr").GetProperty("action").GetString());
    }

    [Fact]
    public async Task ImagesOnlyPageStageAndTextOnlyFinalCannotAccidentallyExposeOtherEvidence()
    {
        var calls = new List<(string Prompt, int Images)>();
        Task<string> Invoke(string prompt, IReadOnlyList<string> images, string stage, CancellationToken ct)
        {
            calls.Add((prompt, images.Count));
            return Task.FromResult(stage == "final" ? Keep : "stage evidence");
        }
        var recipe = Recipe("pagewise") with { AuxiliaryContext = "images-only", IncludeFinalImages = false };
        await ExperimentRunner.RunCaseAsync(Case(), recipe, null, directory, "Final policy.", "Aux policy.",
            Options(), directory, CancellationToken.None, Invoke);
        Assert.Equal(1, calls[0].Images);
        Assert.Equal(1, calls[1].Images);
        Assert.DoesNotContain("source OCR", calls[0].Prompt);
        Assert.DoesNotContain("Acme", calls[0].Prompt);
        Assert.Equal(0, calls[2].Images);
        Assert.Contains("No images are attached to this final call", calls[2].Prompt);
        Assert.Contains("Untrusted prior-stage draft 1", calls[2].Prompt);
    }

    [Fact]
    public async Task NamedFinalRetainsUnmodifiedResponseButPublishesOnlyValidatedIds()
    {
        var raw = Keep.Replace("\"correspondent\":{\"action\":\"keep\",\"value\":null,\"evidence\":[]}",
            "\"correspondent\":{\"action\":\"set\",\"value\":\"Acme\",\"evidence\":[\"Acme printed issuer\"]}");
        string? seenPrompt = null;
        Task<string> Invoke(string prompt, IReadOnlyList<string> images, string stage, CancellationToken ct)
        {
            seenPrompt = prompt;
            return Task.FromResult(raw);
        }
        await ExperimentRunner.RunCaseAsync(Case(), Recipe("single") with { OutputContract = "names" }, null,
            directory, "Final policy.", "", Options(), directory, CancellationToken.None, Invoke);
        Assert.Contains("DOCUMENT INPUT JSON", seenPrompt);
        Assert.Equal(raw, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(directory, "final.txt", SearchOption.AllDirectories))));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "synthetic.json")));
        Assert.Equal(11, doc.RootElement.GetProperty("correspondent").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task InvalidNamedFinalRetainsEvidenceWithoutPublishingScoreableOutput()
    {
        const string malformed = "{\"unexpected\":\"model response\"}";
        await Assert.ThrowsAsync<InvalidDataException>(() => ExperimentRunner.RunCaseAsync(Case(),
            Recipe("single") with { OutputContract = "names" }, null, directory, "Final policy.", "", Options(),
            directory, CancellationToken.None, (_, _, _, _) => Task.FromResult(malformed)));
        Assert.Equal(malformed, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(directory, "final.txt", SearchOption.AllDirectories))));
        Assert.False(File.Exists(Path.Combine(directory, "synthetic.json")));
    }

    [Fact]
    public void LaterEmptyFinalMessageCannotReuseEarlierDraftAsFinalAnswer()
    {
        const string events = """
            {"type":"item.completed","item":{"type":"agent_message","text":"draft"}}
            {"type":"item.completed","item":{"type":"agent_message","text":null}}
            """;
        Assert.Throws<InvalidDataException>(() => ExperimentRunner.ExtractFinalMessage(events));
    }

    private ExperimentOptions Options() => new("unused.jsonl", "unused-recipe.json", directory, 1);
    private static ExperimentRecipe Recipe(string pipeline) => new() { Id = "synthetic", PromptFile = "unused.txt", Pipeline = pipeline };
    private static EvalCase Case() => new()
    {
        CaseId = "synthetic", Split = "train", PageCount = 2,
        Document = new() { Id = 1, Title = "Acme", Content = "source OCR", PageImages = ["page1.png", "page2.png"] },
        Taxonomy = new() { Correspondents = [new(11, "Acme")] },
        Expected = new() { Title = new() { Action = "keep" }, Date = new() { Action = "keep" }, Correspondent = new() { Action = "keep" }, DocumentType = new() { Action = "keep" } }
    };
    private const string Keep = """
        {"schema_version":"1","title":{"action":"keep","value":null,"evidence":[]},"date":{"action":"keep","value":null,"evidence":[]},"correspondent":{"action":"keep","value":null,"evidence":[]},"document_type":{"action":"keep","value":null,"evidence":[]},"add_tags":[],"ocr":{"action":"keep","pages":[],"evidence":[]},"uncertainty":[]}
        """;
    public void Dispose() => Directory.Delete(directory, true);
}
