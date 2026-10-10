using System.Text.Json;
using PaperlessLlm.Paperless;
namespace PaperlessLlm.Organizer;

public interface IIntentRunner
{
    Task<string> GenerateAsync(string model, string instructions, string prompt, IReadOnlyList<string> imageDataUrls, JsonElement schema, CancellationToken ct);
    async Task<IntentResult> GenerateWithUsageAsync(string model, string instructions, string prompt, IReadOnlyList<string> images, JsonElement schema, CancellationToken ct)
        => new(await GenerateAsync(model, instructions, prompt, images, schema, ct));
}
public interface IIntentSynchronizer
{
    Task<SyncResult> ApplyAsync(string jobId, PaperlessDocument source, JsonElement intent, int pageCount, CancellationToken ct);
}
public sealed record SyncResult(string Outcome, PaperlessDocument Document) { }
public sealed class SyncConflictException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed record IntentContext(string Instructions, string Prompt, JsonElement Schema, string PolicyVersion, bool NamedOutput = false, string? PromptSha256 = null) { }
public interface IIntentContextBuilder
{
    Task<IntentContext> BuildAsync(PaperlessDocument source, PaperlessTaxonomy taxonomy, int pageCount, CancellationToken ct);
}
public sealed class OrganizerOptions
{
    public required string StateDirectory { get; init; }
    public required string SourceUrl { get; init; }
    public string Model { get; init; } = "gpt-6-sol";
    public bool UseExistingOcr { get; init; }
    public string ReviewTag { get; init; } = "needs review";
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromHours(1);
    public int BatchSize { get; init; } = 5;
    public int BackfillLimit { get; init; }
    public int DiscoveryLimit { get; init; } = 100;
    public int MaxJobs { get; init; } = 10000;
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMinutes(1);
    public long MaxStateBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    internal void Validate()
    {
        ValidateIdentity();
        ValidateSchedule();
        ValidateCapacity();
    }
    private void ValidateIdentity()
    {
        if (string.IsNullOrWhiteSpace(StateDirectory) || string.IsNullOrWhiteSpace(SourceUrl) || string.IsNullOrWhiteSpace(Model))
            throw new ArgumentException("invalid_organizer_configuration");
    }
    private void ValidateSchedule()
    {
        if (PollInterval < TimeSpan.FromSeconds(1) || PollInterval > TimeSpan.FromDays(1) ||
            MaxAttempts is < 1 or > 10 || RetryBaseDelay < TimeSpan.FromSeconds(1))
            throw new ArgumentException("invalid_organizer_configuration");
    }
    private void ValidateCapacity()
    {
        if (BatchSize is < 1 or > 100 || BackfillLimit is < 0 or > 100 || DiscoveryLimit is < 1 or > 100 ||
            MaxJobs is < 1 or > 100000 || MaxStateBytes < 1)
            throw new ArgumentException("invalid_organizer_configuration");
    }
}
public enum OrganizerJobState { Pending, Running, RetryWaiting, Completed, Failed }
public sealed class OrganizerJob
{
    public string? RunId { get; set; }
    public bool NotesOnly { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DecisionSummary? Decisions { get; set; }
    public ProviderUsage? Usage { get; set; }
    public int DocumentId { get; set; }
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public OrganizerJobState State { get; set; }
    public int Attempts { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public PaperlessDocument? Source { get; set; }
    public JsonElement? Intent { get; set; }
    public int PageCount { get; set; }
    public string? OriginalSha256 { get; set; }
    public string? PolicyVersion { get; set; }
    public string? Model { get; set; }
    public IReadOnlyList<RenderedPage>? RenderedPages { get; set; }
    public string? Outcome { get; set; }
    public PaperlessDocument? After { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? InferenceMilliseconds { get; set; }
    public long? SyncMilliseconds { get; set; }
}
public sealed record OrganizerPollResult(int Completed, int Failed, bool Initialized) { }
public sealed record OrganizerJobSummary(int DocumentId, string JobId, OrganizerJobState State, int Attempts, string? ErrorCode, DateTimeOffset? NextAttemptAt, string? Outcome,
    DateTimeOffset CreatedAt = default, DateTimeOffset? UpdatedAt = null, DateTimeOffset? CompletedAt = null,
    long? InferenceMilliseconds = null, long? SyncMilliseconds = null, string? RunId = null,
    DecisionSummary? Decisions = null, ProviderUsage? Usage = null, string? Phase = null)
{ }
public sealed record OrganizerStatus(int BaselineId, int CursorId, DateTimeOffset? LastPollAt, IReadOnlyList<OrganizerJobSummary> Jobs, DateTimeOffset? LastActivityAt = null, DateTimeOffset? NextRunAt = null, string? PauseReason = null)
{
    public int QueueDepth => Jobs.Count(j => j.State is OrganizerJobState.Pending or OrganizerJobState.RetryWaiting);
    public DateTimeOffset? OldestQueuedAt => Jobs.Where(j => j.State is OrganizerJobState.Pending or OrganizerJobState.RetryWaiting)
        .Select(j => (DateTimeOffset?)j.CreatedAt).DefaultIfEmpty().Min();
    public DateTimeOffset? LastSuccessfulDiscoveryAt => LastPollAt;
    public DateTimeOffset? LastSuccessfulSyncAt => Jobs.Select(j => j.CompletedAt).DefaultIfEmpty().Max();
    public string Phase => PauseReason is not null ? "paused" : Jobs.FirstOrDefault(j => j.State == OrganizerJobState.Running)?.Phase ?? "waiting";
}
