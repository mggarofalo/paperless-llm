using System.Text.Json.Serialization;

namespace PaperlessLlm.Worker;

[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus { Pending, Processing, Ready, RetryWaiting, Failed, AuthPaused, NotEligible }

public sealed class ReviewJob
{
    [JsonRequired] public int DocumentId { get; set; }
    [JsonRequired] public string RevisionHash { get; set; } = "";
    [JsonRequired] public string PolicyFingerprint { get; set; } = "";
    [JsonRequired] public JobStatus Status { get; set; }
    [JsonRequired] public int Attempts { get; set; }
    [JsonRequired] public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? AuditPath { get; set; }
}

public sealed record WorkerStatus(bool Initialized, int? BaselineId, bool AuthenticationPaused,
    DateTimeOffset? AuthenticationRetryAt, DateTimeOffset? LastPollAt, DateTimeOffset? LastSuccessAt,
    string? PauseReason, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<ReviewJob> Jobs);

public static class WorkerStatusReader
{
    /// <summary>Reads the atomically published checkpoint without contacting either service.</summary>
    public static async Task<WorkerStatus> ReadAsync(string stateDirectory, CancellationToken cancellationToken = default)
    {
        var state = await new CheckpointStore(stateDirectory).ReadAsync(cancellationToken);
        if (state is null) return new WorkerStatus(false, null, false, null, null, null, null, new Dictionary<string, int>(), []);
        return new WorkerStatus(true, state.BaselineId, state.AuthRetryAt.HasValue, state.AuthRetryAt,
            state.LastPollAt, state.LastSuccessAt, state.PauseReason,
            state.Jobs.Values.GroupBy(j => j.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            state.Jobs.Values.OrderBy(j => j.DocumentId).ToArray());
    }

    /// <summary>Explicit local retry only; stop the worker first. Never alters the baseline or remote data.</summary>
    public static async Task RetryFailedAsync(string stateDirectory, int documentId, CancellationToken cancellationToken = default)
    {
        if (documentId <= 0) throw new ArgumentException("invalid_document_id");
        var store = new CheckpointStore(stateDirectory);
        using var stateLock = store.Lock();
        var state = await store.ReadAsync(cancellationToken) ?? throw new InvalidOperationException("worker_not_initialized");
        if (!state.Jobs.TryGetValue(documentId, out var job) || job.Status != JobStatus.Failed)
            throw new InvalidOperationException("document_has_no_terminal_failed_job");
        job.Status = JobStatus.Pending;
        job.Attempts = 0;
        job.NextAttemptAt = null;
        job.ErrorCode = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(state, cancellationToken);
    }
}
