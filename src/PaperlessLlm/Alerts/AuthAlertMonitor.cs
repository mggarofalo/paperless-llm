using System.Text.Json;
using Microsoft.Extensions.Logging;
using PaperlessLlm.Review;

namespace PaperlessLlm.Alerts;

/// <summary>
/// Turns credential pauses into at most one alert per outage plus one recovery notice. The outstanding
/// alert is durable, so a worker restart loop on missing sign-in does not repeat it. Delivery failures
/// are logged by code and retried on the next observation; they never block or fail processing.
/// </summary>
public sealed class AuthAlertMonitor(IAlertSink sink, string stateDirectory, string accountName, ILogger logger, TimeProvider? clock = null)
{
    private const string PaperlessCode = "paperless_authentication_failed";
    private sealed record Outstanding(string Code, DateTimeOffset RaisedAt);
    private string StatePath => Path.Combine(Path.GetFullPath(stateDirectory), "alerts.json");

    public static bool IsAuthCode(string? code) => code is "auth_required" or "auth_unavailable" or PaperlessCode;

    public async Task RaiseAsync(string? code, CancellationToken ct)
    {
        if (!IsAuthCode(code) || (await ReadAsync(ct))?.Code == code) return;
        if (await DeliverAsync(PauseAlert(code!), ct))
            await WriteAsync(new Outstanding(code!, (clock ?? TimeProvider.System).GetUtcNow()), ct);
    }

    /// <summary>Paperless access is proven by any successful cycle; harness sign-in only by a completed inference.</summary>
    public async Task ResolveAsync(bool inferenceVerified, CancellationToken ct)
    {
        var current = await ReadAsync(ct);
        if (current is null || !inferenceVerified && current.Code != PaperlessCode) return;
        if (!await DeliverAsync(new("Paperless LLM resumed", $"Processing resumed after {Describe(current.Code)}.", AlertPriority.Default, "white_check_mark"), ct))
            return;
        try { File.Delete(StatePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logger.LogWarning("Operator alert state could not be cleared"); }
    }

    private OperatorAlert PauseAlert(string code) => code switch
    {
        "auth_required" => new($"Paperless LLM paused: {accountName} sign-in required",
            $"{accountName} credentials are missing, expired or revoked, so document processing is paused. Run the auth login setup command, then restart the worker.",
            AlertPriority.High, "warning"),
        "auth_unavailable" => new($"Paperless LLM paused: {accountName} sign-in check failed",
            $"The worker could not confirm {accountName} sign-in. It retries each poll; run auth status to inspect.",
            AlertPriority.High, "warning"),
        _ => new("Paperless LLM paused: Paperless token rejected",
            "Paperless rejected the service account token. Replace secrets/paperless_token.txt with a valid token, then restart the worker.",
            AlertPriority.High, "warning")
    };

    private string Describe(string code) => code == PaperlessCode ? "the Paperless token was rejected" : $"{accountName} sign-in was required";

    private async Task<bool> DeliverAsync(OperatorAlert alert, CancellationToken ct)
    {
        try
        {
            await sink.SendAsync(alert, ct);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Sink exception text is not logged; it could echo a URL or response body.
            logger.LogWarning("Operator alert not delivered: {Code}", error is AlertDeliveryException ? error.Message : "alert_failed");
            return false;
        }
    }

    private async Task<Outstanding?> ReadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            return JsonSerializer.Deserialize<Outstanding>(await File.ReadAllTextAsync(StatePath, ct));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // Best effort: an unwritable state volume may repeat an alert, but must never stop the worker.
    private async Task WriteAsync(Outstanding value, CancellationToken ct)
    {
        var temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            AuditWriter.PrivateDirectory(Path.GetDirectoryName(StatePath)!);
            await AuditWriter.WritePrivateAsync(temporary, JsonSerializer.Serialize(value), ct);
            File.Move(temporary, StatePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logger.LogWarning("Operator alert state could not be saved"); }
        finally { TryDelete(temporary); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
