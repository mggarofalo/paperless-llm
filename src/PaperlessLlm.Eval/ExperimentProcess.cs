using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaperlessLlm.Eval;

public static partial class ExperimentRunner
{
    /// <summary>Token counts a stage reported. Cache counts exist only for harnesses that expose them.</summary>
    internal sealed record StageUsage(long InputTokens, long OutputTokens, long? CacheReadTokens = null, long? CacheWriteTokens = null) { }

    /// <summary>How one harness's child process is interpreted: usage from its stream, and its accepted result.</summary>
    internal sealed record StageTransport(string Harness, Func<string, StageUsage> ReadUsage, Func<string, int, string, string> Extract) { }

    private static readonly StageTransport CodexTransport = new("codex", ReadCodexUsage, (stdout, exitCode, stage) =>
    {
        if (exitCode != 0) throw new InvalidDataException($"Codex stage {stage} exited {exitCode}.");
        return ExtractFinalMessage(stdout);
    });

    internal static Task<string> InvokeCodexAsync(string prompt, IReadOnlyList<string> images, string reasoning,
        ExperimentOptions options, string caseDir, string stage, CancellationToken ct,
        Func<ProcessStartInfo, ProcessStartInfo>? configureProcess = null)
        => InvokeProcessAsync(cwd => CreateCodexStartInfo(options.Model, reasoning, cwd, images), prompt, images, reasoning,
            options, caseDir, stage, CodexTransport, ct, configureProcess);

    internal static async Task<string> InvokeProcessAsync(Func<string, ProcessStartInfo> createStartInfo, string input, IReadOnlyList<string> images,
        string reasoning, ExperimentOptions options, string caseDir, string stage, StageTransport transport, CancellationToken ct,
        Func<ProcessStartInfo, ProcessStartInfo>? configureProcess)
    {
        ct.ThrowIfCancellationRequested();
        var cwd = Path.Combine(caseDir, ".cwd-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cwd);
        var psi = createStartInfo(cwd);
        if (configureProcess is not null) psi = configureProcess(psi);
        try
        {
            using var process = new Process { StartInfo = psi };
            ct.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException($"Could not start the {transport.Harness} stage process.");
            using var killOnCancellation = ct.Register(KillOnCancellation, process);
            ct.ThrowIfCancellationRequested();
            var startedUtc = DateTimeOffset.UtcNow;
            var startedTimestamp = Stopwatch.GetTimestamp();
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxStreamCharacters);
            var stderrTask = ReadBoundedAsync(process.StandardError, MaxStreamCharacters);
            var timedOut = await FeedAndWaitAsync(process, input, options.TimeoutSeconds, ct);
            var stdoutCapture = await stdoutTask; var stderrCapture = await stderrTask;
            var stdout = stdoutCapture.Text + (stdoutCapture.Truncated ? "\n[stdout truncated at configured capture limit]\n" : "");
            var stderr = stderrCapture.Text + (stderrCapture.Truncated ? "\n[stderr truncated at configured capture limit]\n" : "");
            var usage = transport.ReadUsage(stdout);
            var stageProvenance = new
            {
                stage,
                harness = transport.Harness,
                model = options.Model,
                reasoning,
                started_utc = startedUtc,
                finished_utc = DateTimeOffset.UtcNow,
                elapsed_ms = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
                prompt_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input))),
                image_count = images.Count,
                stdout_truncated = stdoutCapture.Truncated,
                stderr_truncated = stderrCapture.Truncated,
                timed_out = timedOut,
                cancelled = ct.IsCancellationRequested,
                images = await ImageProvenanceAsync(images, ct),
                input_tokens = usage.InputTokens,
                output_tokens = usage.OutputTokens,
                cache_read_tokens = usage.CacheReadTokens,
                cache_write_tokens = usage.CacheWriteTokens
            };
            await PersistStageArtifactsAsync(caseDir, stage, stdout, stderr,
                JsonSerializer.Serialize(stageProvenance, new JsonSerializerOptions(EvalJson.Options) { WriteIndented = true }), ct);
            ct.ThrowIfCancellationRequested();
            if (timedOut) throw new TimeoutException($"{transport.Harness} stage {stage} exceeded {options.TimeoutSeconds}s.");
            if (stdoutCapture.Truncated) throw new InvalidDataException($"{transport.Harness} stage {stage} exceeded the stdout capture limit; result rejected.");
            return transport.Extract(stdout, process.ExitCode, stage);
        }
        finally { RemoveWorkingDirectory(cwd); }
    }

    /// <summary>Writes stdin and waits within the stage limit; a stopped child reports whether the limit, not the caller, stopped it.</summary>
    private static async Task<bool> FeedAndWaitAsync(Process process, string input, int timeoutSeconds, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            // A child that refuses the request exits before reading stdin; its own diagnostics, not the broken pipe, explain why.
            try { await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token); }
            catch (IOException) { }
            ct.ThrowIfCancellationRequested();
            try { process.StandardInput.Close(); } catch (IOException) { }
            await process.WaitForExitAsync(timeout.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            await StopProcessTreeAsync(process);
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
            return !ct.IsCancellationRequested;
        }
    }

    private static async Task<List<object>> ImageProvenanceAsync(IReadOnlyList<string> images, CancellationToken ct)
    {
        var imageRecords = new List<object>();
        foreach (var image in images)
        {
            string? imageHash = null;
            if (!ct.IsCancellationRequested && File.Exists(image))
            {
                try { imageHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(image, ct))); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            }
            imageRecords.Add(new { path = image, sha256 = imageHash });
        }
        return imageRecords;
    }

    private static void KillOnCancellation(object? state)
    {
        var child = (Process)state!;
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
    }

    // The per-stage working directory is created fresh for one child; a bridge may leave its own scratch files there.
    private static void RemoveWorkingDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static async Task PersistStageArtifactsAsync(string caseDirectory, string stage, string stdout, string stderr,
        string provenanceJson, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken; // Once a child has stopped, cancellation must not discard its bounded diagnostics.
        await File.WriteAllTextAsync(Path.Combine(caseDirectory, stage + ".stdout.jsonl"), stdout, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(caseDirectory, stage + ".stderr.log"), stderr, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(caseDirectory, stage + ".provenance.json"), provenanceJson, CancellationToken.None);
    }

    internal static async Task StopProcessTreeAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        if (!process.HasExited) await process.WaitForExitAsync(CancellationToken.None);
    }

    private sealed record BoundedCapture(string Text, bool Truncated) { }

    private static async Task<BoundedCapture> ReadBoundedAsync(StreamReader reader, int maxCharacters)
    {
        var builder = new StringBuilder(Math.Min(maxCharacters, 32 * 1024));
        var buffer = new char[16 * 1024];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) != 0)
        {
            var keep = Math.Min(read, maxCharacters - builder.Length);
            if (keep > 0) builder.Append(buffer, 0, keep);
            if (keep < read) truncated = true;
        }
        return new(builder.ToString(), truncated);
    }

}
