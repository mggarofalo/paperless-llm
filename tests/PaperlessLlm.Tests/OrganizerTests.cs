using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PaperlessLlm.Organizer;
using PaperlessLlm.Paperless;
namespace PaperlessLlm.Tests;
public sealed class OrganizerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "organizer-test-" + Guid.NewGuid().ToString("N"));
    private readonly Fake source = new();
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private OrganizerWorker Worker(int backfill = 0, int max = 100) => new(source, source, source, source, source,
        new OrganizerOptions { StateDirectory = root, SourceUrl = "https://example.test", BackfillLimit = backfill, MaxJobs = max }, NullLogger<OrganizerWorker>.Instance);
    [Fact] public async Task BaselineAndRestartPreserveCompletedIdsAfterHumanEdits()
    {
        source.Docs[1] = ProposalTests.Document(1);
        using (var worker = Worker()) await worker.RunOnceAsync();
        source.Docs[2] = ProposalTests.Document(2);
        using (var worker = Worker()) Assert.Equal(1, (await worker.RunOnceAsync()).Completed);
        source.Docs[2] = source.Docs[2] with { Tags = [], Title = "human change", RevisionHash = "changed" };
        using (var worker = Worker()) await worker.RunOnceAsync();
        Assert.Equal(1, source.Inferences); Assert.Equal(1, source.Syncs);
        Assert.All(source.Filters, Assert.Null);
    }
    [Fact] public async Task ExplicitRetryResumesPersistedIntentWithoutModelCall()
    {
        source.Docs[1] = ProposalTests.Document(1); source.FailSync = true;
        using (var worker = Worker(1)) Assert.Equal(1, (await worker.RunOnceAsync()).Failed);
        source.FailSync = false;
        using (var worker = Worker()) { await worker.RetryAsync(1); Assert.Equal(1, (await worker.RunOnceAsync()).Completed); }
        Assert.Equal(1, source.Inferences); Assert.Equal(2, source.Syncs);
    }
    [Fact] public async Task InterruptedSyncResumesSameJobAndIntent()
    {
        source.Docs[1] = ProposalTests.Document(1); source.CancelSync = true;
        using var cts = new CancellationTokenSource(); source.Cancellation = cts;
        using (var worker = Worker(1)) await Assert.ThrowsAsync<OperationCanceledException>(() => worker.RunOnceAsync(cts.Token));
        var before = Assert.Single((await OrganizerStatusReader.ReadAsync(root)).Jobs);
        Assert.Equal(OrganizerJobState.Running, before.State);
        source.CancelSync = false;
        using (var worker = Worker()) Assert.Equal(1, (await worker.RunOnceAsync()).Completed);
        var after = Assert.Single((await OrganizerStatusReader.ReadAsync(root)).Jobs);
        Assert.Equal(before.JobId, after.JobId); Assert.Equal(1, source.Inferences);
    }
    [Fact] public async Task ConflictIsTerminalAndExceptionSecretsAreNotPersisted()
    {
        source.Docs[1] = ProposalTests.Document(1); source.Conflict = true;
        using var worker = Worker(1); await worker.RunOnceAsync(); await worker.RunOnceAsync();
        var job = Assert.Single((await OrganizerStatusReader.ReadAsync(root)).Jobs);
        Assert.Equal(OrganizerJobState.Failed, job.State); Assert.Equal("sync_conflict", job.ErrorCode);
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(Path.Combine(root, "jobs", "1.json")));
        Assert.Equal(1, source.Syncs);
    }
    [Fact] public async Task BackfillDoesNotExpandOnRestart()
    {
        for (int i = 1; i <= 4; i++) source.Docs[i] = ProposalTests.Document(i);
        using (var worker = Worker(1)) await worker.RunOnceAsync();
        using (var worker = Worker(4)) await worker.RunOnceAsync();
        Assert.Equal(1, source.Inferences);
    }
    [Fact] public async Task CapacityDoesNotAdvancePastUnpersistedJob()
    {
        using (var worker = Worker()) await worker.RunOnceAsync();
        source.Docs[1] = ProposalTests.Document(1); source.Docs[2] = ProposalTests.Document(2);
        using (var worker = Worker(max: 1)) await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunOnceAsync());
        Assert.Equal(0, (await OrganizerStatusReader.ReadAsync(root)).CursorId);
        using (var worker = Worker()) Assert.Equal(2, (await worker.RunOnceAsync()).Completed);
    }
    [Fact] public async Task GlobalLockExcludesConcurrentWorkers()
    {
        Directory.CreateDirectory(root);
        using var held = new FileStream(Path.Combine(root, "organizer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var worker = Worker();
        await Assert.ThrowsAsync<IOException>(() => worker.RunOnceAsync());
        Assert.Equal(0, source.Inferences);
    }
    [Fact] public async Task LegacyCheckpointRequiresExplicitMigrationDirectory()
    {
        Directory.CreateDirectory(root); await File.WriteAllTextAsync(Path.Combine(root, "checkpoint.json"), "{}");
        using var worker = Worker();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunOnceAsync());
        Assert.Equal("legacy_review_state_use_new_organizer_directory", error.Message);
    }
    [Fact] public async Task AuthPauseDoesNotBurnOtherJobsAttempts()
    {
        source.Docs[1] = ProposalTests.Document(1); source.Docs[2] = ProposalTests.Document(2); source.AuthFail = true;
        using var worker = Worker(2); await worker.RunOnceAsync();
        var status = await OrganizerStatusReader.ReadAsync(root);
        Assert.Equal("auth_required", status.PauseReason);
        Assert.All(status.Jobs, j => Assert.Equal(0, j.Attempts));
        Assert.Equal(1, source.Inferences);
    }
    [Fact] public async Task InvalidIntentRegeneratesOnRetry()
    {
        source.Docs[1] = ProposalTests.Document(1); source.Invalid = true;
        using var worker = Worker(1); await worker.RunOnceAsync();
        source.Invalid = false; await worker.RetryAsync(1); await worker.RunOnceAsync();
        Assert.Equal(2, source.Inferences);
    }
    [Fact] public async Task RejectedResponseRemainsInPrivateAttemptEvidence()
    {
        source.Docs[1] = ProposalTests.Document(1); source.Invalid = true;
        using var worker = Worker(1); await worker.RunOnceAsync();
        var response = Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "evidence"), "response.txt", SearchOption.AllDirectories));
        Assert.Equal("{}", await File.ReadAllTextAsync(response));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(response)!, "request.json")));
        var current = JsonSerializer.Deserialize<OrganizerJob>(await File.ReadAllTextAsync(Path.Combine(root, "jobs", "1.json")))!;
        Assert.Null(current.Intent);
    }
    [Fact] public async Task AuthRecoveryUsesFreshEvidenceDirectoryEvenWithoutConsumedAttempt()
    {
        source.Docs[1] = ProposalTests.Document(1); source.AuthFail = true;
        using (var worker = Worker(1)) await worker.RunOnceAsync();
        Assert.Equal(0, Assert.Single((await OrganizerStatusReader.ReadAsync(root)).Jobs).Attempts);
        source.AuthFail = false;
        using (var worker = Worker())
        {
            await worker.RetryAsync(1);
            Assert.Equal(1, (await worker.RunOnceAsync()).Completed);
        }
        Assert.Equal(2, source.Inferences);
        Assert.Equal(OrganizerJobState.Completed, Assert.Single((await OrganizerStatusReader.ReadAsync(root)).Jobs).State);
    }
    [Fact] public async Task RateLimitDefersWholeBatchWithoutConsumingAttempts()
    {
        source.Docs[1] = ProposalTests.Document(1); source.Docs[2] = ProposalTests.Document(2); source.RateLimit = true;
        using var worker = Worker(2); await worker.RunOnceAsync();
        var status = await OrganizerStatusReader.ReadAsync(root);
        Assert.Equal("rate_limited", status.PauseReason);
        Assert.All(status.Jobs, j => Assert.Equal(0, j.Attempts));
        Assert.Equal(1, source.Inferences); Assert.Equal(0, source.Syncs);
    }
    [Fact] public async Task PollFailureIsVisibleEvenAfterDiscoveryHeartbeat()
    {
        source.TaxonomyFail = true;
        using var worker = Worker();
        await worker.StartAsync(default);
        OrganizerStatus? status = null;
        for (int i = 0; i < 100; i++)
        {
            status = await OrganizerStatusReader.ReadAsync(root);
            if (status.PauseReason is not null) break;
            await Task.Delay(20);
        }
        await worker.StopAsync(default);
        Assert.Equal("paperless_authentication_failed", status!.PauseReason);
        Assert.NotNull(status.LastActivityAt);
    }
    private sealed class Fake : IPaperlessClient, IIntentRunner, IIntentContextBuilder, IIntentSynchronizer, IDocumentRenderer
    {
        public Dictionary<int, PaperlessDocument> Docs { get; } = [];
        public List<string?> Filters { get; } = [];
        public int Inferences, Syncs;
        public bool FailSync, CancelSync, Conflict, AuthFail, Invalid, RateLimit, TaxonomyFail;
        public CancellationTokenSource? Cancellation;
        public Task<IReadOnlyList<PaperlessDocument>> ListDocumentsAsync(string? tagName = "needs review", int limit = 10, int? afterId = null, CancellationToken ct = default)
        { Filters.Add(tagName); return Task.FromResult<IReadOnlyList<PaperlessDocument>>(Docs.Values.Where(x => afterId is null || x.Id > afterId).OrderBy(x => x.Id).Take(limit).ToArray()); }
        public Task<int> GetLatestDocumentIdAsync(CancellationToken ct = default) => Task.FromResult(Docs.Keys.DefaultIfEmpty().Max());
        public Task<PaperlessDocument> GetDocumentAsync(int id, CancellationToken ct = default) => Task.FromResult(Docs[id]);
        public Task<PaperlessTaxonomy> GetTaxonomyAsync(CancellationToken ct = default)
        {
            if (TaxonomyFail) throw new PaperlessException("private", "paperless_authentication_failed");
            return Task.FromResult(new PaperlessTaxonomy([], [], []));
        }
        public async Task<OriginalDocument> DownloadOriginalAsync(int id, string destination, CancellationToken ct = default)
        { if (File.Exists(destination)) throw new IOException("download_destination_exists"); await File.WriteAllTextAsync(destination, "image", ct); return new(destination, "image/png", "hash", 5); }
        public Task<IReadOnlyList<RenderedPage>> RenderAsync(OriginalDocument original, string outputDirectory, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RenderedPage>>([new(original.Path, "image/png", "hash", 1)]);
        public Task<string> GenerateAsync(string model, string instructions, string prompt, IReadOnlyList<string> imageDataUrls, JsonElement schema, CancellationToken ct)
        { Inferences++; if (RateLimit) throw new PaperlessLlm.Runner.RunnerRateLimitException(); if (AuthFail) throw new PaperlessLlm.Auth.AuthException("secret", true); return Task.FromResult("{}"); }
        public Task<IntentContext> BuildAsync(PaperlessDocument source, PaperlessTaxonomy taxonomy, int pageCount, CancellationToken ct)
            => Task.FromResult(new IntentContext("instructions", "prompt", JsonDocument.Parse("{}").RootElement.Clone(), "v1"));
        public Task<SyncResult> ApplyAsync(string jobId, PaperlessDocument source, JsonElement intent, int pageCount, CancellationToken ct)
        {
            Syncs++;
            if (CancelSync) { Cancellation!.Cancel(); ct.ThrowIfCancellationRequested(); }
            if (Invalid) throw new PaperlessLlm.Review.ProposalValidationException("invalid");
            if (Conflict) throw new SyncConflictException("secret");
            if (FailSync) throw new InvalidOperationException("secret");
            return Task.FromResult(new SyncResult("no_change", source));
        }
    }
}
