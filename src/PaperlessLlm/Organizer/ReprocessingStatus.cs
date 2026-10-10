namespace PaperlessLlm.Organizer;

public sealed class ReprocessingStatus(string directory)
{
    private readonly ReprocessingQueue queue = new(directory);
    public async Task<ReprocessingRunStatus> ReadAsync(string id, CancellationToken ct = default)
    {
        var request = await queue.GetAsync(id, ct);
        var progress = await queue.ProgressAsync(id, ct);
        var cancelled = await queue.CancelledAsync(id, ct);
        var checkpoint = await queue.Store.ReadAsync<OrganizerCheckpoint>(queue.Store.CheckpointPath, ct);
        var items = new List<ReprocessingItemStatus>();
        foreach (var item in request.Items)
            items.Add(WithPause(await ItemAsync(item, progress, cancelled, ct), checkpoint));
        return Summarize(request, items);
    }
    private static ReprocessingItemStatus WithPause(ReprocessingItemStatus item, OrganizerCheckpoint? checkpoint) =>
        checkpoint?.PauseReason is not null && item.State == "queued"
            ? item with { State = "blocked", Reason = checkpoint.PauseReason, NextAttemptAt = checkpoint.NextRunAt } : item;
    private async Task<ReprocessingItemStatus> ItemAsync(ReprocessingItem item, ReprocessingProgress progress, bool cancelled, CancellationToken ct)
    {
        var reason = item.SkipReason ?? progress.Skipped.GetValueOrDefault(item.DocumentId);
        if (reason is not null) return new(item.DocumentId, "skipped", reason);
        var job = await queue.Store.ReadAsync<OrganizerJob>(queue.Store.JobPath(item.DocumentId), ct);
        if (job?.JobId != item.JobId)
            job = await queue.Store.ReadAsync<OrganizerJob>(Path.Combine(queue.Store.DirectoryPath, "history", item.JobId + ".json"), ct);
        if (job is null) return new(item.DocumentId, cancelled ? "cancelled" : "queued");
        var state = JobState(job, cancelled);
        return new(item.DocumentId, state, job.ErrorCode ?? (state == "skipped" ? job.Outcome : null), job.NextAttemptAt, job.UpdatedAt, job.Decisions, job.Usage, OrganizerStatusReader.Phase(job));
    }
    private static string JobState(OrganizerJob job, bool cancelled)
    {
        if (cancelled && job.StartedAt is null) return "cancelled";
        return job.State switch
        {
            OrganizerJobState.Running => "running",
            OrganizerJobState.Failed => "failed",
            OrganizerJobState.RetryWaiting => "blocked",
            OrganizerJobState.Completed => job.Outcome == "applied" ? "applied" : job.Outcome == "no_change" ? "no_change" : "skipped",
            _ => "queued"
        };
    }
    private static ReprocessingRunStatus Summarize(ReprocessingRequest request, List<ReprocessingItemStatus> items)
    {
        int Count(string state) => items.Count(i => i.State == state);
        var phase = Phase(Count("running"), Count("queued"), Count("blocked"), Count("cancelled"));
        return new(request.RunId, request.SubmittedAt, phase, items.Count, Count("queued"), Count("running"),
            Count("applied"), Count("no_change"), Count("skipped"), Count("failed"), Count("blocked"), Count("cancelled"),
            items.Select(i => i.UpdatedAt).DefaultIfEmpty().Max() ?? request.SubmittedAt,
            items.Select(i => i.NextAttemptAt).DefaultIfEmpty().Min(), items);
    }
    private static string Phase(int running, int queued, int blocked, int cancelled) => running > 0 ? "processing"
        : queued > 0 ? "queued" : blocked > 0 ? "waiting" : cancelled > 0 ? "cancelled" : "completed";
}
