using System.Globalization;
using System.Text.Json;
using PaperlessLlm.Organizer;
using PaperlessLlm.Paperless;

namespace PaperlessLlm;

internal static class ReprocessingCli
{
    public static async Task<int> RunAsync(string[] args, string directory, long maxBytes, CancellationToken ct)
    {
        if (args[0] == "reprocess") return await SubmitAsync(args[1..], directory, maxBytes, ct);
        if (args[0] == "runs") return await RunsAsync(args[1..], directory, ct);
        await ShowAsync(args[1..], directory, ct);
        return 0;
    }
    private static async Task<int> SubmitAsync(string[] args, string directory, long maxBytes, CancellationToken ct)
    {
        var options = ReprocessingArguments.Parse(args);
        var request = await SelectOrReplayAsync(options, directory, ct);
        if (!options.Preview) request = await new ReprocessingQueue(directory).SubmitAsync(request, maxBytes, ct);
        var output = new { Preview = options.Preview, RunId = options.Preview ? null : request.RunId,
            Selected = request.Items.Length, Eligible = request.Items.Count(i => i.SkipReason is null),
            Skipped = request.Items.Count(i => i.SkipReason is not null), Mode = request.NotesOnly ? "notes_only" : "metadata_and_notes",
            Items = request.Items.Select(i => new { i.DocumentId, i.SkipReason }) };
        if (options.Json) Console.WriteLine(JsonSerializer.Serialize(output));
        else
        {
            Console.WriteLine($"{(options.Preview ? "Preview" : "Submitted " + request.RunId)}: {output.Eligible} eligible, {output.Skipped} skipped ({output.Mode}).");
            foreach (var group in request.Items.Where(i => i.SkipReason is not null).GroupBy(i => i.SkipReason))
                Console.WriteLine($"  {group.Key}: {group.Count()}");
            if (!options.Preview) Console.WriteLine($"Submission saved. The worker processes it sequentially; you can disconnect.\nProgress: status --run {request.RunId}");
        }
        return 0;
    }
    private static async Task<ReprocessingRequest> SelectOrReplayAsync(ReprocessingArguments options, string directory, CancellationToken ct)
    {
        if (options.RequestId is { } id)
        {
            var existing = await new ReprocessingQueue(directory).ExistingAsync(id, ReprocessingSelector.Key(options.Selection), ct);
            if (existing is not null) return existing;
        }
        using var reader = CreateReader(options.Selection);
        if (reader is not null) await VerifySourceAsync(directory, ct);
        var request = await new ReprocessingSelector(directory, reader).SelectAsync(options.Selection, ct);
        return options.RequestId is null ? request : request with { RunId = options.RequestId };
    }
    private static PaperlessClient? CreateReader(ReprocessingSelection selection)
    {
        if (!selection.MissingSummary && !selection.IncludeUnenrolled) return null;
        return new(new PaperlessOptions { BaseUrl = new Uri(Required("PAPERLESS_URL")), TokenFile = Required("PAPERLESS_TOKEN_FILE") });
    }
    private static async Task VerifySourceAsync(string directory, CancellationToken ct)
    {
        var store = new OrganizerStore(directory);
        var state = await store.ReadAsync<OrganizerCheckpoint>(store.CheckpointPath, ct);
        if (state?.SourceUrl != new Uri(Required("PAPERLESS_URL")).AbsoluteUri)
            throw new ArgumentException("Paperless URL must match the initialized worker scope.");
    }
    private static string Required(string key) => Environment.GetEnvironmentVariable("PPLLM_" + key)
        ?? throw new ArgumentException("Set PPLLM_" + key + ".");
    private static async Task<int> RunsAsync(string[] args, string directory, CancellationToken ct)
    {
        var queue = new ReprocessingQueue(directory);
        if (args.Length == 2 && args[0] is "cancel" or "resume")
        {
            await queue.SetCancelledAsync(args[1], args[0] == "cancel", ct);
            Console.WriteLine(args[0] == "cancel" ? "Unstarted work cancelled. Started jobs still recover and finish safely. Use runs resume ID to restore unstarted work."
                : "Unstarted work resumed. Failed jobs still require explicit retry.");
            return 0;
        }
        if (args.Length != 0) throw new ArgumentException("Use runs, runs cancel ID, or runs resume ID.");
        foreach (var request in await queue.ListAsync(ct))
            Write(await new ReprocessingStatus(directory).ReadAsync(request.RunId, ct), false);
        return 0;
    }
    private static async Task ShowAsync(string[] args, string directory, CancellationToken ct)
    {
        if (args.Length < 2 || args[0] != "--run" || args[2..].Any(a => a is not ("--json" or "--watch")))
            throw new ArgumentException("Use status --run ID [--json] [--watch].");
        while (true)
        {
            var status = await new ReprocessingStatus(directory).ReadAsync(args[1], ct);
            Write(status, args.Contains("--json"));
            if (!args.Contains("--watch") || status.Phase is "completed" or "cancelled") return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
    private static void Write(ReprocessingRunStatus status, bool json)
    {
        if (json) { Console.WriteLine(JsonSerializer.Serialize(status)); return; }
        Console.WriteLine($"{status.RunId} {status.Phase}: {status.Selected} selected; {status.Queued} queued, {status.Running} running, {status.Applied} applied, {status.NoChange} unchanged, {status.Skipped} skipped, {status.Failed} failed, {status.Blocked} waiting, {status.Cancelled} cancelled.");
        foreach (var group in status.Items.Where(i => i.Reason is not null).GroupBy(i => i.Reason))
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        if (status.NextAttemptAt is { } next) Console.WriteLine($"  Next attempt: {next:O}");
    }
}

internal sealed record ReprocessingArguments(ReprocessingSelection Selection, bool Preview, bool Json, string? RequestId = null)
{
    public static ReprocessingArguments Parse(string[] args)
    {
        if (args.Length == 1 && int.TryParse(args[0], out var id) && id > 0) return new(new([id]), false, false);
        var values = Read(args);
        if (values.ContainsKey("--all") == values.ContainsKey("--ids")) throw new ArgumentException("Choose --all or --ids ID,ID-RANGE.");
        var ids = values.TryGetValue("--ids", out var text) ? ParseIds(text!) : null;
        var before = values.TryGetValue("--processed-before", out var date) ? ParseDate(date!) : (DateTimeOffset?)null;
        var limit = ParseLimit(values);
        ValidateHistory(values, ids);
        return new(new(ids, before, values.ContainsKey("--missing-summary"), values.ContainsKey("--include-unenrolled"), limit,
            values.ContainsKey("--notes-only")), values.ContainsKey("--preview"), values.ContainsKey("--json"), values.GetValueOrDefault("--request-id"));
    }
    private static int ParseLimit(Dictionary<string, string?> values)
    {
        if (!values.TryGetValue("--limit", out var text)) return 10000;
        return int.TryParse(text, out var value) && value is >= 1 and <= 10000
            ? value : throw new ArgumentException("--limit must be between 1 and 10000.");
    }
    private static void ValidateHistory(Dictionary<string, string?> values, int[]? ids)
    {
        if (values.ContainsKey("--include-unenrolled") && (ids is not null || !values.ContainsKey("--limit")))
            throw new ArgumentException("Historical enrollment requires --all --include-unenrolled --limit N.");
    }
    private static Dictionary<string, string?> Read(string[] args)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            var value = key switch
            {
                "--ids" or "--processed-before" or "--limit" or "--request-id" => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value."),
                "--all" or "--preview" or "--json" or "--missing-summary" or "--include-unenrolled" or "--notes-only" => null,
                _ => throw new ArgumentException("Unknown reprocessing option. Run --help.")
            };
            if (!values.TryAdd(key, value)) throw new ArgumentException("Duplicate reprocessing option.");
        }
        return values;
    }
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : throw new ArgumentException("Invalid --processed-before timestamp; use ISO 8601.");
    internal static int[] ParseIds(string value)
    {
        var ids = new HashSet<int>();
        foreach (var part in value.Split(','))
        {
            var range = part.Split('-');
            if (range.Length > 2 || !int.TryParse(range[0], out var first) || first < 1) throw new ArgumentException("Invalid document IDs.");
            var last = range.Length == 1 ? first : int.TryParse(range[1], out var end) ? end : 0;
            if (last < first || (long)last - first >= 10000) throw new ArgumentException("Invalid or oversized document range.");
            ids.UnionWith(Enumerable.Range(first, last - first + 1));
            if (ids.Count > 10000) throw new ArgumentException("At most 10000 IDs per request.");
        }
        return ids.Order().ToArray();
    }
}
