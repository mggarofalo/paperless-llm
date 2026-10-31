namespace PaperlessLlm.Runner;

/// <summary>How a harness signs in: NDJSON device codes relayed by .NET, or the harness's own terminal prompt.</summary>
public enum HarnessLogin { DeviceCode, Terminal }

/// <summary>
/// Everything harness-specific that .NET needs: which bridge to start, where its private auth home lives,
/// its default model and login style. Behavior beyond this belongs in the bridge, not in the worker.
/// </summary>
public sealed record RunnerHarness(string Name, string AccountName, string BridgeFile, string AuthSubdirectory,
    string DefaultModel, HarnessLogin Login, string? DeviceLoginUrl, string Fingerprint)
{
    // Fingerprints identify the pinned bridge dependency. Changing one changes every prompt fingerprint.
    public static readonly RunnerHarness Codex = new("codex", "ChatGPT", "bridge.mjs", "pi", "gpt-6-sol",
        HarnessLogin.DeviceCode, "https://auth.openai.com/codex/device", "pi-0.99.2");
    public static readonly RunnerHarness Claude = new("claude", "Claude", "claude-bridge.mjs", "claude", "claude-sonnet-5-5",
        HarnessLogin.Terminal, null, "claude-code-2.1.314");

    public static RunnerHarness Parse(string name) => name switch
    {
        "codex" => Codex,
        "claude" => Claude,
        _ => throw new ArgumentException("PPLLM_HARNESS must be codex or claude.")
    };
}
