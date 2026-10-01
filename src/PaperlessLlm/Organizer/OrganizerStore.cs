using System.Text.Json;
using PaperlessLlm.Review;
namespace PaperlessLlm.Organizer;
internal sealed class OrganizerCheckpoint
{
    public int Version { get; set; } = 1;
    public string SourceUrl { get; set; } = "";
    public int BaselineId { get; set; }
    public int CursorId { get; set; }
    public int[] InitialBackfillIds { get; set; } = [];
    public DateTimeOffset? LastPollAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string? PauseReason { get; set; }
}
internal sealed class OrganizerStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string JobsPath => Path.Combine(DirectoryPath, "jobs");
    public FileStream Lock()
    {
        AuditWriter.PrivateDirectory(DirectoryPath);
        AuditWriter.PrivateDirectory(JobsPath);
        return new FileStream(Path.Combine(DirectoryPath, "organizer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidOperationException("state_file_too_large");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<T>(file, cancellationToken: ct) ?? throw new InvalidOperationException("invalid_organizer_state");
    }
    public string CheckpointPath => Path.Combine(DirectoryPath, "organizer.json");
    public string JobPath(int id) => Path.Combine(JobsPath, id + ".json");
    public async Task SaveAsync<T>(string path, T value, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await AuditWriter.WritePrivateAsync(temporary, JsonSerializer.Serialize(value), ct);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<List<OrganizerJob>> JobsAsync(CancellationToken ct)
    {
        var jobs = new List<OrganizerJob>();
        if (!Directory.Exists(JobsPath)) return jobs;
        foreach (var path in Directory.EnumerateFiles(JobsPath, "*.json"))
        {
            var job = await ReadAsync<OrganizerJob>(path, ct) ?? throw new InvalidOperationException("missing_job");
            if (job.DocumentId < 1 || !Enum.IsDefined(job.State) || job.Attempts < 0 || !Guid.TryParseExact(job.JobId, "N", out _) || Path.GetFileName(path) != job.DocumentId + ".json")
                throw new InvalidOperationException("invalid_organizer_job");
            jobs.Add(job);
        }
        return jobs.OrderBy(x => x.DocumentId).ToList();
    }
}
public static class OrganizerStatusReader
{
    public static async Task<OrganizerStatus> ReadAsync(string directory, CancellationToken ct = default)
    {
        var store = new OrganizerStore(directory);
        var state = await store.ReadAsync<OrganizerCheckpoint>(store.CheckpointPath, ct);
        var jobs = await store.JobsAsync(ct);
        return new(state?.BaselineId ?? 0, state?.CursorId ?? 0, state?.LastPollAt,
            jobs.Select(j => new OrganizerJobSummary(j.DocumentId, j.JobId, j.State, j.Attempts, j.ErrorCode, j.NextAttemptAt, j.Outcome)).ToArray(), state?.LastActivityAt, state?.NextRunAt, state?.PauseReason);
    }
}


