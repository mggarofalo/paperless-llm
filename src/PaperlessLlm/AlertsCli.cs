using PaperlessLlm.Alerts;
using PaperlessLlm.Auth;
using PaperlessLlm.Paperless;

namespace PaperlessLlm;

/// <summary>Optional operator alerting configuration. Alerts are disabled when PPLLM_NTFY_URL is empty.</summary>
internal static class AlertsCli
{
    public static NtfyAlertSink? CreateSink(string url, string tokenFile)
    {
        if (url.Length == 0)
        {
            if (tokenFile.Length > 0) throw new ArgumentException("PPLLM_NTFY_TOKEN_FILE requires PPLLM_NTFY_URL.");
            return null;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var topic)) throw new ArgumentException("PPLLM_NTFY_URL must be an absolute ntfy topic URL.");
        return new NtfyAlertSink(new NtfyOptions(topic, tokenFile.Length > 0 ? tokenFile : null));
    }

    public static async Task<int> RunAsync(string[] args, string url, string tokenFile, CancellationToken ct)
    {
        if (args.Length != 2 || args[1] != "test") throw new ArgumentException("Use alerts test.");
        using var sink = CreateSink(url, tokenFile);
        if (sink is null) throw new ArgumentException("Set PPLLM_NTFY_URL to enable alerts.");
        await sink.SendAsync(new("Paperless LLM test alert", "Alert delivery works. No Paperless documents were accessed.", AlertPriority.Default, "bell"), ct);
        Console.WriteLine("Test alert delivered.");
        return 0;
    }

    /// <summary>Credential failures worth an alert, using the worker's pause codes.</summary>
    public static string? AuthCode(Exception exception) => exception switch
    {
        AuthException auth => auth.RequiresSignIn ? "auth_required" : "auth_unavailable",
        PaperlessException { Code: "paperless_authentication_failed" } => "paperless_authentication_failed",
        _ => null
    };

    /// <summary>Alerts on a credential failure during startup checks, then rethrows it unchanged.</summary>
    public static async Task<T> WatchAsync<T>(AuthAlertMonitor? alerts, Func<Task<T>> action, CancellationToken ct)
    {
        try { return await action(); }
        catch (Exception error) when (alerts is not null && AuthCode(error) is not null)
        {
            await alerts.RaiseAsync(AuthCode(error), ct);
            throw;
        }
    }
}
