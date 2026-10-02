using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaperlessLlm.Eval;

public static partial class ExperimentRunner
{
    private static async Task<string> InvokeCodexAsync(string prompt, IReadOnlyList<string> images, string reasoning,
        ExperimentOptions options, string caseDir, string stage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cwd = Path.Combine(caseDir, ".cwd-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cwd);
        var psi = CreateCodexStartInfo(options.Model, reasoning, cwd, images);
        try
        {
            using var process = new Process { StartInfo = psi };
            ct.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException("Could not start codex.");
            using var killOnCancellation = ct.Register(KillOnCancellation, process);
            ct.ThrowIfCancellationRequested();
            var startedUtc = DateTimeOffset.UtcNow;
            var startedTimestamp = Stopwatch.GetTimestamp();
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxStreamCharacters);
            var stderrTask = ReadBoundedAsync(process.StandardError, MaxStreamCharacters);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var timedOut = false;
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), timeout.Token);
                ct.ThrowIfCancellationRequested();
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                await StopProcessTreeAsync(process);
                try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
                timedOut = !ct.IsCancellationRequested;
            }
            var stdoutCapture = await stdoutTask; var stderrCapture = await stderrTask;
            var stdout = stdoutCapture.Text + (stdoutCapture.Truncated ? "\n[stdout truncated at configured capture limit]\n" : "");
            var stderr = stderrCapture.Text + (stderrCapture.Truncated ? "\n[stderr truncated at configured capture limit]\n" : "");
            var imageRecords = await ImageProvenanceAsync(images, ct);
            var usage = ReadUsage(stdout);
            var stageProvenance = new
            {
                stage,
                model = options.Model,
                reasoning,
                started_utc = startedUtc,
                finished_utc = DateTimeOffset.UtcNow,
                elapsed_ms = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
                prompt_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))),
                image_count = images.Count,
                stdout_truncated = stdoutCapture.Truncated,
                stderr_truncated = stderrCapture.Truncated,
                timed_out = timedOut,
                cancelled = ct.IsCancellationRequested,
                images = imageRecords,
                input_tokens = usage.InputTokens,
                output_tokens = usage.OutputTokens
            };
            await PersistStageArtifactsAsync(caseDir, stage, stdout, stderr,
                JsonSerializer.Serialize(stageProvenance, new JsonSerializerOptions(EvalJson.Options) { WriteIndented = true }), ct);
            ct.ThrowIfCancellationRequested();
            if (timedOut) throw new TimeoutException($"Codex stage {stage} exceeded {options.TimeoutSeconds}s.");
            if (stdoutCapture.Truncated) throw new InvalidDataException($"Codex stage {stage} exceeded the stdout capture limit; result rejected.");
            if (process.ExitCode != 0) throw new InvalidDataException($"Codex stage {stage} exited {process.ExitCode}.");
            return ExtractFinalMessage(stdout);
        }
        finally { RemoveEmptyWorkingDirectory(cwd); }
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

    private static void RemoveEmptyWorkingDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory); } catch (IOException) { }
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
