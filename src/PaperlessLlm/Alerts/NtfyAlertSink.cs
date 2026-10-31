using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace PaperlessLlm.Alerts;

public sealed record NtfyOptions(Uri TopicUrl, string? TokenFile = null)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Publishes alerts to one ntfy topic (ntfy.sh or self-hosted) with an optional access token file.</summary>
public sealed partial class NtfyAlertSink : IAlertSink, IDisposable
{
    private readonly NtfyOptions options;
    private readonly HttpClient http;

    public NtfyAlertSink(NtfyOptions options, HttpMessageHandler? handler = null)
    {
        Validate(options);
        this.options = options;
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    private static void Validate(NtfyOptions options)
    {
        if (!IsTopicUrl(options.TopicUrl))
            throw new ArgumentException("PPLLM_NTFY_URL must be an http(s) ntfy topic URL such as https://ntfy.sh/your-topic, without credentials or query.");
        // An access token must not cross the network in clear text.
        if (options.TokenFile is not null && options.TopicUrl.Scheme != "https")
            throw new ArgumentException("PPLLM_NTFY_TOKEN_FILE requires an https PPLLM_NTFY_URL.");
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentException("ntfy timeout must be between zero and one minute.");
    }

    private static bool IsTopicUrl(Uri url) =>
        url.IsAbsoluteUri && url.Scheme is ("http" or "https") && url.UserInfo.Length == 0 && url.Query.Length == 0
        && url.Fragment.Length == 0 && TopicName().IsMatch(url.Segments[^1]);

    public async Task SendAsync(OperatorAlert alert, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.TopicUrl)
        {
            Content = new StringContent(alert.Message, Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("Title", alert.Title);
        request.Headers.Add("Priority", alert.Priority == AlertPriority.High ? "high" : "default");
        request.Headers.Add("Tags", alert.Tag);
        if (options.TokenFile is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await ReadTokenAsync(ct));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new AlertDeliveryException("alert_rejected_" + (int)response.StatusCode);
        }
        catch (HttpRequestException) { throw new AlertDeliveryException("alert_transport_failed"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AlertDeliveryException("alert_timeout"); }
    }

    private async Task<string> ReadTokenAsync(CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(options.TokenFile!);
            if (info.Length is < 1 or > 4096) throw new AlertDeliveryException("alert_token_invalid");
            var token = (await File.ReadAllTextAsync(options.TokenFile!, ct)).Trim();
            if (token.Length == 0 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl)) throw new AlertDeliveryException("alert_token_invalid");
            return token;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new AlertDeliveryException("alert_token_unreadable"); }
    }

    public void Dispose() => http.Dispose();

    [GeneratedRegex("^[-_A-Za-z0-9]{1,64}$")]
    private static partial Regex TopicName();
}
