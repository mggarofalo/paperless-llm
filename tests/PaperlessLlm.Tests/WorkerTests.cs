using System.Text.Json;
using Microsoft.Extensions.Logging;
using PaperlessLlm.Auth;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;
using PaperlessLlm.Worker;

namespace PaperlessLlm.Tests;

public sealed class WorkerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ppllm-worker-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakePaperless paperless = new();
    private readonly FakeGenerator generator = new();
    private readonly Clock clock = new();
    private readonly MemoryLogger logger = new();
    private string StatePath => Path.Combine(root, "state");
    private string AuditPath => Path.Combine(root, "audit");

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private ReviewWorker Create(int backfill = 0, string model = "gpt-6-luna", int batch = 5, long cap = 2L * 1024 * 1024 * 1024, IDocumentRenderer? renderer = null, int maxJobs = 10_000)
        => new(paperless, generator, new WorkerOptions { StateDirectory = StatePath, Model = model, BackfillLimit = backfill,
            BatchSize = batch, MaxAuditBytes = cap, PollInterval = TimeSpan.FromMinutes(5), MaxJobs = maxJobs },
            new AuditWriter(AuditPath), logger, renderer, clock);

    private Task<WorkerStatus> Status() => WorkerStatusReader.ReadAsync(StatePath);

    [Fact]
    public async Task DefaultBaselineSkipsHistoryAndReviewsOnlyNewDocuments()
    {
        paperless.Documents[7] = ProposalTests.Document(7);
        using var worker = Create();
        Assert.True((await worker.RunOnceAsync()).Initialized);
        Assert.Equal(0, generator.Calls);
        Assert.Equal(7, (await Status()).BaselineId);
        paperless.Documents[8] = ProposalTests.Document(8);
        Assert.Equal(1, (await worker.RunOnceAsync()).Reviewed);
        var job = Assert.Single((await Status()).Jobs);
        Assert.Equal(8, job.DocumentId);
        Assert.Equal(JobStatus.Ready, job.Status);
        Assert.True(File.Exists(Path.Combine(job.AuditPath!, "audit.json")));
        Assert.Equal(0, paperless.Writes);
    }

    [Fact]
    public async Task ExplicitBackfillIsBoundedAndRestartDoesNotExpandIt()
    {
        for (int id = 1; id <= 4; id++) paperless.Documents[id] = ProposalTests.Document(id);
        using (var worker = Create(backfill: 2)) Assert.Equal(2, (await worker.RunOnceAsync()).Reviewed);
        using (var worker = Create(backfill: 4)) await worker.RunOnceAsync();
        Assert.Equal(2, generator.Calls);
        Assert.Equal(2, (await Status()).Jobs.Count);
    }

    [Fact]
    public async Task RestartSkipsUnchangedAndHistoricalSourceChangeCreatesNewAudit()
    {
        paperless.Documents[1] = ProposalTests.Document();
        using (var worker = Create(backfill: 1)) await worker.RunOnceAsync();
        using var restarted = Create();
        await restarted.RunOnceAsync();
        Assert.Equal(1, generator.Calls);
        paperless.Documents[1] = ProposalTests.Document(revision: "revision-2");
        await restarted.RunOnceAsync();
        Assert.Equal(2, generator.Calls);
        Assert.Equal("revision-2", Assert.Single((await Status()).Jobs).RevisionHash);
        Assert.Equal(2, Directory.GetDirectories(AuditPath).Length);
    }

    [Fact]
    public async Task LateTagOnPostBaselineDocumentIsEventuallyDiscovered()
    {
        using var worker = Create();
        await worker.RunOnceAsync();
        paperless.Documents[1] = ProposalTests.Document() with { Tags = [] };
        paperless.Documents[2] = ProposalTests.Document(2);
        await worker.RunOnceAsync();
        paperless.Documents[1] = ProposalTests.Document();
        await worker.RunOnceAsync(); // wrap cursor
        await worker.RunOnceAsync(); // discover late tag
        Assert.Equal(2, generator.Calls);
        Assert.Equal(2, (await Status()).Jobs.Count);
    }

    [Fact]
    public async Task ModelPolicyChangeRequeuesEvenHistoricalReadyJob()
    {
        paperless.Documents[1] = ProposalTests.Document();
        using (var worker = Create(backfill: 1)) await worker.RunOnceAsync();
        var previous = Assert.Single((await Status()).Jobs).PolicyFingerprint;
        using (var worker = Create(model: "gpt-6-sol")) await worker.RunOnceAsync();
        Assert.Equal(2, generator.Calls);
        Assert.NotEqual(previous, Assert.Single((await Status()).Jobs).PolicyFingerprint);
    }

    [Fact]
    public async Task SourceChangedDuringInferenceNeverBecomesReady()
    {
        paperless.Documents[1] = ProposalTests.Document();
        generator.AfterCall = () => paperless.Documents[1] = ProposalTests.Document(revision: "changed-during-inference");
        using var worker = Create(backfill: 1);
        Assert.Equal(1, (await worker.RunOnceAsync()).Failed);
        var job = Assert.Single((await Status()).Jobs);
        Assert.Equal(JobStatus.RetryWaiting, job.Status);
        Assert.Equal("source_changed", job.ErrorCode);
        Assert.Contains("source_changed", await File.ReadAllTextAsync(Path.Combine(job.AuditPath!, "audit.json")));
    }

    [Fact]
    public async Task FailureBackoffTerminatesAndExplicitRetryPreservesBaseline()
    {
        paperless.Documents[1] = ProposalTests.Document();
        generator.Failure = new InvalidOperationException("PRIVATE SECRET");
        using var worker = Create(backfill: 1);
        await worker.RunOnceAsync();
        await worker.RunOnceAsync();
        Assert.Equal(1, generator.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await worker.RunOnceAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await worker.RunOnceAsync();
        var job = Assert.Single((await Status()).Jobs);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(3, job.Attempts);
        clock.Advance(TimeSpan.FromDays(1));
        await worker.RunOnceAsync();
        Assert.Equal(3, generator.Calls);
        await WorkerStatusReader.RetryFailedAsync(StatePath, 1);
        generator.Failure = null;
        await worker.RunOnceAsync();
        Assert.Equal(JobStatus.Ready, Assert.Single((await Status()).Jobs).Status);
        Assert.Equal(1, (await Status()).BaselineId);
        Assert.DoesNotContain("PRIVATE SECRET", string.Join("\n", logger.Messages));
    }

    [Fact]
    public async Task AuthRequiredPausesAllJobsWithoutConsumingAttemptsAndRecovers()
    {
        paperless.Documents[1] = ProposalTests.Document();
        paperless.Documents[2] = ProposalTests.Document(2);
        generator.Failure = new AuthException("PRIVATE TOKEN", requiresSignIn: true);
        using var worker = Create(backfill: 2);
        await worker.RunOnceAsync();
        var status = await Status();
        Assert.True(status.AuthenticationPaused);
        Assert.Equal("authentication_required", status.PauseReason);
        Assert.Equal(0, status.Jobs.First(j => j.DocumentId == 1).Attempts);
        await worker.RunOnceAsync();
        Assert.Equal(1, generator.Calls);
        generator.Failure = null;
        clock.Advance(TimeSpan.FromMinutes(5));
        await worker.RunOnceAsync();
        Assert.False((await Status()).AuthenticationPaused);
        Assert.All((await Status()).Jobs, j => Assert.Equal(JobStatus.Ready, j.Status));
        Assert.DoesNotContain("PRIVATE TOKEN", string.Join("\n", logger.Messages));
    }

    [Fact]
    public async Task NewJobsDoNotWaitBehindTerminalFailure()
    {
        using var worker = Create(batch: 1);
        await worker.RunOnceAsync();
        paperless.Documents[1] = ProposalTests.Document();
        generator.Failure = new InvalidOperationException();
        await worker.RunOnceAsync();
        generator.Failure = null;
        paperless.Documents[2] = ProposalTests.Document(2);
        await worker.RunOnceAsync();
        Assert.Equal(JobStatus.Ready, (await Status()).Jobs.Single(j => j.DocumentId == 2).Status);
    }

    [Fact]
    public async Task MissingCheckpointFieldsFailClosedRatherThanRebaseline()
    {
        Directory.CreateDirectory(StatePath);
        await File.WriteAllTextAsync(Path.Combine(StatePath, "checkpoint.json"), "{}");
        using var worker = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunOnceAsync());
        Assert.Equal(0, paperless.LatestCalls);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task DiskCapacityPausesBeforeInferenceAndIsObservable()
    {
        paperless.Documents[1] = ProposalTests.Document();
        Directory.CreateDirectory(AuditPath);
        await File.WriteAllTextAsync(Path.Combine(AuditPath, "keep"), "existing private audit");
        using var worker = Create(backfill: 1, cap: 1);
        await Assert.ThrowsAnyAsync<Exception>(() => worker.RunOnceAsync());
        Assert.Equal(0, generator.Calls);
        Assert.Equal("audit_capacity_reached", (await Status()).PauseReason);
        Assert.True(File.Exists(Path.Combine(AuditPath, "keep")));
    }

    [Fact]
    public async Task JobCapacityPausesWithoutLosingDiscoveredJobs()
    {
        using var worker = Create(maxJobs: 1);
        await worker.RunOnceAsync();
        paperless.Documents[1] = ProposalTests.Document();
        paperless.Documents[2] = ProposalTests.Document(2);
        await Assert.ThrowsAnyAsync<Exception>(() => worker.RunOnceAsync());
        var status = await Status();
        Assert.Single(status.Jobs);
        Assert.Equal("job_capacity_reached", status.PauseReason);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task VisualEvidenceIsActuallySentAndRetainedPrivately()
    {
        paperless.Documents[1] = ProposalTests.Document();
        generator.Response = ProposalTests.Valid.Replace("\"ocr_text\":null", "\"ocr_text\":\"TOTAL -15.99\"");
        using var worker = Create(backfill: 1, renderer: new FakeRenderer());
        await worker.RunOnceAsync();
        Assert.Single(generator.Images);
        Assert.StartsWith("data:image/png;base64,", generator.Images[0]);
        var job = Assert.Single((await Status()).Jobs);
        Assert.Equal(JobStatus.Ready, job.Status);
        Assert.True(File.Exists(Path.Combine(job.AuditPath!, "original")));
        Assert.True(File.Exists(Path.Combine(job.AuditPath!, "page-1.png")));
        Assert.DoesNotContain(Directory.GetDirectories(AuditPath), path => Path.GetFileName(path).StartsWith('.'));
    }

    [Fact]
    public async Task OcrWithoutRenderedEvidenceFailsAndNeverMarksReady()
    {
        paperless.Documents[1] = ProposalTests.Document();
        generator.Response = ProposalTests.Valid.Replace("\"ocr_text\":null", "\"ocr_text\":\"invented text\"");
        using var worker = Create(backfill: 1);
        await worker.RunOnceAsync();
        var job = Assert.Single((await Status()).Jobs);
        Assert.Equal(JobStatus.RetryWaiting, job.Status);
        Assert.Equal("ocr_requires_visual_source", job.ErrorCode);
    }

    [Fact]
    public async Task RemovedReviewTagSuppressesInferenceForQueuedJob()
    {
        paperless.Documents[1] = ProposalTests.Document();
        paperless.BeforeGet = id => paperless.Documents[id] = paperless.Documents[id] with { Tags = [], RevisionHash = "removed-tag" };
        using var worker = Create(backfill: 1);
        await worker.RunOnceAsync();
        Assert.Equal(0, generator.Calls);
        Assert.Equal(JobStatus.NotEligible, Assert.Single((await Status()).Jobs).Status);
    }

    [Fact]
    public async Task OperationalStatusDoesNotContainPrivateOcrOrTitles()
    {
        paperless.Documents[1] = ProposalTests.Document();
        using var worker = Create(backfill: 1);
        await worker.RunOnceAsync();
        var status = JsonSerializer.Serialize(await Status());
        Assert.DoesNotContain("private OCR", status);
        Assert.DoesNotContain("Synthetic refund", status);
        Assert.NotNull((await Status()).LastPollAt);
        Assert.NotNull((await Status()).LastSuccessAt);
        Assert.DoesNotContain("private OCR", string.Join('\n', logger.Messages));
    }

    [Fact]
    public async Task FailedAuditStorageNeverMarksJobReady()
    {
        paperless.Documents[1] = ProposalTests.Document();
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(AuditPath, "This file deliberately blocks the audit directory");
        using var worker = Create(backfill: 1);
        await worker.RunOnceAsync();
        var job = Assert.Single((await Status()).Jobs);
        Assert.Equal(JobStatus.RetryWaiting, job.Status);
        Assert.Null(job.AuditPath);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task InterruptedProcessingIsDurableAndRetriesWithBackoff()
    {
        paperless.Documents[1] = ProposalTests.Document();
        using var cancellation = new CancellationTokenSource();
        generator.AfterCall = () => cancellation.Cancel();
        using (var worker = Create(backfill: 1))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunOnceAsync(cancellation.Token));
        Assert.Equal(JobStatus.Processing, Assert.Single((await Status()).Jobs).Status);
        generator.AfterCall = null;
        using var restarted = Create();
        await restarted.RunOnceAsync();
        Assert.Equal(JobStatus.RetryWaiting, Assert.Single((await Status()).Jobs).Status);
        Assert.Equal(1, generator.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await restarted.RunOnceAsync();
        Assert.Equal(JobStatus.Ready, Assert.Single((await Status()).Jobs).Status);
        Assert.Equal(2, generator.Calls);
    }

    [Fact]
    public async Task SimultaneousWorkersCannotDuplicateInference()
    {
        paperless.Documents[1] = ProposalTests.Document();
        generator.Pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = Create(backfill: 1);
        var running = first.RunOnceAsync();
        while (generator.Calls == 0) await Task.Delay(5);
        using var second = Create();
        await Assert.ThrowsAsync<IOException>(() => second.RunOnceAsync());
        generator.Pending.SetResult(ProposalTests.Valid);
        await running;
        Assert.Equal(1, generator.Calls);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class FakeGenerator : IProposalGenerator
    {
        public int Calls { get; private set; }
        public string Response { get; set; } = ProposalTests.Valid;
        public Exception? Failure { get; set; }
        public Action? AfterCall { get; set; }
        public TaskCompletionSource<string>? Pending { get; set; }
        public IReadOnlyList<string> Images { get; private set; } = [];
        public Task<string> GenerateAsync(string model, string prompt, IReadOnlyList<string> imageDataUrls, CancellationToken cancellationToken = default)
        {
            Calls++;
            Images = imageDataUrls;
            if (Failure is not null) throw Failure;
            AfterCall?.Invoke();
            return Pending?.Task ?? Task.FromResult(Response);
        }
    }

    private sealed class FakePaperless : IPaperlessClient
    {
        public Dictionary<int, PaperlessDocument> Documents { get; } = [];
        public int Writes => 0; // Interface has no production mutation operations.
        public int LatestCalls { get; private set; }
        public Action<int>? BeforeGet { get; set; }
        public Task<IReadOnlyList<PaperlessDocument>> ListDocumentsAsync(string? tagName = "needs review", int limit = 10, int? afterId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PaperlessDocument>>(Documents.Values.Where(d => d.Id > (afterId ?? 0) && (tagName is null || d.Tags.Contains(2))).OrderBy(d => d.Id).Take(limit).ToArray());
        public Task<int> GetLatestDocumentIdAsync(CancellationToken ct = default)
        { LatestCalls++; return Task.FromResult(Documents.Keys.DefaultIfEmpty(0).Max()); }
        public Task<PaperlessDocument> GetDocumentAsync(int id, CancellationToken ct = default)
        { BeforeGet?.Invoke(id); return Task.FromResult(Documents[id]); }
        public Task<PaperlessTaxonomy> GetTaxonomyAsync(CancellationToken ct = default) => Task.FromResult(ProposalTests.Taxonomy);
        public async Task<OriginalDocument> DownloadOriginalAsync(int id, string destination, CancellationToken ct = default)
        { await File.WriteAllBytesAsync(destination, [1, 2, 3], ct); return new OriginalDocument(destination, "application/pdf", "original-sha", 3); }
    }

    private sealed class FakeRenderer : IDocumentRenderer
    {
        public async Task<IReadOnlyList<RenderedPage>> RenderAsync(OriginalDocument original, string outputDirectory, CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, "page-1.png");
            await File.WriteAllBytesAsync(path, [137, 80, 78, 71], ct);
            return [new RenderedPage(path, "image/png", "synthetic-sha", 1)];
        }
    }

    private sealed class MemoryLogger : ILogger<ReviewWorker>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
