using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Sync;

public interface IPaperlessWriter
{
    Task PatchAsync(int documentId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct);
}

/// <summary>Narrow mutation boundary; no model-provided paths, methods or field names.</summary>
public sealed class PaperlessWriter : IPaperlessWriter, IDisposable
{
    private readonly PaperlessOptions options;
    private readonly HttpClient http;
    private static readonly HashSet<string> Fields = ["title", "created", "correspondent", "document_type", "tags", "content"];

    public PaperlessWriter(PaperlessOptions options, HttpMessageHandler? handler = null)
    {
        using var validation = new PaperlessClient(options);
        this.options = options;
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
    }

    public async Task PatchAsync(int documentId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
    {
        if (documentId <= 0 || fields.Count == 0 || fields.Keys.Any(f => !Fields.Contains(f)))
            throw new ArgumentException("Invalid managed document patch.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.RequestTimeout);
        try
        {
            if (new FileInfo(options.TokenFile).Length is < 1 or > 4096) throw new PaperlessException("Invalid Paperless token file.");
            var token = (await File.ReadAllTextAsync(options.TokenFile, deadline.Token)).Trim();
            if (token.Length == 0 || token.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
                throw new PaperlessException("Invalid Paperless token file.");
            var uri = new Uri(options.BaseUrl.AbsoluteUri.TrimEnd('/') + $"/api/documents/{documentId}/");
            using var request = new HttpRequestMessage(HttpMethod.Patch, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            request.Content = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new PaperlessException("Paperless update was not confirmed.", response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "paperless_authentication_failed",
                    HttpStatusCode.TooManyRequests => "paperless_rate_limited",
                    _ => "paperless_write_unconfirmed"
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PaperlessException("Paperless update timed out; reconcile before retrying.", "paperless_write_unconfirmed"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        { throw new PaperlessException("Paperless update could not be confirmed.", "paperless_write_unconfirmed"); }
    }

    public void Dispose() => http.Dispose();
}
