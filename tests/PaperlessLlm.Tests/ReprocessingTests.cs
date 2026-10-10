using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PaperlessLlm.Organizer;
using PaperlessLlm.Paperless;
using PaperlessLlm.Intent;
using PaperlessLlm.Runner;

namespace PaperlessLlm.Tests;

public sealed class ReprocessingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "reprocessing-" + Guid.NewGuid().ToString("N"));
    private readonly Fake fake = new();
    private readonly Clock clock = new();
    private OrganizerStore Store => new(root);
    private ReprocessingQueue Queue => new(root);
    private OrganizerOptions Options(int batch = 2, int max = 10000) => new() { StateDirectory = root, SourceUrl = "https://example.test/",
        UseExistingOcr = true, BatchSize = batch, MaxJobs = max };
    private OrganizerWorker Worker(int batch = 2) => new(fake, fake, fake, fake, fake, Options(batch), NullLogger<OrganizerWorker>.Instance, clock);
    private async Task InitializeAsync(int count = 3)
    {
        using var held = Store.Lock();
        await Store.SaveAsync(Store.CheckpointPath, new OrganizerCheckpoint { SourceUrl = Options().SourceUrl,
            BaselineId = count, CursorId = count, LastPollAt = clock.GetUtcNow() }, default);
        for (var id = 1; id <= count; id++)
        {
            fake.Docs[id] = SyntheticDocuments.Document(id) with { Notes = [], Tags = [] };
            await Store.SaveAsync(Store.JobPath(id), new OrganizerJob { DocumentId = id, State = OrganizerJobState.Completed,
                Outcome = "applied", CompletedAt = clock.GetUtcNow().AddDays(-1), Source = fake.Docs[id], After = fake.Docs[id] }, default);
        }
    }
    private Task<ReprocessingRequest> SelectAsync(ReprocessingSelection? selection = null) =>
        new ReprocessingSelector(root, fake).SelectAsync(selection ?? new());
    private Task<ReprocessingRunStatus> StatusAsync(string id) => new ReprocessingStatus(root).ReadAsync(id);
    private async Task<OrganizerJob> JobAsync(int id) => (await Store.ReadAsync<OrganizerJob>(Store.JobPath(id), default))!;
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact] public async Task PreviewIsReadOnlyAndSubmissionDoesNotNeedWorkerLock()
    {
        await InitializeAsync();
        var original = await File.ReadAllTextAsync(Store.JobPath(1));
        using var held = Store.Lock();
        var request = await SelectAsync();
        Assert.False(Directory.Exists(Queue.RunsPath));
        await Queue.SubmitAsync(request);
        Assert.Equal(3, (await StatusAsync(request.RunId)).Queued);
        Assert.Equal(original, await File.ReadAllTextAsync(Store.JobPath(1)));
        Assert.Equal(0, fake.Inferences);
    }
    [Fact] public async Task BulkDrainsAcrossBatchesKeepsHistoryAndReportsSafeResults()
    {
        await InitializeAsync();
        var request = await SelectAsync();
        await Queue.SubmitAsync(request);
        using var worker = Worker();
        Assert.Equal(2, (await worker.RunOnceAsync()).Completed);
        Assert.Equal(1, (await StatusAsync(request.RunId)).Queued);
        var checkpoint = await Store.ReadAsync<OrganizerCheckpoint>(Store.CheckpointPath, default);
        Assert.Equal(clock.GetUtcNow().AddSeconds(1), checkpoint!.NextRunAt);
        Assert.Equal(2, checkpoint.Version); // v0.3.1 refuses this state instead of ignoring notes-only restrictions.
        Assert.Equal(1, (await worker.RunOnceAsync()).Completed);
        Assert.Equal(0, (await worker.RunOnceAsync()).Completed);
        var status = await StatusAsync(request.RunId);
        Assert.Equal("completed", status.Phase);
        Assert.Equal(3, status.NoChange);
        Assert.Equal(3, fake.Inferences);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(root, "history"), "*.json").Length);
        Assert.All(status.Items, i => Assert.Equal(new ProviderUsage(12, 3, 2, 0), i.Usage));
        Assert.All(status.Items, i => Assert.Equal(1, i.Decisions!.UncertaintyCount));
        var json = JsonSerializer.Serialize(status);
        Assert.DoesNotContain("private", json);
        var summary = await OrganizerStatusReader.ReadAsync(root);
        Assert.Equal(0, summary.QueueDepth);
        Assert.NotNull(summary.LastSuccessfulSyncAt);
        Assert.Equal("waiting", summary.Phase);
    }
    [Fact] public async Task ConcurrentRequestsDeduplicateAgainstFixedPreviousIdentity()
    {
        await InitializeAsync(1);
        var a = await SelectAsync(); var b = await SelectAsync();
        await Task.WhenAll(Queue.SubmitAsync(a), Queue.SubmitAsync(b));
        using var worker = Worker();
        await worker.RunOnceAsync();
        Assert.Equal(1, fake.Inferences);
        var statuses = new[] { await StatusAsync(a.RunId), await StatusAsync(b.RunId) };
        Assert.Equal(1, statuses.Sum(s => s.NoChange));
        Assert.Equal("selection_changed", statuses.SelectMany(s => s.Items).Single(i => i.State == "skipped").Reason);
    }
    [Fact] public async Task CrashBetweenArchiveAndReplacementResumesWithPlannedIdentity()
    {
        await InitializeAsync(1);
        var request = await SelectAsync(); await Queue.SubmitAsync(request);
        var old = await JobAsync(1);
        Directory.CreateDirectory(Path.Combine(root, "history"));
        await Store.SaveAsync(Path.Combine(root, "history", old.JobId + ".json"), old, default);
        var processor = new ReprocessingProcessor(Queue);
        await processor.MaterializeAsync(await Store.JobsAsync(default), Options(), default);
        Assert.Equal(request.Items[0].JobId, (await JobAsync(1)).JobId);
        // Replaying materialization after the atomic job write cannot replace it again.
        await processor.MaterializeAsync(await Store.JobsAsync(default), Options(), default);
        using var worker = Worker(); await worker.RunOnceAsync();
        Assert.Equal(1, fake.Inferences);
    }
    [Fact] public async Task CancelThenResumeDoesNotBlockNormalDiscoveryOrReplayCompletedWork()
    {
        await InitializeAsync();
        var request = await SelectAsync(); await Queue.SubmitAsync(request);
        await new ReprocessingProcessor(Queue).MaterializeAsync(await Store.JobsAsync(default), Options(), default);
        await Queue.SetCancelledAsync(request.RunId, true);
        fake.Docs[4] = SyntheticDocuments.Document(4) with { Notes = [] };
        using var worker = Worker();
        Assert.Equal(1, (await worker.RunOnceAsync()).Completed);
        Assert.Equal(3, (await StatusAsync(request.RunId)).Cancelled);
        await Queue.SetCancelledAsync(request.RunId, false);
        await worker.RunOnceAsync(); await worker.RunOnceAsync();
        Assert.Equal(3, (await StatusAsync(request.RunId)).NoChange);
        Assert.Equal(4, fake.Inferences);
    }
    [Fact] public async Task CancellationAfterStartPreservesInterruptedSyncIdentityAndIntent()
    {
        await InitializeAsync(1);
        var request = await SelectAsync(); await Queue.SubmitAsync(request);
        fake.CancelSync = true;
        using var cts = new CancellationTokenSource(); fake.Cancel = cts.Cancel;
        using (var worker = Worker()) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunOnceAsync(cts.Token));
        var interrupted = await JobAsync(1);
        Assert.Equal(OrganizerJobState.Running, interrupted.State);
        Assert.NotNull(interrupted.Intent);
        await Queue.SetCancelledAsync(request.RunId, true);
        fake.CancelSync = false;
        using (var worker = Worker()) Assert.Equal(1, (await worker.RunOnceAsync()).Completed);
        Assert.Equal(interrupted.JobId, fake.SyncIds.Last());
        Assert.Equal(1, fake.Inferences);
        Assert.Equal(1, (await StatusAsync(request.RunId)).NoChange);
    }
    [Fact] public async Task RateLimitPauseSurvivesRestartAndNewSubmissions()
    {
        await InitializeAsync();
        var request = await SelectAsync(); await Queue.SubmitAsync(request);
        fake.RateLimit = true;
        using (var worker = Worker()) await worker.RunOnceAsync(default, scheduled: true);
        var status = await OrganizerStatusReader.ReadAsync(root);
        Assert.Equal("rate_limited", status.PauseReason);
        Assert.Equal("paused", status.Phase);
        Assert.Equal(3, (await StatusAsync(request.RunId)).Blocked);
        fake.RateLimit = false;
        using (var worker = Worker()) Assert.Equal(0, (await worker.RunOnceAsync(default, scheduled: true)).Completed);
        Assert.Equal(1, fake.Inferences);
        clock.Now += TimeSpan.FromHours(1);
        using (var worker = Worker()) Assert.Equal(2, (await worker.RunOnceAsync(default, scheduled: true)).Completed);
    }
    [Fact] public async Task FailedAndUnresolvedJobsAreNeverRegenerated()
    {
        await InitializeAsync(2);
        var job = await JobAsync(1); job.State = OrganizerJobState.Failed;
        await Store.SaveAsync(Store.JobPath(1), job, default);
        var request = await SelectAsync(new([1, 2, 999]));
        Assert.Equal("not_completed", request.Items[0].SkipReason);
        Assert.Equal("not_enrolled", request.Items[2].SkipReason);
        await Queue.SubmitAsync(request);
        using var worker = Worker();
        await Assert.ThrowsAsync<ArgumentException>(() => worker.RetryAsync(1, true));
        fake.FailSync = true;
        await worker.RunOnceAsync();
        Assert.Equal(1, (await StatusAsync(request.RunId)).Blocked);
        fake.FailSync = false;
        clock.Now += TimeSpan.FromMinutes(2);
        await worker.RunOnceAsync();
        Assert.Equal(1, fake.Inferences);
        Assert.Equal(job.JobId, (await JobAsync(1)).JobId);
    }
    [Fact] public async Task MissingSummaryUsesFreshPaperlessNotesAndCutoff()
    {
        await InitializeAsync();
        fake.Docs[1] = fake.Docs[1] with { Notes = [new(1, DocumentNotes.Label + "\nalready present")] };
        fake.Docs[2] = fake.Docs[2] with { Notes = null };
        var selection = await SelectAsync(new(MissingSummary: true));
        Assert.Equal(new[] { "summary_exists", "notes_unavailable", null }, selection.Items.Select(i => i.SkipReason));
        Assert.All((await SelectAsync(new(ProcessedBefore: clock.Now.AddDays(-2)))).Items, i => Assert.Equal("processed_after_cutoff", i.SkipReason));
        fake.Docs.Remove(3);
        Assert.Equal("not_visible", (await SelectAsync(new([3], MissingSummary: true))).Items[0].SkipReason);
    }
    [Fact] public async Task ExplicitHistoryPaginatesAndFailsClosedAtCapacity()
    {
        await InitializeAsync(1);
        for (var id = 2; id <= 105; id++) fake.Docs[id] = SyntheticDocuments.Document(id);
        var request = await SelectAsync(new(IncludeUnenrolled: true, Limit: 105));
        Assert.Equal(105, request.Items.Length);
        Assert.True(fake.Lists >= 2);
        await Assert.ThrowsAsync<ArgumentException>(() => SelectAsync(new(IncludeUnenrolled: true, Limit: 100)));
        await Queue.SubmitAsync(request);
        var jobs = await Store.JobsAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReprocessingProcessor(Queue)
            .MaterializeAsync(jobs, Options(max: 1), default));
        Assert.Equal(0, fake.Inferences);
    }
    [Fact] public async Task NotesOnlyCannotModifyMetadataEvenWhenModelProposesIt()
    {
        await InitializeAsync(1);
        fake.Output = ValidIntent(setTitle: true, note: true);
        var request = await SelectAsync(new(NotesOnly: true)); await Queue.SubmitAsync(request);
        using var worker = Worker(); await worker.RunOnceAsync();
        var intent = fake.LastIntent!.Value;
        Assert.Equal("keep", intent.GetProperty("title").GetProperty("action").GetString());
        Assert.Equal("set", intent.GetProperty("note").GetProperty("action").GetString());
        Assert.Empty(intent.GetProperty("add_tags").EnumerateArray());
        Assert.Equal("keep", intent.GetProperty("ocr").GetProperty("action").GetString());
    }
    [Fact] public async Task PastRunProgressSurvivesSubsequentReprocessing()
    {
        await InitializeAsync(1);
        var first = await SelectAsync(); await Queue.SubmitAsync(first);
        using var worker = Worker(); await worker.RunOnceAsync();
        var second = await SelectAsync(); await Queue.SubmitAsync(second); await worker.RunOnceAsync();
        Assert.Equal(1, (await StatusAsync(first.RunId)).NoChange);
        Assert.Equal(1, (await StatusAsync(second.RunId)).NoChange);
        Assert.Equal(2, fake.Inferences);
    }
    [Fact] public async Task NotesOnlyStillRejectsAnInvalidMetadataProposal()
    {
        await InitializeAsync(1);
        fake.Output = ValidIntent().Replace("\"value\":null", "\"value\":\"invalid keep\"");
        var request = await SelectAsync(new(NotesOnly: true)); await Queue.SubmitAsync(request);
        using var worker = Worker();
        Assert.Equal(1, (await worker.RunOnceAsync()).Failed);
        Assert.Empty(fake.SyncIds);
    }
    [Fact] public async Task DisappearingDocumentReportsFailureAndNeverCallsModel()
    {
        await InitializeAsync(1);
        var request = await SelectAsync(); await Queue.SubmitAsync(request); fake.Docs.Clear();
        using var worker = Worker(); await worker.RunOnceAsync();
        Assert.Equal("document_not_visible", Assert.Single((await StatusAsync(request.RunId)).Items).Reason);
        Assert.Equal(0, fake.Inferences);
    }
    [Fact] public async Task NamedProposalKeepsFieldDispositionsInSafeRunStatus()
    {
        await InitializeAsync(1);
        var raw = System.Text.Json.Nodes.JsonNode.Parse(fake.Output)!;
        raw["decisions"] = System.Text.Json.Nodes.JsonNode.Parse("""{"title":"policy","date":"uncertain","correspondent":"unchanged","document_type":"unchanged","note":"not_applicable"}""");
        fake.Output = raw.ToJsonString(); fake.NamedOutput = true;
        var request = await SelectAsync(); await Queue.SubmitAsync(request);
        using var worker = Worker(); await worker.RunOnceAsync();
        Assert.Equal("uncertain", Assert.Single((await StatusAsync(request.RunId)).Items).Decisions!.Fields!["date"]);
        Assert.False(fake.LastIntent!.Value.TryGetProperty("decisions", out _));
    }
    [Fact] public async Task NormalAndManualJobsGetTurnsEvenWithBatchSizeOne()
    {
        await InitializeAsync(3);
        var request = await SelectAsync(); await Queue.SubmitAsync(request);
        fake.Docs[4] = SyntheticDocuments.Document(4);
        fake.Docs[5] = SyntheticDocuments.Document(5);
        using var worker = Worker(1);
        await worker.RunOnceAsync(); await worker.RunOnceAsync();
        Assert.Equal(1, (await StatusAsync(request.RunId)).NoChange);
        Assert.Equal(OrganizerJobState.Completed, (await JobAsync(4)).State);
        Assert.Equal(OrganizerJobState.Pending, (await JobAsync(5)).State);
    }
    [Fact] public async Task IdleWorkerWakesForSubmissionWithoutHourlyDelay()
    {
        await InitializeAsync(1);
        using var worker = Worker();
        await worker.StartAsync(default);
        try
        {
            var request = await SelectAsync(); await Queue.SubmitAsync(request);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await StatusAsync(request.RunId)).NoChange != 1) await Task.Delay(100, timeout.Token);
            Assert.Equal(1, fake.Inferences);
        }
        finally { await worker.StopAsync(default); }
    }
    [Fact] public async Task SubmissionBoundsAndUninitializedSelectionAreExplicit()
    {
        Assert.Empty(await Queue.ListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => SelectAsync());
        await InitializeAsync(1);
        await Assert.ThrowsAsync<ArgumentException>(() => SelectAsync(new(Limit: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => SelectAsync(new([0])));
        var request = await SelectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Queue.SubmitAsync(request, 1));
        await Queue.SubmitAsync(request);
        Assert.Equal(request.RunId, (await Queue.SubmitAsync(request)).RunId);
        await Assert.ThrowsAsync<ArgumentException>(() => Queue.SubmitAsync(request with { SelectionKey = "different" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Queue.GetAsync("../../secret"));
        await Assert.ThrowsAsync<ArgumentException>(() => Queue.GetAsync(Guid.NewGuid().ToString("N")));
        Assert.Equal(0, fake.Inferences);
    }
    private static string ValidIntent(bool setTitle = false, bool note = false)
    {
        var keep = new { action = "keep", value = (string?)null, evidence = Array.Empty<string>() };
        var set = new { action = "set", value = (string?)"Synthetic summary", evidence = new[] { "TOTAL -15.99" } };
        return JsonSerializer.Serialize(new { schema_version = "2", title = setTitle ? set : keep, date = keep,
            correspondent = keep, document_type = keep, note = note ? set : keep, add_tags = Array.Empty<object>(),
            ocr = new { action = "keep", pages = Array.Empty<object>(), evidence = Array.Empty<string>() }, uncertainty = new[] { "private uncertainty text" } });
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fake : IPaperlessClient, IIntentRunner, IIntentContextBuilder, IIntentSynchronizer, IDocumentRenderer
    {
        public Dictionary<int, PaperlessDocument> Docs = [];
        public int Inferences, Lists;
        public bool CancelSync, RateLimit, FailSync, NamedOutput;
        public Action? Cancel;
        public List<string> SyncIds = [];
        public JsonElement? LastIntent;
        public string Output = ValidIntent();
        public Task<IReadOnlyList<PaperlessDocument>> ListDocumentsAsync(string? tagName = "needs review", int limit = 10, int? afterId = null, CancellationToken ct = default)
        { Lists++; return Task.FromResult<IReadOnlyList<PaperlessDocument>>(Docs.Values.Where(d => d.Id > (afterId ?? 0)).OrderBy(d => d.Id).Take(limit).ToArray()); }
        public Task<int> GetLatestDocumentIdAsync(CancellationToken ct = default) => Task.FromResult(Docs.Keys.DefaultIfEmpty().Max());
        public Task<PaperlessDocument> GetDocumentAsync(int id, CancellationToken ct = default) => Task.FromResult(Docs.TryGetValue(id, out var doc) ? doc : throw new PaperlessException("private", "paperless_not_found"));
        public Task<PaperlessTaxonomy> GetTaxonomyAsync(CancellationToken ct = default) => Task.FromResult(SyntheticDocuments.Taxonomy);
        public Task<OriginalDocument> DownloadOriginalAsync(int id, string destination, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<RenderedPage>> RenderAsync(OriginalDocument original, string directory, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<string> GenerateAsync(string model, string instructions, string prompt, IReadOnlyList<string> images, JsonElement schema, CancellationToken ct)
        { Inferences++; if (RateLimit) throw new RunnerRateLimitException(); return Task.FromResult(Output); }
        public async Task<IntentResult> GenerateWithUsageAsync(string model, string instructions, string prompt, IReadOnlyList<string> images, JsonElement schema, CancellationToken ct)
            => new(await GenerateAsync(model, instructions, prompt, images, schema, ct), new(12, 3, 2, 0));
        public Task<IntentContext> BuildAsync(PaperlessDocument source, PaperlessTaxonomy taxonomy, int pageCount, CancellationToken ct)
            => Task.FromResult(new IntentContext("private policy", "private prompt", DocumentIntent.Schema, "synthetic", NamedOutput));
        public Task<SyncResult> ApplyAsync(string jobId, PaperlessDocument source, JsonElement intent, int pageCount, CancellationToken ct)
        {
            LastIntent = intent; SyncIds.Add(jobId);
            if (CancelSync) { Cancel!(); ct.ThrowIfCancellationRequested(); }
            if (FailSync) throw new InvalidOperationException("private sync error");
            return Task.FromResult(new SyncResult("no_change", source));
        }
    }
}
