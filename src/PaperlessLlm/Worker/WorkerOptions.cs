namespace PaperlessLlm.Worker;

public sealed class WorkerOptions
{
    public required string StateDirectory { get; init; }
    public string Model { get; init; } = "gpt-6-luna";
    public string Tag { get; init; } = "needs review";
    public int BatchSize { get; init; } = 5;
    public int BackfillLimit { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(5);
    public long MaxAuditBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public string? SourceUrl { get; init; }
    public int DiscoveryLimit { get; init; } = 100;
    public int MaxJobs { get; init; } = 10_000;
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(StateDirectory) || string.IsNullOrWhiteSpace(Tag) ||
            string.IsNullOrWhiteSpace(Model) || Model.Length > 128 || BatchSize is < 1 or > 100 ||
            BackfillLimit is < 0 or > 100 || PollInterval < TimeSpan.FromSeconds(1) ||
            PollInterval > TimeSpan.FromDays(1) || MaxAuditBytes < 1 || DiscoveryLimit is < 1 or > 100 ||
            MaxJobs is < 1 or > 100_000 || MaxAttempts is < 1 or > 10 || RetryBaseDelay < TimeSpan.FromSeconds(1))
            throw new ArgumentException("invalid_worker_configuration");
    }
}

public sealed record PollResult(int Reviewed, int Unchanged, int Failed, bool Initialized);
