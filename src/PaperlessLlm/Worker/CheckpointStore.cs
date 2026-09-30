using System.Text.Json;
using System.Text.Json.Serialization;
using PaperlessLlm.Review;

namespace PaperlessLlm.Worker;

internal sealed class Checkpoint
{
    [JsonRequired] public int FormatVersion { get; set; } = 2;
    [JsonRequired] public int BaselineId { get; set; }
    [JsonRequired] public int CursorId { get; set; }
    [JsonRequired] public int BackfillCursorId { get; set; }
    [JsonRequired] public string Tag { get; set; } = "";
    [JsonRequired] public string SourceUrl { get; set; } = "";
    public DateTimeOffset? AuthRetryAt { get; set; }
    public DateTimeOffset? LastPollAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? PauseReason { get; set; }
    [JsonRequired] public Dictionary<int, ReviewJob> Jobs { get; set; } = [];
}

internal sealed class CheckpointStore(string directory)
{
    private readonly string directory = Path.GetFullPath(directory);
    private string FilePath => Path.Combine(directory, "checkpoint.json");

    public FileStream Lock()
    {
        AuditWriter.PrivateDirectory(directory);
        return new FileStream(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async Task<Checkpoint?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath)) return null;
        if (new FileInfo(FilePath).Length > 32 * 1024 * 1024) throw new InvalidOperationException("checkpoint_too_large");
        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var value = await JsonSerializer.DeserializeAsync<Checkpoint>(stream, cancellationToken: cancellationToken);
            if (value is null || value.FormatVersion != 2 || value.BaselineId < 0 || value.CursorId < value.BaselineId || value.BackfillCursorId < 0 ||
                value.Jobs is null || value.Jobs.Any(x => x.Key <= 0 || x.Value is null || x.Value.DocumentId != x.Key ||
                    string.IsNullOrWhiteSpace(x.Value.RevisionHash) || string.IsNullOrWhiteSpace(x.Value.PolicyFingerprint) ||
                    !Enum.IsDefined(x.Value.Status) || x.Value.Attempts < 0))
                throw new InvalidOperationException("invalid_checkpoint");
            return value;
        }
        catch (JsonException) { throw new InvalidOperationException("invalid_checkpoint"); }
    }

    public async Task SaveAsync(Checkpoint value, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(directory, ".checkpoint-" + Guid.NewGuid().ToString("N"));
        try
        {
            await AuditWriter.WritePrivateAsync(temporary, JsonSerializer.Serialize(value), cancellationToken);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
