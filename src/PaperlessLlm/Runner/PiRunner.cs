using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PaperlessLlm.Auth;
using PaperlessLlm.Inference;

namespace PaperlessLlm.Runner;

public sealed record PiRunnerOptions(string HomeDirectory, string BridgePath, string NodeExecutable = "node", TimeSpan? Timeout = null, string Reasoning = "medium") { }
public sealed record DeviceLogin(string Url, string Code) { }
public sealed class RunnerRateLimitException() : Exception("runner_rate_limited");

/// <summary>One isolated Pi provider request. The child has no Paperless credentials or tool executor.</summary>
public sealed class PiRunner(PiRunnerOptions options)
{
    private const int MaxOutput = 4 * 1024 * 1024;
    public async Task<string> RunAsync(string model, string instructions, string prompt, IReadOnlyList<string> imageDataUrls, JsonElement schema, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model) || imageDataUrls.Count > 10 || prompt.Length > 2_000_000)
            throw new InferenceException("runner_input_invalid");
        long total = 0;
        foreach (var url in imageDataUrls)
        {
            if (!url.StartsWith("data:image/png;base64,", StringComparison.Ordinal) && !url.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal))
                throw new InferenceException("runner_image_invalid");
            total += url.Length;
        }
        if (total > 42 * 1024 * 1024) throw new InferenceException("runner_input_limit");
        if (options.Reasoning is not ("low" or "medium" or "high")) throw new InferenceException("runner_reasoning_invalid");
        var request = JsonSerializer.Serialize(new { model, instructions, prompt, images = imageDataUrls, schema, reasoning = options.Reasoning });
        var result = await ExecuteAsync("infer", request, null, cancellationToken);
        var message = result.LastOrDefault(x => x.GetProperty("type").GetString() == "result");
        if (message.ValueKind == JsonValueKind.Undefined) throw new InferenceException("runner_missing_result");
        var text = message.GetProperty("text").GetString()!;
        return text;
    }

    public async Task LoginAsync(Action<DeviceLogin> displayCode, CancellationToken cancellationToken = default)
        => await ExecuteAsync("login", null, displayCode, cancellationToken);
    public async Task<bool> StatusAsync(CancellationToken cancellationToken = default)
        => (await ExecuteAsync("status", null, null, cancellationToken)).Any(x => x.GetProperty("type").GetString() == "ready");
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync("logout", null, null, cancellationToken);
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var messages = await ExecuteAsync("models", null, null, cancellationToken);
        return messages.Single(x => x.GetProperty("type").GetString() == "models").GetProperty("models").Deserialize<string[]>()!;
    }

    private async Task<List<JsonElement>> ExecuteAsync(string command, string? input, Action<DeviceLogin>? displayCode, CancellationToken cancellationToken)
    {
        var home = Path.GetFullPath(options.HomeDirectory);
        Directory.CreateDirectory(home);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FileStream homeLock;
        try { homeLock = new FileStream(Path.Combine(home, ".runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InferenceException("runner_busy"); }
        await using var heldLock = homeLock;
        var work = Path.Combine(home, "work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var start = CreateStartInfo(command, home, work);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(command == "login" ? TimeSpan.FromMinutes(20) : options.Timeout ?? TimeSpan.FromMinutes(5));
        using var process = new Process { StartInfo = start };
        try
        {
            try { if (!process.Start()) throw new InferenceException("runner_start_failed"); }
            catch (System.ComponentModel.Win32Exception) { throw new InferenceException("runner_unavailable"); }
            using var registration = timeout.Token.Register(() => Kill(process));
            var output = ReadOutputAsync(process.StandardOutput, displayCode, timeout.Token);
            var errors = DrainAsync(process.StandardError, timeout.Token);
            _ = output.ContinueWith(_ => Kill(process), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var messages = await output;
            await errors;
            ValidateExit(command, process.ExitCode, messages);
            return messages;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InferenceException("runner_timeout"); }
        catch (JsonException) { throw new InferenceException("runner_protocol_invalid"); }
        finally
        {
            await CleanUpAsync(process, work);
        }
    }

    private static async Task<List<JsonElement>> ReadOutputAsync(StreamReader reader, Action<DeviceLogin>? displayCode, CancellationToken ct)
    {
        var messages = new List<JsonElement>();
        var line = new StringBuilder();
        var buffer = new char[4096];
        var total = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            total += count;
            if (total > MaxOutput) throw new InferenceException("runner_output_limit");
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n') { line.Append(buffer[i]); continue; }
                using var json = JsonDocument.Parse(line.ToString());
                line.Clear();
                var message = json.RootElement.Clone();
                messages.Add(message);
                DisplayDeviceCode(message, displayCode);
            }
        }
        if (line.Length != 0) throw new InferenceException("runner_protocol_invalid");
        return messages;
    }

    private ProcessStartInfo CreateStartInfo(string command, string home, string work)
    {
        var start = new ProcessStartInfo(options.NodeExecutable) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath(options.BridgePath));
        start.ArgumentList.Add(command);
        // Allowlist only OS execution essentials. In particular NODE_OPTIONS, proxy settings,
        // provider API keys, personal configuration and Paperless credentials never cross.
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "PATHEXT" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (var name in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "TMPDIR", "TMP", "TEMP" }) start.Environment[name] = work;
        start.Environment["PPLLM_RUNNER_HOME"] = home;
        start.Environment["PI_CODING_AGENT_DIR"] = work;
        start.Environment["NO_COLOR"] = "1";
        return start;
    }

    private static bool HasCode(List<JsonElement> messages, string value) =>
        messages.Any(x => x.TryGetProperty("code", out var code) && code.GetString() == value);

    private static void ValidateExit(string command, int exitCode, List<JsonElement> messages)
    {
        var authFailure = HasCode(messages, "auth_required");
        if (exitCode == 22 || HasCode(messages, "rate_limited"))
            throw new RunnerRateLimitException();
        if (exitCode == 20 || authFailure && command != "status") throw new AuthException("runner_sign_in_required", requiresSignIn: true);
        if (exitCode != 0)
        {
            var code = messages.LastOrDefault(x => x.TryGetProperty("code", out _));
            var value = code.ValueKind == JsonValueKind.Undefined ? null : code.GetProperty("code").GetString();
            throw new InferenceException(value is "model_unavailable" or "context_limit" or "image_invalid" or "input_limit" or "incomplete_response"
                or "image_rejected" or "model_access_denied" or "access_denied" or "transport_failed" or "provider_unavailable"
                or "request_rejected" or "auth_failed" or "inference_failed" ? "runner_" + value : "runner_request_failed");
        }
    }

    private static async Task CleanUpAsync(Process process, string work)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
        try { Directory.Delete(work, recursive: true); } catch (IOException) { }
    }

    private static void DisplayDeviceCode(JsonElement message, Action<DeviceLogin>? displayCode)
    {
        if (message.GetProperty("type").GetString() == "device_code" && displayCode is not null)
        {
            var url = message.GetProperty("url").GetString()!;
            var code = message.GetProperty("code").GetString()!;
            if (url != "https://auth.openai.com/codex/device" || code.Length > 32 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new AuthException("runner_device_code_invalid");
            displayCode(new DeviceLogin(url, code));
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0) { }
    }
}
