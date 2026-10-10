namespace PaperlessLlm.Organizer;

public sealed record ReprocessingSelection(int[]? Ids = null, DateTimeOffset? ProcessedBefore = null,
    bool MissingSummary = false, bool IncludeUnenrolled = false, int Limit = 10000, bool NotesOnly = false);
public sealed record ReprocessingItem(int DocumentId, string? PreviousJobId, string JobId, string? SkipReason = null);
public sealed record ReprocessingRequest(string RunId, string SourceUrl, DateTimeOffset SubmittedAt,
    bool NotesOnly, ReprocessingItem[] Items, string? SelectionKey = null);
internal sealed record ReprocessingControl(bool Cancelled);
internal sealed class ReprocessingProgress
{
    public Dictionary<int, string> Skipped { get; set; } = [];
}
public sealed record ReprocessingItemStatus(int DocumentId, string State, string? Reason = null,
    DateTimeOffset? NextAttemptAt = null, DateTimeOffset? UpdatedAt = null,
    DecisionSummary? Decisions = null, ProviderUsage? Usage = null, string? Phase = null);
public sealed record ReprocessingRunStatus(string RunId, DateTimeOffset SubmittedAt, string Phase,
    int Selected, int Queued, int Running, int Applied, int NoChange, int Skipped, int Failed,
    int Blocked, int Cancelled, DateTimeOffset? LastActivityAt, DateTimeOffset? NextAttemptAt,
    IReadOnlyList<ReprocessingItemStatus> Items);
public sealed record ProviderUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens);
public sealed record IntentResult(string Text, ProviderUsage? Usage = null);
// The model's free-text uncertainty is private. Counts do not claim field-level attribution.
public sealed record DecisionSummary(int KeptFields, int ProposedFields, int UncertaintyCount,
    IReadOnlyDictionary<string, string>? Fields = null);
