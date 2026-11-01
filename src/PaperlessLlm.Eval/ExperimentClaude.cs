using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PaperlessLlm.Runner;

namespace PaperlessLlm.Eval;

/// <summary>
/// Claude Code experiment harness. Each stage runs the unmodified production bridge (<c>runner/claude-bridge.mjs infer</c>)
/// with the worker's environment allowlist, so the CLI flags, structured-output schema, effort and isolation are the
/// ones the deployed worker uses. Only the bridge's NDJSON result and token counts cross back.
/// </summary>
public static partial class ExperimentRunner
{
    public const string ClaudeHarness = "claude";
    public const string CodexHarness = "codex";
    public static string DefaultClaudeBridge => Path.Combine("runner", RunnerHarness.Claude.BridgeFile);

    private static readonly StageTransport ClaudeTransport = new(ClaudeHarness, ReadBridgeUsage, ExtractBridgeResult);

    internal static Task<string> InvokeClaudeAsync(string instructions, string prompt, string schemaJson, IReadOnlyList<string> images,
        string reasoning, ExperimentOptions options, string caseDir, string stage, CancellationToken ct,
        Func<ProcessStartInfo, ProcessStartInfo>? configureProcess = null)
    {
        var request = BuildBridgeRequest(options.Model, instructions, prompt, images, schemaJson, reasoning);
        return InvokeProcessAsync(cwd => CreateClaudeStartInfo(options.BridgePath ?? DefaultClaudeBridge, options.RunnerHome!, cwd),
            request, images, reasoning, options, caseDir, stage, ClaudeTransport, ct, configureProcess);
    }

    /// <summary>The same request shape <c>BridgeRunner</c> sends; page images become data URLs because the bridge accepts no paths.</summary>
    public static string BuildBridgeRequest(string model, string instructions, string prompt, IReadOnlyList<string> images, string schemaJson, string reasoning)
    {
        if (reasoning is not ("low" or "medium" or "high")) throw new InvalidDataException("Claude harness reasoning must be low, medium, or high.");
        using var schema = JsonDocument.Parse(schemaJson);
        return JsonSerializer.Serialize(new
        {
            model, instructions, prompt, images = images.Select(ImageDataUrl).ToArray(), schema = schema.RootElement, reasoning
        });
    }

    public static string ImageDataUrl(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var mediaType = bytes is [0x89, 0x50, 0x4E, 0x47, ..] ? "image/png" : bytes is [0xFF, 0xD8, 0xFF, ..] ? "image/jpeg"
            : throw new InvalidDataException($"Image '{Path.GetFileName(path)}' is not PNG or JPEG.");
        return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>Mirrors <c>BridgeRunner.CreateStartInfo</c>: only OS execution essentials cross, and personal configuration never does.</summary>
    public static ProcessStartInfo CreateClaudeStartInfo(string bridgePath, string runnerHome, string workingDirectory, string nodeExecutable = "node")
    {
        var utf8 = new UTF8Encoding(false);
        var psi = new ProcessStartInfo(nodeExecutable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = utf8, StandardOutputEncoding = utf8, StandardErrorEncoding = utf8,
            UseShellExecute = false, CreateNoWindow = true
        };
        psi.ArgumentList.Add(Path.GetFullPath(bridgePath));
        psi.ArgumentList.Add("infer");
        psi.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "PATHEXT" })
            if (Environment.GetEnvironmentVariable(name) is { } value) psi.Environment[name] = value;
        foreach (var name in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "TMPDIR", "TMP", "TEMP" })
            psi.Environment[name] = workingDirectory;
        psi.Environment["PPLLM_RUNNER_HOME"] = Path.GetFullPath(runnerHome);
        psi.Environment["NO_COLOR"] = "1";
        return psi;
    }

    /// <summary>Accepts only a protocol <c>result</c>; a bridge error code is reported as such, never as a bare exit status.</summary>
    public static string ExtractBridgeResult(string events, int exitCode, string stage)
    {
        JsonElement? result = null;
        foreach (var message in BridgeMessages(events))
        {
            var type = message.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "error")
                throw new InvalidDataException($"Claude bridge stage {stage} reported {(message.TryGetProperty("code", out var code) ? code.GetString() : "an unnamed error")}.");
            if (type == "result") result = message;
        }
        if (exitCode != 0) throw new InvalidDataException($"Claude bridge stage {stage} exited {exitCode}.");
        if (result is not { } found || !found.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Claude bridge stage {stage} returned no result.");
        return text.GetString()!;
    }

    private static StageUsage ReadBridgeUsage(string events)
    {
        foreach (var message in BridgeMessages(events))
        {
            if (!message.TryGetProperty("type", out var type) || type.GetString() != "result" || !message.TryGetProperty("usage", out var usage)) continue;
            return new(Count(usage, "input"), Count(usage, "output"), Count(usage, "cacheRead"), Count(usage, "cacheWrite"));
        }
        return new(0, 0);
        static long Count(JsonElement usage, string name) =>
            usage.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) ? count : 0;
    }

    private static IEnumerable<JsonElement> BridgeMessages(string events)
    {
        foreach (var line in events.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonElement root;
            try { using var doc = JsonDocument.Parse(line); root = doc.RootElement.Clone(); }
            catch (JsonException) { continue; } // A truncated tail after a timeout or capture limit is not a protocol message.
            if (root.ValueKind == JsonValueKind.Object) yield return root;
        }
    }

    public static void ValidateHarness(ExperimentOptions options, ExperimentRecipe recipe)
    {
        if (options.Harness is not (CodexHarness or ClaudeHarness)) throw new ArgumentException("Harness must be codex or claude.");
        if (options.Harness != ClaudeHarness) return;
        if (string.IsNullOrWhiteSpace(options.RunnerHome)) throw new ArgumentException("The claude harness requires --runner-home, the directory holding the Claude Code sign-in.");
        if (recipe.Pipeline != "single") throw new InvalidDataException("The claude harness supports only single-stage recipes; every call returns schema-bound structured output.");
    }

    internal static string RunnerDescription(string harness) => harness == ClaudeHarness
        ? $"{RunnerHarness.Claude.BridgeFile} infer ({RunnerHarness.Claude.Fingerprint}; production bridge flags: -p --json-schema --tools '' --strict-mcp-config --setting-sources '' --no-session-persistence --max-turns 3)"
        : "codex exec --json --ephemeral --ignore-user-config --sandbox read-only";
}
