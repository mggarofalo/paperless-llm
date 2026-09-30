using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaperlessLlm.Auth;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;

namespace PaperlessLlm.Worker;

/// <summary>Durable local jobs. Ready means private evidence exists, never that Paperless was edited.</summary>
public sealed class ReviewWorker : BackgroundService
{
    private readonly IPaperlessClient paperless;
    private readonly IProposalGenerator generator;
    private readonly WorkerOptions options;
    private readonly AuditWriter audit;
    private readonly ILogger<ReviewWorker> logger;
    private readonly IDocumentRenderer? renderer;
    private readonly CheckpointStore store;
    private readonly TimeProvider clock;
    private readonly string policy;

    public ReviewWorker(IPaperlessClient paperless, IProposalGenerator generator, WorkerOptions options,
        AuditWriter audit, ILogger<ReviewWorker> logger, IDocumentRenderer? renderer = null, TimeProvider? timeProvider = null)
    {
        options.Validate();
        this.paperless = paperless;
        this.generator = generator;
        this.options = options;
        this.audit = audit;
        this.logger = logger;
        this.renderer = renderer;
        clock = timeProvider ?? TimeProvider.System;
        store = new CheckpointStore(options.StateDirectory);
        policy = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"review-policy-v1|{options.Model}|{options.Tag}|visual:{renderer is not null}|{ProposalValidator.Schema.GetRawText()}")));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await RunOnceAsync(stoppingToken);
                logger.LogInformation("Poll completed: reviewed {Reviewed}, unchanged {Unchanged}, failed {Failed}", result.Reviewed, result.Unchanged, result.Failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Poll paused with code {Code}; inspect worker status and private storage", SafeCode(exception, "poll_failed")); }
            try { await Task.Delay(options.PollInterval, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<PollResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var stateLock = store.Lock();
        var state = await store.ReadAsync(cancellationToken);
        bool initialized = state is null;
        if (state is null)
        {
            var baseline = await paperless.GetLatestDocumentIdAsync(cancellationToken);
            var backfill = options.BackfillLimit == 0 ? [] : await paperless.ListDocumentsAsync(options.Tag, options.BackfillLimit, ct: cancellationToken);
            state = new Checkpoint { BaselineId = baseline, CursorId = baseline, Tag = options.Tag, SourceUrl = options.SourceUrl ?? "" };
            foreach (var doc in backfill.Where(d => d.Id <= baseline)) Discover(state, doc);
            await store.SaveAsync(state, cancellationToken);
        }
        if (state.Tag != options.Tag || state.SourceUrl != (options.SourceUrl ?? ""))
            throw new InvalidOperationException("checkpoint_scope_changed_use_new_state_directory");
        state.LastPollAt = clock.GetUtcNow();
        state.PauseReason = null;
        try { return await PollAsync(state, initialized, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            state.PauseReason = SafeCode(exception, "poll_failed");
            await store.SaveAsync(state, cancellationToken);
            throw;
        }
    }

    private async Task<PollResult> PollAsync(Checkpoint state, bool initialized, CancellationToken cancellationToken)
    {
        EnsureAuditCapacity();
        foreach (var job in state.Jobs.Values.Where(j => j.Status == JobStatus.Processing)) Fail(job, "interrupted", null);
        foreach (var job in state.Jobs.Values.Where(j => j.PolicyFingerprint != policy))
        {
            job.PolicyFingerprint = policy;
            job.Status = JobStatus.Pending;
            job.Attempts = 0;
            job.NextAttemptAt = null;
            job.ErrorCode = null;
            job.UpdatedAt = clock.GetUtcNow();
        }
        // Only explicitly enrolled historical IDs are reconciled. Never expand backfill on restart.
        var historical = state.Jobs.Keys.Where(id => id <= state.BaselineId && id > state.BackfillCursorId)
            .Order().Take(options.DiscoveryLimit).ToArray();
        foreach (var id in historical)
        {
            try { Discover(state, await paperless.GetDocumentAsync(id, cancellationToken), eligible: false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { logger.LogWarning("Historical document {DocumentId} could not be refreshed", id); }
            state.BackfillCursorId = id;
        }
        if (historical.Length == 0) state.BackfillCursorId = 0;
        // Discovery rotates independently of processing. Late review tags on post-baseline IDs are found.
        var discovered = await paperless.ListDocumentsAsync(options.Tag, options.DiscoveryLimit, state.CursorId, cancellationToken);
        if (discovered.Count > options.DiscoveryLimit || discovered.Any(d => d.Id <= state.CursorId) ||
            discovered.Select(d => d.Id).Distinct().Count() != discovered.Count)
            throw new InvalidOperationException("invalid_document_selection");
        int unchanged = 0;
        foreach (var doc in discovered)
        {
            if (!Discover(state, doc)) unchanged++;
            state.CursorId = Math.Max(state.CursorId, doc.Id);
        }
        if (discovered.Count == 0) state.CursorId = state.BaselineId;
        await store.SaveAsync(state, cancellationToken);
        if (state.AuthRetryAt > clock.GetUtcNow())
        {
            state.PauseReason = "authentication_required";
            await store.SaveAsync(state, cancellationToken);
            return new PollResult(0, unchanged, 0, initialized);
        }
        state.AuthRetryAt = null;
        var due = state.Jobs.Values.Where(j => j.Status is JobStatus.Pending or JobStatus.RetryWaiting or JobStatus.AuthPaused)
            .Where(j => j.NextAttemptAt is null || j.NextAttemptAt <= clock.GetUtcNow())
            .OrderBy(j => j.NextAttemptAt ?? DateTimeOffset.MinValue).ThenBy(j => j.UpdatedAt).ThenBy(j => j.DocumentId)
            .Take(options.BatchSize).ToArray();
        int reviewed = 0, failed = 0;
        foreach (var job in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAuditCapacity();
            job.Status = JobStatus.Processing;
            job.Attempts++;
            job.UpdatedAt = clock.GetUtcNow();
            await store.SaveAsync(state, cancellationToken);
            try
            {
                var document = await paperless.GetDocumentAsync(job.DocumentId, cancellationToken);
                if (document.Id != job.DocumentId) throw new InvalidOperationException("source_identity_changed");
                if (document.RevisionHash != job.RevisionHash)
                {
                    job.RevisionHash = document.RevisionHash;
                    job.Attempts = 1;
                }
                var result = await ReviewAsync(document, cancellationToken);
                job.AuditPath = result.AuditPath;
                job.UpdatedAt = clock.GetUtcNow();
                if (result.Outcome == "proposal_ready")
                {
                    job.Status = JobStatus.Ready;
                    job.ErrorCode = null;
                    job.NextAttemptAt = null;
                    state.AuthRetryAt = null;
                    state.LastSuccessAt = clock.GetUtcNow();
                    reviewed++;
                }
                else if (result.Outcome == "not_eligible")
                {
                    job.Status = JobStatus.NotEligible;
                    job.ErrorCode = null;
                    job.NextAttemptAt = null;
                }
                else { Fail(job, result.Outcome, result.AuditPath); failed++; }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (AuthException exception) when (exception.RequiresSignIn)
            {
                job.Attempts--;
                job.Status = JobStatus.AuthPaused;
                job.ErrorCode = "authentication_required";
                state.AuthRetryAt = clock.GetUtcNow().Add(options.PollInterval);
                state.PauseReason = "authentication_required";
                job.NextAttemptAt = state.AuthRetryAt;
                job.UpdatedAt = clock.GetUtcNow();
                await store.SaveAsync(state, cancellationToken);
                logger.LogWarning("Authentication requires attention; document processing is paused");
                break;
            }
            catch (Exception exception)
            {
                Fail(job, SafeCode(exception, "review_failed"), null);
                failed++;
                logger.LogWarning("Document {DocumentId} could not be reviewed", job.DocumentId);
            }
            await store.SaveAsync(state, cancellationToken);
        }
        await store.SaveAsync(state, cancellationToken);
        return new PollResult(reviewed, unchanged, failed, initialized);
    }

    private bool Discover(Checkpoint state, PaperlessDocument document, bool eligible = true)
    {
        if (state.Jobs.TryGetValue(document.Id, out var existing) && existing.RevisionHash == document.RevisionHash &&
            existing.PolicyFingerprint == policy && (!eligible || existing.Status != JobStatus.NotEligible)) return false;
        if (!state.Jobs.ContainsKey(document.Id) && state.Jobs.Count >= options.MaxJobs) throw new CapacityException("job_capacity_reached");
        state.Jobs[document.Id] = new ReviewJob { DocumentId = document.Id, RevisionHash = document.RevisionHash,
            PolicyFingerprint = policy, Status = JobStatus.Pending, UpdatedAt = clock.GetUtcNow() };
        logger.LogInformation("Document {DocumentId} has a pending local review job", document.Id);
        return true;
    }

    private void Fail(ReviewJob job, string code, string? auditPath)
    {
        job.Status = job.Attempts >= options.MaxAttempts ? JobStatus.Failed : JobStatus.RetryWaiting;
        job.ErrorCode = code;
        job.AuditPath = auditPath;
        job.UpdatedAt = clock.GetUtcNow();
        job.NextAttemptAt = job.Status == JobStatus.Failed ? null : clock.GetUtcNow().AddSeconds(
            Math.Min(3600, options.RetryBaseDelay.TotalSeconds * Math.Pow(2, Math.Max(0, job.Attempts - 1))));
        logger.LogWarning("Document {DocumentId} review state {Status}, code {Code}, attempt {Attempt}", job.DocumentId, job.Status, code, job.Attempts);
    }

    private sealed record ReviewOutcome(string Outcome, string? AuditPath);

    private async Task<ReviewOutcome> ReviewAsync(PaperlessDocument document, CancellationToken cancellationToken)
    {
        var working = Path.Combine(audit.DirectoryPath, ".working-" + Guid.NewGuid().ToString("N"));
        AuditWriter.PrivateDirectory(working);
        PaperlessTaxonomy? taxonomy = null;
        string? prompt = null, response = null;
        bool visual = false;
        List<AuditAttachment> attachments = [];
        try
        {
            taxonomy = await paperless.GetTaxonomyAsync(cancellationToken);
            var eligible = taxonomy.Tags.Where(t => t.Name.Equals(options.Tag, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (eligible.Length != 1) throw new InvalidOperationException("ambiguous_review_tag");
            if (!document.Tags.Contains(eligible[0].Id)) return new ReviewOutcome("not_eligible", null);
            List<string> images = [];
            if (renderer is not null)
            {
                var original = await paperless.DownloadOriginalAsync(document.Id, Path.Combine(working, "original"), cancellationToken);
                attachments.Add(new AuditAttachment(original.Path, "original"));
                var pages = await renderer.RenderAsync(original, Path.Combine(working, "pages"), cancellationToken);
                if (pages.Count == 0 || pages.Count > 100 || pages.Select(p => p.PageNumber).Distinct().Count() != pages.Count)
                    throw new InvalidOperationException("invalid_rendered_pages");
                long totalBytes = 0;
                foreach (var page in pages.OrderBy(p => p.PageNumber))
                {
                    if (page.MediaType is not ("image/png" or "image/jpeg" or "image/webp")) throw new InvalidOperationException("invalid_page_media_type");
                    totalBytes += new FileInfo(page.Path).Length;
                    if (totalBytes > 28 * 1024 * 1024) throw new InvalidOperationException("rendered_input_too_large");
                    var data = await File.ReadAllBytesAsync(page.Path, cancellationToken);
                    images.Add($"data:{page.MediaType};base64,{Convert.ToBase64String(data)}");
                    var extension = page.MediaType == "image/png" ? "png" : page.MediaType == "image/jpeg" ? "jpg" : "webp";
                    attachments.Add(new AuditAttachment(page.Path, $"page-{page.PageNumber}.{extension}"));
                }
                visual = true;
            }
            prompt = ProposalPrompt.Build(document, taxonomy, visual);
            response = await generator.GenerateAsync(options.Model, prompt, images, cancellationToken);
            var proposal = ProposalValidator.Validate(response, document, taxonomy, visual);
            var current = await paperless.GetDocumentAsync(document.Id, cancellationToken);
            var unchanged = current.Id == document.Id && current.RevisionHash == document.RevisionHash;
            var outcome = unchanged ? "proposal_ready" : "source_changed";
            var path = await audit.WriteAsync(new AuditRecord(clock.GetUtcNow(), outcome, options.Model, document, taxonomy,
                visual, prompt, response, proposal, unchanged ? null : "source_changed", options.SourceUrl), attachments, cancellationToken);
            logger.LogInformation("Document {DocumentId} review outcome {Outcome}", document.Id, outcome);
            return new ReviewOutcome(outcome, path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AuthException exception) when (exception.RequiresSignIn) { throw; }
        catch (Exception exception)
        {
            var code = SafeCode(exception, "review_failed");
            var path = await audit.WriteAsync(new AuditRecord(clock.GetUtcNow(), "review_failed", options.Model, document,
                taxonomy, visual, prompt, response, null, code, options.SourceUrl), attachments, cancellationToken);
            logger.LogWarning("Document {DocumentId} review failed with code {Code}", document.Id, code);
            return new ReviewOutcome(code, path);
        }
        finally { if (Directory.Exists(working)) Directory.Delete(working, recursive: true); }
    }

    private sealed class CapacityException(string code) : Exception(code) { public string Code { get; } = code; }

    private static string SafeCode(Exception exception, string fallback) => exception switch
    {
        ProposalValidationException invalid => invalid.Code,
        PaperlessException remote => remote.Code,
        CapacityException capacity => capacity.Code,
        _ => fallback
    };

    private void EnsureAuditCapacity()
    {
        if (!Directory.Exists(audit.DirectoryPath)) return;
        long bytes = 0;
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var file in Directory.EnumerateFiles(audit.DirectoryPath, "*", enumeration))
        {
            bytes += new FileInfo(file).Length;
            if (bytes >= options.MaxAuditBytes) throw new CapacityException("audit_capacity_reached");
        }
    }
}
