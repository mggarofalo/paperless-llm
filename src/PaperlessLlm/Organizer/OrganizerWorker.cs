using System.Text.Json;
using System.Diagnostics;
using PaperlessLlm.Auth;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;
using PaperlessLlm.Runner;
namespace PaperlessLlm.Organizer;
/// <summary>One durable job per discovered document. Completed documents are never automatically overwritten.</summary>
public sealed class OrganizerWorker : BackgroundService
{
    private readonly IPaperlessClient paperless;
    private readonly IIntentRunner runner;
    private readonly IIntentContextBuilder context;
    private readonly IIntentSynchronizer synchronizer;
    private readonly IDocumentRenderer renderer;
    private readonly OrganizerOptions options;
    private readonly ILogger<OrganizerWorker> logger;
    private readonly TimeProvider clock;
    private readonly OrganizerStore store;
    public OrganizerWorker(IPaperlessClient paperless, IIntentRunner runner, IIntentContextBuilder context,
        IIntentSynchronizer synchronizer, IDocumentRenderer renderer, OrganizerOptions options,
        ILogger<OrganizerWorker> logger, TimeProvider? clock = null)
    {
        options.Validate();
        this.paperless = paperless; this.runner = runner; this.context = context; this.synchronizer = synchronizer;
        this.renderer = renderer; this.options = options; this.logger = logger; this.clock = clock ?? TimeProvider.System;
        store = new(options.StateDirectory);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await RunOnceAsync(stoppingToken);
                logger.LogInformation("Organizer poll: completed {Completed}, failed {Failed}", result.Completed, result.Failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                var code = exception is PaperlessException paperlessError ? paperlessError.Code
                    : exception is InvalidOperationException invalid && invalid.Message is "organizer_storage_capacity" or "organizer_job_capacity"
                    ? invalid.Message : "poll_failed";
                logger.LogWarning("Organizer poll stopped with {Code}", code);
                try
                {
                    using var stateLock = store.Lock();
                    var state = await store.ReadAsync<OrganizerCheckpoint>(store.CheckpointPath, stoppingToken);
                    if (state is not null)
                    {
                        state.PauseReason = code; state.NextRunAt = clock.GetUtcNow() + options.PollInterval;
                        await HeartbeatAsync(state, stoppingToken);
                    }
                }
                catch (Exception) { logger.LogWarning("Could not persist poll failure status"); }
            }
            try { await Task.Delay(options.PollInterval, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    public async Task<OrganizerPollResult> RunOnceAsync(CancellationToken ct = default)
    {
        using var stateLock = store.Lock();
        CheckCapacity();
        if (File.Exists(Path.Combine(store.DirectoryPath, "checkpoint.json")))
            throw new InvalidOperationException("legacy_review_state_use_new_organizer_directory");
        var taxonomy = await paperless.GetTaxonomyAsync(ct);
        SetupValidation.RequireReviewTag(taxonomy, options.ReviewTag);
        var state = await store.ReadAsync<OrganizerCheckpoint>(store.CheckpointPath, ct);
        var initialized = state is null;
        var jobs = await store.JobsAsync(ct);
        if (state is null)
        {
            var bootstrapPath = Path.Combine(store.DirectoryPath, "organizer-bootstrap.json");
            state = await store.ReadAsync<OrganizerCheckpoint>(bootstrapPath, ct);
            if (state is null)
            {
                var baseline = await paperless.GetLatestDocumentIdAsync(ct);
                var backfill = options.BackfillLimit > 0
                    ? await paperless.ListDocumentsAsync(null, options.BackfillLimit, ct: ct) : [];
                state = new() { BaselineId = baseline, CursorId = baseline, SourceUrl = options.SourceUrl,
                    InitialBackfillIds = backfill.Where(d => d.Id <= baseline).Select(d => d.Id).Distinct().ToArray() };
                // Keep the original baseline and explicit enrollment set if initialization crashes.
                await store.SaveAsync(bootstrapPath, state, ct);
            }
            if (state.SourceUrl != options.SourceUrl) throw new InvalidOperationException("organizer_scope_changed_or_invalid");
            foreach (var id in state.InitialBackfillIds) await EnrollAsync(id, jobs, ct);
            // Jobs are durable before checkpoint advances, including initialization.
            await store.SaveAsync(store.CheckpointPath, state, ct);
        }
        if (state.Version != 1 || state.SourceUrl != options.SourceUrl || state.CursorId < state.BaselineId)
            throw new InvalidOperationException("organizer_scope_changed_or_invalid");
        foreach (var job in jobs.Where(j => j.State == OrganizerJobState.Running))
        {
            job.State = OrganizerJobState.Pending; // Saved intent resumes sync with the same idempotency key.
            await SaveAsync(job, ct);
        }
        var discovered = await paperless.ListDocumentsAsync(null, options.DiscoveryLimit, state.CursorId, ct);
        foreach (var doc in discovered.OrderBy(d => d.Id))
        {
            if (doc.Id <= state.BaselineId) continue;
            await EnrollAsync(doc.Id, jobs, ct);
            state.CursorId = Math.Max(state.CursorId, doc.Id);
        }
        // Rotate across post-baseline IDs. Never use review tags or modified timestamps as work triggers.
        if (discovered.Count < options.DiscoveryLimit) state.CursorId = state.BaselineId;
        state.LastPollAt = clock.GetUtcNow();
        state.LastActivityAt = clock.GetUtcNow(); state.NextRunAt = null; state.PauseReason = null;
        await store.SaveAsync(store.CheckpointPath, state, ct);
        int completed = 0, failed = 0;
        foreach (var job in jobs.Where(j => j.State == OrganizerJobState.Pending ||
            j.State == OrganizerJobState.RetryWaiting && j.NextAttemptAt <= clock.GetUtcNow()).Take(options.BatchSize))
        {
            CheckCapacity();
            job.State = OrganizerJobState.Running; job.Attempts++; job.ErrorCode = null;
            await SaveAsync(job, ct);
            await HeartbeatAsync(state, ct);
            logger.LogInformation("Organizer job {JobId} document {DocumentId} started attempt {Attempt}", job.JobId, job.DocumentId, job.Attempts);
            try
            {
                if (job.Intent is null)
                {
                    job.Source = await paperless.GetDocumentAsync(job.DocumentId, ct);
                    var attemptPath = Path.Combine(store.DirectoryPath, "evidence", job.JobId,
                        job.Attempts + "-" + Guid.NewGuid().ToString("N"));
                    AuditWriter.PrivateDirectory(attemptPath);
                    var original = await paperless.DownloadOriginalAsync(job.DocumentId, Path.Combine(attemptPath, "original"), ct);
                    var pages = await renderer.RenderAsync(original, Path.Combine(attemptPath, "pages"), ct);
                    if (pages.Count is < 1 or > 100) throw new InvalidOperationException("invalid_rendered_pages");
                    await HeartbeatAsync(state, ct);
                    var input = await context.BuildAsync(job.Source, taxonomy, pages.Count, ct);
                    await AuditWriter.WritePrivateAsync(Path.Combine(attemptPath, "request.json"),
                        JsonSerializer.Serialize(new { Model = options.Model, Context = input, Source = job.Source,
                            Taxonomy = taxonomy, OriginalSha256 = original.Sha256, Pages = pages }), ct);
                    var images = new List<string>();
                    foreach (var page in pages)
                        images.Add($"data:{page.MediaType};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(page.Path, ct))}");
                    var inferenceTimer = Stopwatch.StartNew();
                    var raw = await runner.GenerateAsync(options.Model, input.Instructions, input.Prompt, images, input.Schema, ct);
                    job.InferenceMilliseconds = inferenceTimer.ElapsedMilliseconds;
                    if (raw.Length > 2 * 1024 * 1024) throw new InvalidOperationException("intent_too_large");
                    await AuditWriter.WritePrivateAsync(Path.Combine(attemptPath, "response.txt"), raw, ct);
                    using var parsed = JsonDocument.Parse(raw);
                    job.Intent = parsed.RootElement.Clone(); job.PolicyVersion = input.PolicyVersion;
                    job.PageCount = pages.Count; job.OriginalSha256 = original.Sha256;
                    job.Model = options.Model; job.RenderedPages = pages;
                    await SaveAsync(job, ct); // Never repeat inference merely because sync was interrupted.
                }
                await HeartbeatAsync(state, ct);
                var syncTimer = Stopwatch.StartNew();
                var result = await synchronizer.ApplyAsync(job.JobId, job.Source!, job.Intent.Value, job.PageCount, ct);
                job.SyncMilliseconds = syncTimer.ElapsedMilliseconds;
                job.After = result.Document; job.Outcome = result.Outcome; job.State = OrganizerJobState.Completed;
                job.CompletedAt = clock.GetUtcNow();
                job.NextAttemptAt = null;
                await SaveAsync(job, ct); completed++;
                logger.LogInformation("Organizer job {JobId} document {DocumentId} completed with {Outcome}; inference {InferenceMilliseconds}ms sync {SyncMilliseconds}ms",
                    job.JobId, job.DocumentId, job.Outcome, job.InferenceMilliseconds, job.SyncMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AuthException exception)
            {
                job.Attempts--; job.State = OrganizerJobState.RetryWaiting;
                job.ErrorCode = exception.RequiresSignIn ? "auth_required" : "auth_unavailable";
                job.NextAttemptAt = clock.GetUtcNow() + options.PollInterval;
                state.PauseReason = job.ErrorCode;
                await SaveAsync(job, ct);
                logger.LogWarning("Organizer paused until authorization recovers: {Code}", job.ErrorCode);
                break; // One credential failure must not exhaust every queued document.
            }
            catch (PaperlessException exception) when (exception.Code is "paperless_authentication_failed" or "paperless_rate_limited")
            {
                job.Attempts--; job.State = OrganizerJobState.RetryWaiting;
                job.ErrorCode = exception.Code; job.NextAttemptAt = clock.GetUtcNow() + options.PollInterval;
                state.PauseReason = job.ErrorCode;
                await SaveAsync(job, ct);
                logger.LogWarning("Organizer paused until next poll: {Code}", job.ErrorCode);
                break;
            }
            catch (RunnerRateLimitException)
            {
                job.Attempts--; job.State = OrganizerJobState.RetryWaiting;
                job.ErrorCode = "rate_limited"; job.NextAttemptAt = clock.GetUtcNow() + options.PollInterval;
                state.PauseReason = job.ErrorCode;
                await SaveAsync(job, ct);
                logger.LogWarning("Organizer paused until next poll: {Code}", job.ErrorCode);
                break;
            }
            catch (Exception exception)
            {
                if (exception is ProposalValidationException) job.Intent = null;
                // Raw provider exceptions may contain prompts, OCR or credentials; persist only fixed codes.
                bool conflict = exception is SyncConflictException;
                job.ErrorCode = conflict ? "sync_conflict" : job.Intent is null ? "inference_failed" : "sync_failed";
                job.State = conflict || job.Attempts >= options.MaxAttempts ? OrganizerJobState.Failed : OrganizerJobState.RetryWaiting;
                job.NextAttemptAt = job.State == OrganizerJobState.RetryWaiting
                    ? clock.GetUtcNow() + TimeSpan.FromTicks(options.RetryBaseDelay.Ticks * (1L << Math.Min(job.Attempts - 1, 9))) : null;
                await SaveAsync(job, ct); failed++;
                logger.LogWarning("Organizer job {JobId} document {DocumentId} stopped with {Code}", job.JobId, job.DocumentId, job.ErrorCode);
            }
        }
        state.NextRunAt = clock.GetUtcNow() + options.PollInterval;
        await HeartbeatAsync(state, ct);
        return new(completed, failed, initialized);
    }
    /// <summary>Explicit retry preserves a proposal for idempotent sync unless regenerate is deliberately selected.</summary>
    public async Task RetryAsync(int documentId, bool regenerate = false, CancellationToken ct = default)
    {
        using var stateLock = store.Lock();
        var job = await store.ReadAsync<OrganizerJob>(store.JobPath(documentId), ct) ?? throw new InvalidOperationException("job_not_found");
        if (regenerate)
        {
            // Preserve the previous job's evidence before replacing its current index entry.
            var history = Path.Combine(store.DirectoryPath, "history");
            AuditWriter.PrivateDirectory(history);
            await store.SaveAsync(Path.Combine(history, job.JobId + ".json"), job, ct);
            job.Intent = null; job.Source = null; job.After = null; job.JobId = Guid.NewGuid().ToString("N");
            job.CreatedAt = clock.GetUtcNow(); job.CompletedAt = null; job.Outcome = null;
            job.InferenceMilliseconds = null; job.SyncMilliseconds = null;
        }
        job.State = OrganizerJobState.Pending; job.Attempts = 0; job.ErrorCode = null; job.NextAttemptAt = null;
        await SaveAsync(job, ct);
    }
    private Task HeartbeatAsync(OrganizerCheckpoint state, CancellationToken ct)
    {
        state.LastActivityAt = clock.GetUtcNow();
        return store.SaveAsync(store.CheckpointPath, state, ct);
    }
    private Task SaveAsync(OrganizerJob job, CancellationToken ct)
    {
        job.UpdatedAt = clock.GetUtcNow();
        return store.SaveAsync(store.JobPath(job.DocumentId), job, ct);
    }
    private async Task EnrollAsync(int id, List<OrganizerJob> jobs, CancellationToken ct)
    {
        if (jobs.Any(j => j.DocumentId == id)) return;
        if (jobs.Count >= options.MaxJobs) throw new InvalidOperationException("organizer_job_capacity");
        var job = new OrganizerJob { DocumentId = id };
        await SaveAsync(job, ct); jobs.Add(job);
    }
    private void CheckCapacity()
    {
        if (Directory.Exists(store.DirectoryPath) && Directory.EnumerateFiles(store.DirectoryPath, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length) >= options.MaxStateBytes)
            throw new InvalidOperationException("organizer_storage_capacity");
    }
}
