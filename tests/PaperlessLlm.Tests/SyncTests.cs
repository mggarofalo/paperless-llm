using System.Text.Json;
using System.Text.Json.Nodes;
using PaperlessLlm.Organizer;
using PaperlessLlm.Paperless;
using PaperlessLlm.Sync;

namespace PaperlessLlm.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-sync-" + Guid.NewGuid().ToString("N"));
    private static JsonElement Intent(string title) => JsonSerializer.SerializeToElement(new
    {
        schema_version = "1",
        title = new { action = "set", value = title, evidence = new[] { "page 1: Merchant receipt heading" } },
        date = new { action = "keep", value = (string?)null, evidence = Array.Empty<string>() },
        correspondent = new { action = "keep", value = (int?)null, evidence = Array.Empty<string>() },
        document_type = new { action = "keep", value = (int?)null, evidence = Array.Empty<string>() },
        add_tags = Array.Empty<object>(),
        ocr = new { action = "keep", pages = Array.Empty<object>(), evidence = Array.Empty<string>() },
        uncertainty = Array.Empty<string>()
    });

    [Fact]
    public async Task AppliesAllManagedFieldsIncludingCompleteMultipageOcr()
    {
        var api = new Fake();
        var node = JsonNode.Parse(Intent("New title").GetRawText())!;
        node["date"] = JsonSerializer.SerializeToNode(new { action = "set", value = "2026-03-04", evidence = new[] { "page 1 transaction date" } });
        foreach (var field in new[] { "correspondent", "document_type" })
            node[field] = JsonSerializer.SerializeToNode(new { action = "set", value = 1, evidence = new[] { "page 1 issuer and receipt" } });
        node["ocr"] = JsonSerializer.SerializeToNode(new { action = "set", pages = new[]
        {
            new { page = 1, text = "Refund -15.99", complete = true, uncertainty = Array.Empty<string>() },
            new { page = 2, text = "Second page", complete = true, uncertainty = Array.Empty<string>() }
        }, evidence = new[] { "pages 1 and 2 literal complete text" } });
        await new IntentSynchronizer(api, api, directory).ApplyAsync("all", api.Document,
            JsonSerializer.SerializeToElement(node), 2, default);
        Assert.Equal("2026-03-04", api.Document.Created);
        Assert.Equal(1, api.Document.CorrespondentId);
        Assert.Equal(1, api.Document.DocumentTypeId);
        Assert.Equal("Refund -15.99\n\nSecond page", api.Document.Content);
    }

    [Fact]
    public async Task PendingWriteCannotReplayAfterTaxonomyBecomesProtected()
    {
        var api = new Fake { ThrowBeforeCommit = true };
        var node = JsonNode.Parse(Intent("New").GetRawText())!;
        node["add_tags"] = JsonSerializer.SerializeToNode(new[] { new { id = 40, evidence = new[] { "page 1 receipt" } } });
        var intent = JsonSerializer.SerializeToElement(node);
        var source = api.Document;
        var sync = new IntentSynchronizer(api, api, directory);
        await Assert.ThrowsAsync<PaperlessException>(() => sync.ApplyAsync("taxonomy", source, intent, 1, default));
        api.ExtraTag = "inbox";
        await Assert.ThrowsAsync<SyncConflictException>(() => sync.ApplyAsync("taxonomy", source, intent, 1, default));
        Assert.Equal(1, api.Writes);
        Assert.Equal("Old title", api.Document.Title);
    }

    [Fact]
    public async Task AppliesMinimalPatchAndReviewMarkerPreservingWorkflowTags()
    {
        var api = new Fake();
        var source = api.Document;
        var result = await new IntentSynchronizer(api, api, directory).ApplyAsync("job1", source, Intent("Merchant receipt"), 1, default);
        Assert.Equal("applied", result.Outcome);
        Assert.Equal("Merchant receipt", api.Document.Title);
        Assert.Equal(new[] { 2, 27, 30 }, api.Document.Tags);
        Assert.Equal(new[] { "tags", "title" }, api.LastPatch!.Keys.Order().ToArray());
        Assert.Equal(1, api.Writes);
    }

    [Fact]
    public async Task NoChangeDoesNotAddReviewMarker()
    {
        var api = new Fake();
        var result = await new IntentSynchronizer(api, api, directory).ApplyAsync("noop", api.Document, Intent(api.Document.Title), 1, default);
        Assert.Equal("no_change", result.Outcome);
        Assert.DoesNotContain(2, api.Document.Tags);
        Assert.Equal(0, api.Writes);
    }

    [Fact]
    public async Task LostAcknowledgmentReconcilesWithoutSecondWrite()
    {
        var api = new Fake { ThrowAfterCommit = true };
        var source = api.Document;
        var sync = new IntentSynchronizer(api, api, directory);
        await Assert.ThrowsAsync<PaperlessException>(() => sync.ApplyAsync("retry", source, Intent("New title"), 1, default));
        var result = await new IntentSynchronizer(api, api, directory).ApplyAsync("retry", source, Intent("New title"), 1, default);
        Assert.Equal("applied", result.Outcome);
        Assert.Equal(1, api.Writes);
    }

    [Fact]
    public async Task VerifiedJournalNeverRetagsAfterHumanReview()
    {
        var api = new Fake();
        var source = api.Document;
        var sync = new IntentSynchronizer(api, api, directory);
        await sync.ApplyAsync("done", source, Intent("New title"), 1, default);
        api.Document = api.Document with { Tags = [27, 30], RevisionHash = "human-reviewed" };
        await sync.ApplyAsync("done", source, Intent("New title"), 1, default);
        Assert.Equal(1, api.Writes);
        Assert.DoesNotContain(2, api.Document.Tags);
    }

    [Fact]
    public async Task ConcurrentCorrectionAfterAmbiguousCommitIsNotOverwritten()
    {
        var api = new Fake { ThrowAfterCommit = true };
        var source = api.Document;
        var sync = new IntentSynchronizer(api, api, directory);
        await Assert.ThrowsAsync<PaperlessException>(() => sync.ApplyAsync("conflict", source, Intent("New title"), 1, default));
        api.Document = api.Document with { Title = "Human title", RevisionHash = "human-edit" };
        await Assert.ThrowsAsync<SyncConflictException>(() => sync.ApplyAsync("conflict", source, Intent("New title"), 1, default));
        Assert.Equal("Human title", api.Document.Title);
        Assert.Equal(1, api.Writes);
    }

    [Fact]
    public async Task StaleSourceAndDryRunNeverWrite()
    {
        var api = new Fake();
        var source = api.Document;
        api.Document = source with { RevisionHash = "changed" };
        await Assert.ThrowsAsync<SyncConflictException>(() => new IntentSynchronizer(api, api, directory)
            .ApplyAsync("stale", source, Intent("New"), 1, default));
        var result = await new IntentSynchronizer(api, api, directory, dryRun: true)
            .ApplyAsync("dry", api.Document, Intent("New"), 1, default);
        Assert.Equal("dry_run", result.Outcome);
        Assert.Equal(0, api.Writes);
        Assert.False(File.Exists(Path.Combine(directory, "dry.json")));
    }

    private sealed class Fake : IPaperlessClient, IPaperlessWriter
    {
        public PaperlessDocument Document = new(1, "Old title", "Original content", "2026-01-01", "2026-01-02", null, null,
            [27, 30], "image/png", "scan.png", "before");
        public int Writes;
        public bool ThrowAfterCommit;
        public bool ThrowBeforeCommit;
        public string ExtraTag = "receipt";
        public IReadOnlyDictionary<string, object?>? LastPatch;
        public Task<PaperlessDocument> GetDocumentAsync(int id, CancellationToken ct = default) => Task.FromResult(Document);
        public Task<PaperlessTaxonomy> GetTaxonomyAsync(CancellationToken ct = default) => Task.FromResult(new PaperlessTaxonomy(
            [new(2, "needs review"), new(27, "receipt to log"), new(30, "hsa unreimbursed"), new(40, ExtraTag)], [new(1, "Merchant")], [new(1, "Receipt")]));
        public Task PatchAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
        {
            Writes++;
            LastPatch = fields;
            if (ThrowBeforeCommit) throw new PaperlessException("Simulated uncommitted write");
            var json = JsonSerializer.SerializeToElement(fields);
            Document = Document with
            {
                Title = json.TryGetProperty("title", out var title) ? title.GetString()! : Document.Title,
                Content = json.TryGetProperty("content", out var content) ? content.GetString()! : Document.Content,
                Created = json.TryGetProperty("created", out var date) ? date.GetString()! : Document.Created,
                CorrespondentId = json.TryGetProperty("correspondent", out var correspondent) ? correspondent.GetInt32() : Document.CorrespondentId,
                DocumentTypeId = json.TryGetProperty("document_type", out var type) ? type.GetInt32() : Document.DocumentTypeId,
                Tags = json.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().Select(t => t.GetInt32()).ToArray() : Document.Tags,
                RevisionHash = "written" + Writes
            };
            if (ThrowAfterCommit) throw new PaperlessException("Simulated lost acknowledgment");
            return Task.CompletedTask;
        }
        public Task<int> GetLatestDocumentIdAsync(CancellationToken ct = default) => Task.FromResult(1);
        public Task<IReadOnlyList<PaperlessDocument>> ListDocumentsAsync(string? tagName = "needs review", int limit = 10,
            int? afterId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OriginalDocument> DownloadOriginalAsync(int id, string destination, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
