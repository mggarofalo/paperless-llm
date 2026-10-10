using System.Text.Json;
using System.Diagnostics;
using PaperlessLlm.Auth;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;
using PaperlessLlm.Runner;
using PaperlessLlm.Intent;
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
    private readonly ReprocessingProcessor requests;
    private readonly ReprocessingSchedule schedule;
    public OrganizerWorker(IPaperlessClient paperless, IIntentRunner runner, IIntentContextBuilder context,
        IIntentSynchronizer synchronizer, IDocumentRenderer renderer, OrganizerOptions options,
        ILogger<OrganizerWorker> logger, TimeProvider? clock = null)
    {
        options.Validate();
        this.paperless = paperless; this.runner = runner; this.context = context; this.synchronizer = synchronizer;
        this.renderer = renderer; this.options = options; this.logger = logger; this.clock = clock ?? TimeProvider.System;
        store = new(options.StateDirectory);
        requests = new(new(options.StateDirectory));
        schedule = new(options.StateDirectory, this.clock);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var signal = schedule.Signal();
            try
            {
                var result = await RunOnceAsync(stoppingToken, scheduled: true);
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
            try { await WaitForWorkAsync(signal, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    private async Task WaitForWorkAsync(string signal, CancellationToken ct)
    {
        var state = await store.ReadAsync<OrganizerCheckpoint>(store.CheckpointPath, ct);
        await schedule.WaitAsync(state?.NextRunAt ?? clock.GetUtcNow() + options.PollInterval,
            signal, state?.PauseReason is not null, ct);
    }
    public async Task<OrganizerPollResult> RunOnceAsync(CancellationToken ct = default, bool scheduled = false)
    {
        using var stateLock = store.Lock();
        CheckCapacity();
        if (File.Exists(Path.Combine(store.DirectoryPath, "checkpoint.json")))
            throw new InvalidOperationException("legacy_review_state_use_new_organizer_directory");
        var state = await store.ReadAsync<OrganizerCheckpoint>(store.CheckpointPath, ct);
        if (scheduled && IsPaused(state)) return new(0, 0, false);
        var taxonomy = await paperless.GetTaxonomyAsync(ct);
        SetupValidation.RequireReviewTag(taxonomy, options.ReviewTag);
        var initialized = state is null;
        var jobs = await store.JobsAsync(ct);
        state ??= await InitializeAsync(jobs, ct);
        await PrepareCycleAsync(state, jobs, scheduled, ct);
        var eligible = await requests.EligibleAsync(jobs, ct);
        int completed = 0, failed = 0;
        foreach (var job in FairOrder(eligible.Where(IsDue), state).Take(options.BatchSize))
        {
            CheckCapacity();
            if (!await requests.StartAsync(job, clock.GetUtcNow(), ct)) continue;
            await HeartbeatAsync(state, ct);
            logger.LogInformation("Organizer job {JobId} document {DocumentId} started attempt {Attempt}", job.JobId, job.DocumentId, job.Attempts);
            try
            {
                if (job.Intent is null) await GenerateIntentAsync(job, taxonomy, state, ct);
                await SyncJobAsync(job, state, ct);
                completed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) when (PauseCode(exception) is not null)
            {
                await PauseAsync(job, state, PauseCode(exception)!, ct);
                break; // Shared service failure must not exhaust every queued document.
            }
            catch (Exception exception)
            {
                await FailJobAsync(job, exception, ct);
                failed++;
            }
        }
        await SetNextRunAsync(state, ct);
        await HeartbeatAsync(state, ct);
        return new(completed, failed, initialized);
    }
    private bool IsPaused(OrganizerCheckpoint? state) => state?.PauseReason is not null && state.NextRunAt > clock.GetUtcNow();
    private async Task PrepareCycleAsync(OrganizerCheckpoint state, List<OrganizerJob> jobs, bool scheduled, CancellationToken ct)
    {
        if (state.Version is not (1 or 2) || state.SourceUrl != options.SourceUrl || state.CursorId < state.BaselineId)
            throw new InvalidOperationException("organizer_scope_changed_or_invalid");
        await RecoverAsync(jobs, ct);
        if (!scheduled || state.LastPollAt is null || state.LastPollAt + options.PollInterval <= clock.GetUtcNow())
            await DiscoverAsync(state, jobs, ct);
        state.PauseReason = null;
        await GuardManualStateAsync(state, ct);
        await requests.MaterializeAsync(jobs, options, ct);
    }
    private async Task GuardManualStateAsync(OrganizerCheckpoint state, CancellationToken ct)
    {
        if (state.Version == 2) return;
        // Older workers must not ignore NotesOnly/RunId on materialized jobs after a downgrade.
        state.Version = 2;
        await store.SaveAsync(store.CheckpointPath, state, ct);
    }
    private async Task SetNextRunAsync(OrganizerCheckpoint state, CancellationToken ct)
    {
        state.NextRunAt = clock.GetUtcNow() + options.PollInterval;
        if (state.PauseReason is null)
        {
            var discovery = (state.LastPollAt ?? clock.GetUtcNow()) + options.PollInterval;
            state.NextRunAt = await schedule.NextAsync(discovery, ct);
        }
    }
    private bool IsDue(OrganizerJob job) => job.State == OrganizerJobState.Pending ||
        job.State == OrganizerJobState.RetryWaiting && job.NextAttemptAt <= clock.GetUtcNow();

    private static IEnumerable<OrganizerJob> FairOrder(IEnumerable<OrganizerJob> jobs, OrganizerCheckpoint state)
    {
        var normal = new Queue<OrganizerJob>(jobs.Where(j => j.RunId is null).OrderBy(j => j.CreatedAt));
        var manual = new Queue<OrganizerJob>(jobs.Where(j => j.RunId is not null).OrderBy(j => j.CreatedAt));
        while (normal.Count + manual.Count > 0)
        {
            var selected = state.ManualNext ? manual : normal;
            if (selected.Count == 0) selected = state.ManualNext ? normal : manual;
            state.ManualNext = !state.ManualNext;
            yield return selected.Dequeue();
        }
    }

    private async Task<OrganizerCheckpoint> InitializeAsync(List<OrganizerJob> jobs, CancellationToken ct)
    {
        OrganizerCheckpoint? state;
        var bootstrapPath = Path.Combine(store.DirectoryPath, "organizer-bootstrap.json");
        state = await store.ReadAsync<OrganizerCheckpoint>(bootstrapPath, ct);
        if (state is null)
        {
            var baseline = await paperless.GetLatestDocumentIdAsync(ct);
            var backfill = options.BackfillLimit > 0
                ? await paperless.ListDocumentsAsync(null, options.BackfillLimit, ct: ct) : [];
            state = new()
            {
                BaselineId = baseline,
                CursorId = baseline,
                SourceUrl = options.SourceUrl,
                InitialBackfillIds = backfill.Where(d => d.Id <= baseline).Select(d => d.Id).Distinct().ToArray()
            };
            // Keep the original baseline and explicit enrollment set if initialization crashes.
            await store.SaveAsync(bootstrapPath, state, ct);
        }
        if (state.SourceUrl != options.SourceUrl) throw new InvalidOperationException("organizer_scope_changed_or_invalid");
        foreach (var id in state.InitialBackfillIds) await EnrollAsync(id, jobs, ct);
        // Jobs are durable before checkpoint advances, including initialization.
        await store.SaveAsync(store.CheckpointPath, state, ct);
        return state;
    }

    private async Task RecoverAsync(List<OrganizerJob> jobs, CancellationToken ct)
    {
        foreach (var job in jobs.Where(j => j.State == OrganizerJobState.Running))
        {
            job.State = OrganizerJobState.Pending; // Saved intent resumes sync with the same idempotency key.
            await SaveAsync(job, ct);
        }
    }
    private async Task DiscoverAsync(OrganizerCheckpoint state, List<OrganizerJob> jobs, CancellationToken ct)
    {
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
    }

    private async Task GenerateIntentAsync(OrganizerJob job, PaperlessTaxonomy taxonomy, OrganizerCheckpoint state, CancellationToken ct)
    {
        job.Source = await paperless.GetDocumentAsync(job.DocumentId, ct);
        var attemptPath = Path.Combine(store.DirectoryPath, "evidence", job.JobId,
            job.Attempts + "-" + Guid.NewGuid().ToString("N"));
        AuditWriter.PrivateDirectory(attemptPath);
        string? originalHash = null;
        IReadOnlyList<RenderedPage> pages = [];
        if (!options.UseExistingOcr)
        {
            var original = await paperless.DownloadOriginalAsync(job.DocumentId, Path.Combine(attemptPath, "original"), ct);
            pages = await renderer.RenderAsync(original, Path.Combine(attemptPath, "pages"), ct);
            originalHash = original.Sha256;
            if (pages.Count is < 1 or > 100) throw new InvalidOperationException("invalid_rendered_pages");
        }
        await HeartbeatAsync(state, ct);
        var input = await context.BuildAsync(job.Source, taxonomy, pages.Count, ct);
        await AuditWriter.WritePrivateAsync(Path.Combine(attemptPath, "request.json"),
            JsonSerializer.Serialize(new
            {
                Model = options.Model,
                Context = input,
                Source = job.Source,
                Taxonomy = taxonomy,
                OriginalSha256 = originalHash,
                Pages = pages
            }), ct);
        logger.LogInformation("Organizer job {JobId} policy {PolicyVersion} prompt {PromptSha256}",
            job.JobId, input.PolicyVersion, input.PromptSha256);
        var images = new List<string>();
        foreach (var page in pages)
            images.Add($"data:{page.MediaType};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(page.Path, ct))}");
        var inferenceTimer = Stopwatch.StartNew();
        var generated = await runner.GenerateWithUsageAsync(options.Model, input.Instructions, input.Prompt, images, input.Schema, ct);
        var raw = generated.Text;
        job.Usage = generated.Usage;
        job.InferenceMilliseconds = inferenceTimer.ElapsedMilliseconds;
        if (raw.Length > 2 * 1024 * 1024) throw new InvalidOperationException("intent_too_large");
        await AuditWriter.WritePrivateAsync(Path.Combine(attemptPath, "response.txt"), raw, ct);
        using var parsed = JsonDocument.Parse(input.NamedOutput
            ? NamedIntentContract.Resolve(raw, job.Source, taxonomy, pages.Count) : raw);
        job.Decisions = IntentDiagnostics.Summarize(parsed.RootElement, input.NamedOutput ? raw : null);
        job.Intent = job.NotesOnly ? IntentDiagnostics.NotesOnly(IntentValidator.Validate(parsed.RootElement.GetRawText(), job.Source, taxonomy, pages.Count)) : parsed.RootElement.Clone();
        job.PolicyVersion = input.PolicyVersion;
        job.PageCount = pages.Count; job.OriginalSha256 = originalHash;
        job.Model = options.Model; job.RenderedPages = pages;
        await SaveAsync(job, ct); // Never repeat inference merely because sync was interrupted.
    }

    private async Task SyncJobAsync(OrganizerJob job, OrganizerCheckpoint state, CancellationToken ct)
    {
        await HeartbeatAsync(state, ct);
        var syncTimer = Stopwatch.StartNew();
        // Do not replay legacy OCR writes under the metadata-only policy. Preserve their
        // journal and intent for inspection; never alter an existing operation's identity.
        if (options.UseExistingOcr && job.Intent!.Value.GetProperty("ocr").GetProperty("action").GetString() != "keep")
            throw new SyncConflictException("legacy_ocr_intent_requires_inspection");
        var result = await synchronizer.ApplyAsync(job.JobId, job.Source!, job.Intent!.Value, job.PageCount, ct);
        job.SyncMilliseconds = syncTimer.ElapsedMilliseconds;
        job.After = result.Document; job.Outcome = result.Outcome; job.State = OrganizerJobState.Completed;
        job.CompletedAt = clock.GetUtcNow();
        job.NextAttemptAt = null;
        await SaveAsync(job, ct);
        logger.LogInformation("Organizer job {JobId} document {DocumentId} completed with {Outcome}; inference {InferenceMilliseconds}ms sync {SyncMilliseconds}ms",
            job.JobId, job.DocumentId, job.Outcome, job.InferenceMilliseconds, job.SyncMilliseconds);
    }

    private async Task FailJobAsync(OrganizerJob job, Exception exception, CancellationToken ct)
    {
        if (exception is ProposalValidationException) job.Intent = null;
        // Raw provider exceptions may contain prompts, OCR or credentials; persist only fixed codes.
        bool conflict = exception is SyncConflictException;
        job.ErrorCode = FailureCode(job, exception);
        job.State = conflict || job.Attempts >= options.MaxAttempts ? OrganizerJobState.Failed : OrganizerJobState.RetryWaiting;
        job.NextAttemptAt = job.State == OrganizerJobState.RetryWaiting
            ? clock.GetUtcNow() + TimeSpan.FromTicks(options.RetryBaseDelay.Ticks * (1L << Math.Min(job.Attempts - 1, 9))) : null;
        await SaveAsync(job, ct);
        logger.LogWarning("Organizer job {JobId} document {DocumentId} stopped with {Code}", job.JobId, job.DocumentId, job.ErrorCode);
    }
    private static string FailureCode(OrganizerJob job, Exception exception) => exception switch
    {
        SyncConflictException => "sync_conflict",
        PaperlessException { Code: "paperless_not_found" } => "document_not_visible",
        _ => job.Intent is null ? "inference_failed" : "sync_failed"
    };

    private static string? PauseCode(Exception exception) => exception switch
    {
        AuthException auth => auth.RequiresSignIn ? "auth_required" : "auth_unavailable",
        PaperlessException { Code: "paperless_authentication_failed" or "paperless_rate_limited" } paperless => paperless.Code,
        OrganizationPromptException => "organization_prompt_unreadable_or_invalid",
        RunnerRateLimitException => "rate_limited",
        _ => null
    };

    private async Task PauseAsync(OrganizerJob job, OrganizerCheckpoint state, string code, CancellationToken ct)
    {
        job.Attempts--; job.State = OrganizerJobState.RetryWaiting;
        job.ErrorCode = code; job.NextAttemptAt = clock.GetUtcNow() + options.PollInterval;
        state.PauseReason = code;
        await SaveAsync(job, ct);
        logger.LogWarning("Organizer paused until next poll: {Code}", code);
    }

    /// <summary>Explicit retry preserves a proposal for idempotent sync unless regenerate is deliberately selected.</summary>
    public async Task RetryAsync(int documentId, bool regenerate = false, CancellationToken ct = default)
    {
        using var stateLock = store.Lock();
        var job = await store.ReadAsync<OrganizerJob>(store.JobPath(documentId), ct) ?? throw new InvalidOperationException("job_not_found");
        if (regenerate)
        {
            if (job.State != OrganizerJobState.Completed) throw new ArgumentException("Only completed jobs can be reprocessed; retry unresolved jobs with their saved intent.");
            // Preserve the previous job's evidence before replacing its current index entry.
            var history = Path.Combine(store.DirectoryPath, "history");
            AuditWriter.PrivateDirectory(history);
            await store.SaveAsync(Path.Combine(history, job.JobId + ".json"), job, ct);
            job.Intent = null; job.Source = null; job.After = null; job.JobId = Guid.NewGuid().ToString("N");
            job.CreatedAt = clock.GetUtcNow(); job.CompletedAt = null; job.Outcome = null;
            job.InferenceMilliseconds = null; job.SyncMilliseconds = null;
            job.RunId = null; job.NotesOnly = false; job.StartedAt = null; job.Decisions = null; job.Usage = null;
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
