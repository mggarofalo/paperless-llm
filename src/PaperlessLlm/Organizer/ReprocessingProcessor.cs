using PaperlessLlm.Review;

namespace PaperlessLlm.Organizer;

internal sealed class ReprocessingProcessor(ReprocessingQueue queue)
{
    // Called under the worker lock. The immutable planned job ID is the crash-recovery key.
    public async Task MaterializeAsync(List<OrganizerJob> jobs, OrganizerOptions options, CancellationToken ct)
    {
        var remaining = options.BatchSize;
        foreach (var request in await queue.ListAsync(ct))
        {
            if (request.SourceUrl != options.SourceUrl) throw new InvalidOperationException("reprocessing_scope_changed");
            using var held = await queue.LockAsync(request.RunId, ct);
            if (await queue.CancelledAsync(request.RunId, ct)) continue;
            var progress = await queue.ProgressAsync(request.RunId, ct);
            foreach (var item in request.Items)
            {
                if (item.SkipReason is not null || progress.Skipped.ContainsKey(item.DocumentId)) continue;
                var current = jobs.SingleOrDefault(j => j.DocumentId == item.DocumentId);
                if (AlreadyMaterialized(item, current)) continue;
                var reason = Conflict(item, current);
                if (reason is not null) progress.Skipped[item.DocumentId] = reason;
                else if (remaining > 0)
                {
                    CheckCapacity(current, jobs.Count, options.MaxJobs);
                    await ReplaceAsync(request, item, current, jobs, ct);
                    remaining--;
                }
            }
            await queue.Store.SaveAsync(queue.PathFor(request.RunId, "progress"), progress, ct);
        }
    }
    private bool AlreadyMaterialized(ReprocessingItem item, OrganizerJob? current) =>
        current?.JobId == item.JobId || File.Exists(HistoryPath(item.JobId));
    private static void CheckCapacity(OrganizerJob? current, int count, int max)
    {
        if (current is null && count >= max) throw new InvalidOperationException("organizer_job_capacity");
    }
    public async Task<List<OrganizerJob>> EligibleAsync(List<OrganizerJob> jobs, CancellationToken ct)
    {
        var cancelled = new HashSet<string>();
        foreach (var id in jobs.Where(j => j.RunId is not null).Select(j => j.RunId!).Distinct())
            if (await queue.CancelledAsync(id, ct)) cancelled.Add(id);
        return jobs.Where(j => j.RunId is null || j.StartedAt is not null || !cancelled.Contains(j.RunId)).ToList();
    }
    private static string? Conflict(ReprocessingItem item, OrganizerJob? current)
    {
        if (current?.JobId != item.PreviousJobId) return "selection_changed";
        if (current is not null && current.State != OrganizerJobState.Completed) return "not_completed";
        return null;
    }
    private string HistoryPath(string id) => Path.Combine(queue.Store.DirectoryPath, "history", id + ".json");
    private async Task ReplaceAsync(ReprocessingRequest request, ReprocessingItem item, OrganizerJob? current,
        List<OrganizerJob> jobs, CancellationToken ct)
    {
        if (current is not null)
        {
            AuditWriter.PrivateDirectory(Path.GetDirectoryName(HistoryPath(current.JobId))!);
            await queue.Store.SaveAsync(HistoryPath(current.JobId), current, ct);
        }
        var job = new OrganizerJob { DocumentId = item.DocumentId, JobId = item.JobId, RunId = request.RunId,
            NotesOnly = request.NotesOnly, CreatedAt = request.SubmittedAt };
        await queue.Store.SaveAsync(queue.Store.JobPath(job.DocumentId), job, ct);
        if (current is not null) jobs.Remove(current);
        jobs.Add(job);
    }
    public async Task<bool> StartAsync(OrganizerJob job, DateTimeOffset now, CancellationToken ct)
    {
        using var held = job.RunId is null ? null : await queue.LockAsync(job.RunId, ct);
        if (job.RunId is not null && job.StartedAt is null && await queue.CancelledAsync(job.RunId, ct)) return false;
        job.State = OrganizerJobState.Running; job.Attempts++; job.ErrorCode = null;
        job.StartedAt ??= now; job.UpdatedAt = now;
        await queue.Store.SaveAsync(queue.Store.JobPath(job.DocumentId), job, ct);
        return true;
    }
}
