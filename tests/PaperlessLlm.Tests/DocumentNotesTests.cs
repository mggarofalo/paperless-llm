using System.Text.Json;
using System.Text.Json.Nodes;
using PaperlessLlm.Intent;
using PaperlessLlm.Organizer;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;
using PaperlessLlm.Sync;

namespace PaperlessLlm.Tests;

public sealed class DocumentNotesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-notes-" + Guid.NewGuid().ToString("N"));
    private const string Summary = "The invoice records replacement of the blower motor, total $684.20, paid by card.";
    private static readonly PaperlessTaxonomy Taxonomy = new([new(2, "needs review"), new(3, "inbox", true)], [], []);
    private static PaperlessDocument Source => new(1, "Repair invoice", "Blower motor replaced. Total $684.20. Paid by card.",
        "2026-02-14", "2026-02-14", null, null, [3], "application/pdf", "repair.pdf", "before")
        { Notes = [new(10, "Human note: called the contractor.")] };

    private static JsonElement Intent(bool keep = false)
    {
        var unchanged = new { action = "keep", value = (string?)null, evidence = Array.Empty<string>() };
        return JsonSerializer.SerializeToElement(new
        {
            schema_version = "2", title = unchanged, date = unchanged, correspondent = unchanged, document_type = unchanged,
            add_tags = Array.Empty<object>(), ocr = new { action = "keep", pages = Array.Empty<object>(), evidence = Array.Empty<string>() },
            uncertainty = Array.Empty<string>(),
            note = new { action = keep ? "keep" : "set", value = keep ? null : Summary,
                evidence = keep ? [] : new[] { "Blower motor replaced.", "Total $684.20.", "Paid by card." } }
        });
    }

    private IntentSynchronizer Sync(Fake api, bool dryRun = false) => new(api, api, directory, dryRun: dryRun);
    private async Task<JsonNode> Journal() => JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "job.json")))!;

    [Fact]
    public async Task NoteOnlyChangePreservesHumanNotesAndWorkflowTagsAndMarksReview()
    {
        var api = new Fake();
        var result = await Sync(api).ApplyAsync("job", Source, Intent(), 0, default);
        Assert.Equal("applied", result.Outcome);
        Assert.Equal(Source.Notes![0], api.Document.Notes![0]);
        Assert.Equal(DocumentNotes.Label + "\n" + Summary, api.Document.Notes[1].Note);
        Assert.Equal(new[] { 2, 3 }, api.Document.Tags);
        Assert.Equal(new[] { "tags" }, api.PatchFields);
        Assert.Equal(Source.Content, api.Document.Content);
        Assert.Equal("verified", (await Journal())["Status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoopAndExistingSummaryDoNotRetag(bool existing)
    {
        var api = new Fake();
        if (existing) api.Document = Source with { Notes = [.. Source.Notes!, new(11, DocumentNotes.Label + "\nHuman-corrected summary")] };
        var result = await Sync(api).ApplyAsync("job", api.Document, Intent(keep: !existing), 0, default);
        Assert.Equal("no_change", result.Outcome);
        Assert.Equal(0, api.Patches + api.Posts);
        Assert.DoesNotContain(2, api.Document.Tags);
    }

    [Fact]
    public async Task ReprocessingAndVerifiedReplayNeverDuplicateOrRetag()
    {
        var api = new Fake();
        await Sync(api).ApplyAsync("job", Source, Intent(), 0, default);
        api.Document = api.Document with { Tags = [3], RevisionHash = "reviewed" };
        await Sync(api).ApplyAsync("job", Source, Intent(), 0, default);
        var result = await Sync(api).ApplyAsync("new-job", api.Document, Intent(), 0, default);
        Assert.Equal("no_change", result.Outcome);
        Assert.Equal(1, api.Posts);
        Assert.Equal(1, api.Patches);
        Assert.DoesNotContain(2, api.Document.Tags);
    }

    [Fact]
    public async Task LostNoteResponseReconcilesAfterRestartWithoutSecondPost()
    {
        var api = new Fake { FailAfterNote = true };
        await Assert.ThrowsAsync<PaperlessException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        Assert.True((await Journal())["NoteAttempted"]!.GetValue<bool>());
        // A later human edit must not cause the existing summary to be recreated.
        api.Document = api.Document with { Title = "Human correction", Tags = [3], RevisionHash = "human" };
        Assert.Equal("applied", (await Sync(api).ApplyAsync("job", Source, Intent(), 0, default)).Outcome);
        Assert.Equal(1, api.Posts);
        Assert.Equal(1, api.Patches);
        Assert.DoesNotContain(2, api.Document.Tags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedPostNeverReplaysEvenIfNothingVisible(bool successfulResponse)
    {
        var api = new Fake { FailBeforeNote = !successfulResponse, IgnoreNote = successfulResponse };
        await Assert.ThrowsAnyAsync<Exception>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        var error = await Assert.ThrowsAsync<SyncConflictException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        Assert.Equal("sync_note_write_unconfirmed", error.Code);
        Assert.Equal(1, api.Posts);
        Assert.Equal("metadata_verified", (await Journal())["Status"]!.GetValue<string>());
    }

    [Fact]
    public async Task LostMetadataResponseRecoversBeforePostingNote()
    {
        var api = new Fake { FailAfterPatch = true };
        await Assert.ThrowsAsync<PaperlessException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        Assert.Equal(0, api.Posts);
        await Sync(api).ApplyAsync("job", Source, Intent(), 0, default);
        Assert.Equal(1, api.Patches);
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task ExistingReviewMarkerNeedsOnlyANotePost()
    {
        var api = new Fake { Document = Source with { Tags = [2, 3] } };
        await Sync(api).ApplyAsync("job", api.Document, Intent(), 0, default);
        Assert.Equal(0, api.Patches);
        Assert.Equal(1, api.Posts);
    }

    [Theory]
    [InlineData("note")]
    [InlineData("after")]
    [InlineData("attempt")]
    public async Task CorruptNoteJournalNeverReplays(string field)
    {
        var api = new Fake { FailBeforeNote = true };
        await Assert.ThrowsAsync<PaperlessException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        var journal = await Journal();
        if (field == "note") journal["Note"] = "Substituted summary";
        if (field == "after") journal["After"] = null;
        if (field == "attempt") journal["Status"] = "pending";
        await File.WriteAllTextAsync(Path.Combine(directory, "job.json"), journal.ToJsonString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task MetadataCheckpointChecksSourceAndDryRunBeforeFirstPost()
    {
        var api = new Fake { FailBeforeNote = true };
        await Assert.ThrowsAsync<PaperlessException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        // Simulate a crash after the metadata checkpoint but before the attempt checkpoint.
        var journal = await Journal();
        journal["NoteAttempted"] = false;
        await File.WriteAllTextAsync(Path.Combine(directory, "job.json"), journal.ToJsonString());
        Assert.Equal("dry_run", (await Sync(api, true).ApplyAsync("job", Source, Intent(), 0, default)).Outcome);
        api.Document = api.Document with { RevisionHash = "human edit" };
        var error = await Assert.ThrowsAsync<SyncConflictException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        Assert.Equal("sync_note_source_changed", error.Code);
        api.Document = api.Document with { Notes = null };
        error = await Assert.ThrowsAsync<SyncConflictException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        Assert.Equal("sync_note_context_missing", error.Code);
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task BundledPromptNoChangeTemplateMatchesCurrentContract()
    {
        var text = await File.ReadAllTextAsync(OrganizationPrompt.DefaultPath);
        var template = text.Split('\n').Single(line => line.StartsWith("{\"schema_version\""));
        var resolved = JsonNode.Parse(NamedIntentContract.Resolve(template, Source, Taxonomy, 0))!;
        Assert.Equal(DocumentIntent.Version, resolved["schema_version"]!.GetValue<string>());
        Assert.Equal("keep", resolved["note"]!["action"]!.GetValue<string>());
    }

    [Fact]
    public void NoteCannotBePairedWithModelGeneratedOcr()
    {
        var intent = JsonNode.Parse(Intent().GetRawText())!;
        intent["ocr"] = JsonNode.Parse("""{"action":"set","pages":[{"page":1,"text":"invented text","complete":true,"uncertainty":[]}],"evidence":["page 1"]}""");
        var error = Assert.Throws<ProposalValidationException>(() => IntentValidator.Validate(intent.ToJsonString(), Source, Taxonomy, 1));
        Assert.Equal("note_requires_existing_ocr", error.Code);
    }

    [Theory]
    [InlineData("ocr")]
    [InlineData("notes")]
    public async Task ChangedSourceAfterMetadataCommitStopsNote(string change)
    {
        var api = new Fake { FailAfterPatch = true };
        await Assert.ThrowsAsync<PaperlessException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        api.Document = change == "ocr" ? api.Document with { Content = "Corrected OCR" }
            : api.Document with { Notes = [new(10, "Edited human note")] };
        await Assert.ThrowsAsync<SyncConflictException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        Assert.Equal(0, api.Posts);
    }

    [Fact]
    public async Task NoteEditsAreStaleEvenIfMetadataRevisionDoesNotChange()
    {
        var api = new Fake { Document = Source with { Notes = [] } };
        await Assert.ThrowsAsync<SyncConflictException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        Assert.Equal(0, api.Patches + api.Posts);
    }

    [Fact]
    public async Task FreshAndResumedDryRunsNeverPost()
    {
        var api = new Fake();
        Assert.Equal("dry_run", (await Sync(api, true).ApplyAsync("job", Source, Intent(), 0, default)).Outcome);
        Assert.False(Directory.Exists(directory) && File.Exists(Path.Combine(directory, "job.json")));
        Assert.Equal(0, api.Patches + api.Posts);
        api.FailAfterPatch = true;
        await Assert.ThrowsAsync<PaperlessException>(() => Sync(api).ApplyAsync("job", Source, Intent(), 0, default));
        Assert.Equal("dry_run", (await Sync(api, true).ApplyAsync("job", Source, Intent(), 0, default)).Outcome);
        Assert.Equal(0, api.Posts);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("large")]
    [InlineData("control")]
    [InlineData("not-string")]
    [InlineData("quote")]
    [InlineData("no-evidence")]
    [InlineData("evidence-type")]
    [InlineData("action")]
    [InlineData("keep-value")]
    [InlineData("keep-evidence")]
    [InlineData("extra-key")]
    public void RejectsMalformedOrUngroundedNotes(string scenario)
    {
        var node = JsonNode.Parse(Intent().GetRawText())!;
        var note = node["note"]!;
        switch (scenario)
        {
            case "empty": note["value"] = " "; break;
            case "large": note["value"] = new string('x', 1201); break;
            case "control": note["value"] = "Summary\nInjected heading"; break;
            case "not-string": note["value"] = 5; break;
            case "quote": note["evidence"] = new JsonArray("Invented payment"); break;
            case "no-evidence": note["evidence"] = new JsonArray(); break;
            case "evidence-type": note["evidence"] = new JsonArray(3); break;
            case "action": note["action"] = "delete"; break;
            case "keep-value": note["action"] = "keep"; note["evidence"] = new JsonArray(); break;
            case "keep-evidence": note["action"] = "keep"; note["value"] = null; break;
            case "extra-key": note["id"] = 10; break;
        }
        Assert.Throws<ProposalValidationException>(() => IntentValidator.Validate(node.ToJsonString(), Source, Taxonomy, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingNotesAndTruncatedOcrRequireAbstention(bool truncated)
    {
        var source = truncated ? Source with { Content = new string('x', IntentPrompt.MaxOcrCharacters + 1) } : Source with { Notes = null };
        Assert.Throws<ProposalValidationException>(() => IntentValidator.Validate(Intent().GetRawText(), source, Taxonomy, 0));
        IntentValidator.Validate(Intent(true).GetRawText(), source, Taxonomy, 0);
    }

    [Fact]
    public void NamedContractCarriesNoteAndOmitsHumanNoteTextFromModelContext()
    {
        var resolved = NamedIntentContract.Resolve(Intent().GetRawText(), Source, Taxonomy, 0);
        Assert.Equal(Summary, JsonNode.Parse(resolved)!["note"]!["value"]!.GetValue<string>());
        var context = NamedIntentContract.Payload(IntentPrompt.Build(Source, Taxonomy, 0), Taxonomy);
        Assert.DoesNotContain("called the contractor", context);
        var doc = JsonNode.Parse(context)!["document"]!;
        Assert.True(doc["notes_available"]!.GetValue<bool>());
        Assert.False(doc["has_generated_summary"]!.GetValue<bool>());
    }

    private sealed class Fake : IPaperlessClient, IPaperlessWriter
    {
        public PaperlessDocument Document = Source;
        public int Patches, Posts;
        public string[] PatchFields = [];
        public bool FailAfterPatch, FailBeforeNote, FailAfterNote, IgnoreNote;
        public Task<PaperlessDocument> GetDocumentAsync(int id, CancellationToken ct = default) => Task.FromResult(Document);
        public Task<PaperlessTaxonomy> GetTaxonomyAsync(CancellationToken ct = default) => Task.FromResult(Taxonomy);
        public Task PatchAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
        {
            Patches++;
            PatchFields = fields.Keys.ToArray();
            Document = Document with { Tags = JsonSerializer.SerializeToElement(fields).GetProperty("tags").EnumerateArray().Select(t => t.GetInt32()).ToArray(), RevisionHash = "metadata" };
            if (FailAfterPatch) throw new PaperlessException("Simulated lost PATCH response");
            return Task.CompletedTask;
        }
        public Task AddNoteAsync(int id, string note, CancellationToken ct)
        {
            Posts++;
            if (FailBeforeNote) throw new PaperlessException("Simulated uncertain POST");
            if (!IgnoreNote) Document = Document with { Notes = [.. Document.Notes!, new(11, note)], RevisionHash = "note" };
            if (FailAfterNote) throw new PaperlessException("Simulated lost POST response");
            return Task.CompletedTask;
        }
        public Task<int> GetLatestDocumentIdAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PaperlessDocument>> ListDocumentsAsync(string? tagName = "needs review", int limit = 10, int? afterId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OriginalDocument> DownloadOriginalAsync(int id, string destination, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
