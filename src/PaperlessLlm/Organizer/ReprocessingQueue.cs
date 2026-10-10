using PaperlessLlm.Review;

namespace PaperlessLlm.Organizer;

/// <summary>Immutable submissions; only the worker updates progress. Controls have a short per-run lock.</summary>
public sealed class ReprocessingQueue(string directory)
{
    internal readonly OrganizerStore Store = new(directory);
    internal string RunsPath => Path.Combine(Store.DirectoryPath, "runs");
    internal string PathFor(string id, string extension = "json")
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid run ID.");
        return Path.Combine(RunsPath, id + "." + extension);
    }
    public async Task<ReprocessingRequest> GetAsync(string id, CancellationToken ct = default)
    {
        var request = await Store.ReadAsync<ReprocessingRequest>(PathFor(id), ct) ?? throw new ArgumentException("Run not found.");
        Validate(request, id);
        return request;
    }
    private static void Validate(ReprocessingRequest request, string id)
    {
        if (request.RunId != id || string.IsNullOrWhiteSpace(request.SourceUrl) || request.Items.Length > 10000
            || request.Items.Select(i => i.DocumentId).Distinct().Count() != request.Items.Length)
            throw new ArgumentException("Invalid reprocessing request.");
        foreach (var item in request.Items)
            if (item.DocumentId < 1 || !Guid.TryParseExact(item.JobId, "N", out _)
                || item.PreviousJobId is not null && !Guid.TryParseExact(item.PreviousJobId, "N", out _))
                throw new ArgumentException("Invalid reprocessing item.");
    }

    public async Task<IReadOnlyList<ReprocessingRequest>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(RunsPath)) return [];
        var requests = new List<ReprocessingRequest>();
        foreach (var path in Directory.EnumerateFiles(RunsPath, "*.json"))
            requests.Add(await GetAsync(Path.GetFileNameWithoutExtension(path), ct));
        return requests.OrderBy(r => r.SubmittedAt).ThenBy(r => r.RunId).ToArray();
    }
    public async Task<ReprocessingRequest?> ExistingAsync(string id, string? key, CancellationToken ct = default)
    {
        if (!File.Exists(PathFor(id))) return null;
        var existing = await GetAsync(id, ct);
        if (key is null || existing.SelectionKey != key) throw new ArgumentException("Request ID was already used with a different selection.");
        return existing;
    }
    public async Task<ReprocessingRequest> SubmitAsync(ReprocessingRequest request, long maxBytes = 2L * 1024 * 1024 * 1024,
        CancellationToken ct = default)
    {
        _ = PathFor(request.RunId);
        Validate(request, request.RunId);
        AuditWriter.PrivateDirectory(RunsPath);
        using var held = await LockAsync("submission", ct);
        var existing = await ExistingAsync(request.RunId, request.SelectionKey, ct);
        if (existing is not null) return existing;
        if (Directory.EnumerateFiles(RunsPath, "*.json").Count() >= 1000)
            throw new ArgumentException("Run history capacity reached (1000 runs).");
        if (Directory.EnumerateFiles(Store.DirectoryPath, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) >= maxBytes)
            throw new ArgumentException("Organizer storage capacity reached.");
        await Store.SaveAsync(PathFor(request.RunId), request, ct);
        return request;
    }
    public async Task SetCancelledAsync(string id, bool cancelled, CancellationToken ct = default)
    {
        await GetAsync(id, ct);
        using var held = await LockAsync(id, ct);
        await Store.SaveAsync(PathFor(id, "control"), new ReprocessingControl(cancelled), ct);
    }
    internal async Task<bool> CancelledAsync(string id, CancellationToken ct) =>
        (await Store.ReadAsync<ReprocessingControl>(PathFor(id, "control"), ct))?.Cancelled == true;
    internal async Task<ReprocessingProgress> ProgressAsync(string id, CancellationToken ct) =>
        await Store.ReadAsync<ReprocessingProgress>(PathFor(id, "progress"), ct) ?? new();
    internal async Task<FileStream> LockAsync(string id, CancellationToken ct)
    {
        var path = Path.Combine(RunsPath, id + ".lock");
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(50, ct); }
        }
    }
}
