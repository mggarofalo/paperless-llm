using PaperlessLlm.Intent;
using PaperlessLlm.Paperless;
using System.Security.Cryptography;
using System.Text.Json;

namespace PaperlessLlm.Organizer;

public sealed class ReprocessingSelector(string directory, IPaperlessClient? paperless = null)
{
    private readonly OrganizerStore store = new(directory);
    public async Task<ReprocessingRequest> SelectAsync(ReprocessingSelection selection, CancellationToken ct = default)
    {
        if (selection.Limit is < 1 or > 10000) throw new ArgumentException("Selection limit must be between 1 and 10000.");
        var checkpoint = await store.ReadAsync<OrganizerCheckpoint>(store.CheckpointPath, ct)
            ?? throw new ArgumentException("Start the worker once before submitting reprocessing.");
        if (checkpoint.Version is not (1 or 2)) throw new ArgumentException("Unsupported organizer state version.");
        var jobs = (await store.JobsAsync(ct)).ToDictionary(j => j.DocumentId);
        var ids = selection.Ids?.Distinct().Order().ToArray() ?? jobs.Keys.Order().ToArray();
        if (selection.IncludeUnenrolled) ids = await IncludeVisibleAsync(ids, selection.Limit, ct);
        if (ids.Length > selection.Limit) throw new ArgumentException("Selection exceeds --limit; narrow the selection or raise the explicit limit.");
        var items = new List<ReprocessingItem>();
        foreach (var id in ids)
        {
            if (id < 1) throw new ArgumentException("Document IDs must be positive.");
            jobs.TryGetValue(id, out var job);
            var reason = await ReasonAsync(id, job, selection, ct);
            items.Add(new(id, job?.JobId, Guid.NewGuid().ToString("N"), reason));
        }
        return new(Guid.NewGuid().ToString("N"), checkpoint.SourceUrl, DateTimeOffset.UtcNow, selection.NotesOnly, items.ToArray(), Key(selection));
    }
    public static string Key(ReprocessingSelection selection) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(selection with { Ids = selection.Ids?.Distinct().Order().ToArray() })));
    private async Task<string?> ReasonAsync(int id, OrganizerJob? job, ReprocessingSelection selection, CancellationToken ct)
    {
        var reason = Eligibility(job, selection);
        return reason is null && selection.MissingSummary ? await SummaryReasonAsync(id, ct) : reason;
    }
    private static string? Eligibility(OrganizerJob? job, ReprocessingSelection selection)
    {
        if (job is null) return selection.IncludeUnenrolled && selection.ProcessedBefore is null ? null : "not_enrolled";
        if (job.State != OrganizerJobState.Completed) return "not_completed";
        if (selection.ProcessedBefore is { } before && (job.CompletedAt is null || job.CompletedAt >= before)) return "processed_after_cutoff";
        return null;
    }
    private async Task<string?> SummaryReasonAsync(int id, CancellationToken ct)
    {
        var client = paperless ?? throw new ArgumentException("Missing-summary selection requires Paperless read access.");
        try
        {
            var document = await client.GetDocumentAsync(id, ct);
            if (document.Notes is null) return "notes_unavailable";
            return DocumentNotes.HasSummary(document) ? "summary_exists" : null;
        }
        catch (PaperlessException e) when (e.Code == "paperless_not_found") { return "not_visible"; }
    }
    private async Task<int[]> IncludeVisibleAsync(int[] enrolled, int limit, CancellationToken ct)
    {
        var client = paperless ?? throw new ArgumentException("Historical enrollment requires Paperless read access.");
        var ids = enrolled.ToHashSet();
        var cursor = 0;
        while (true)
        {
            var page = await client.ListDocumentsAsync(null, 100, cursor, ct);
            if (page.Count == 0) break;
            if (page.Any(d => d.Id <= cursor)) throw new ArgumentException("Paperless pagination did not advance.");
            ids.UnionWith(page.Select(d => d.Id));
            if (ids.Count > limit) throw new ArgumentException("Visible library exceeds --limit; historical enrollment was not submitted.");
            cursor = page.Max(d => d.Id);
        }
        return ids.Order().ToArray();
    }
}
