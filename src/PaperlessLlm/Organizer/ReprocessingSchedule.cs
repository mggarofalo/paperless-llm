namespace PaperlessLlm.Organizer;

internal sealed class ReprocessingSchedule(string directory, TimeProvider clock)
{
    private readonly ReprocessingQueue queue = new(directory);
    public string Signal() => !Directory.Exists(queue.RunsPath) ? "" : string.Join('|',
        Directory.EnumerateFiles(queue.RunsPath).Where(p => p.EndsWith(".json") || p.EndsWith(".control"))
            .Order().Select(p => Path.GetFileName(p) + File.GetLastWriteTimeUtc(p).Ticks));

    public async Task<DateTimeOffset> NextAsync(DateTimeOffset normalPoll, CancellationToken ct)
    {
        var next = normalPoll;
        var reader = new ReprocessingStatus(directory);
        foreach (var request in await queue.ListAsync(ct))
        {
            var status = await reader.ReadAsync(request.RunId, ct);
            if (status.Queued > 0) return clock.GetUtcNow() + TimeSpan.FromSeconds(1);
            if (status.NextAttemptAt is { } retry && retry < next) next = retry;
        }
        return next;
    }
    public async Task WaitAsync(DateTimeOffset until, string signal, bool paused, CancellationToken ct)
    {
        while (clock.GetUtcNow() < until)
        {
            if (!paused && signal != Signal()) return;
            var delay = until - clock.GetUtcNow();
            if (delay <= TimeSpan.Zero) return;
            await Task.Delay(delay < TimeSpan.FromSeconds(2) ? delay : TimeSpan.FromSeconds(2), clock, ct);
        }
    }
}
