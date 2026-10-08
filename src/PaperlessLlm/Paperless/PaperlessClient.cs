using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaperlessLlm.Paperless;

/// <summary>GET-only API client. Never includes credentials, response bodies, or URLs in errors.</summary>
public sealed class PaperlessClient : IPaperlessClient, IDisposable
{
    private readonly PaperlessOptions options;
    private readonly HttpClient http;
    private readonly Uri api;

    public PaperlessClient(PaperlessOptions options, HttpMessageHandler? handler = null)
    {
        ValidateOptions(options);
        this.options = options;
        api = new Uri(options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/api/");
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static void ValidateOptions(PaperlessOptions options)
    {
        if (!options.BaseUrl.IsAbsoluteUri || options.BaseUrl.Scheme is not ("http" or "https") ||
            options.BaseUrl.UserInfo.Length != 0 || options.BaseUrl.Query.Length != 0 || options.BaseUrl.Fragment.Length != 0)
            throw new ArgumentException("Paperless URL must be an HTTP(S) instance URL without credentials, query, or fragment.");
        ValidateLimits(options);
    }
    private static void ValidateLimits(PaperlessOptions options)
    {
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(10) ||
            options.PageSize is < 1 or > 100 || options.MaxPages is < 1 or > 100 || options.MaxOriginalBytes <= 0 || options.MaxJsonBytes <= 0)
            throw new ArgumentException("Invalid Paperless request limits.");
    }

    private bool IsProxyUpgrade(Uri uri) => api.Scheme == "https" && api.IsDefaultPort && uri.Scheme == "http" && uri.IsDefaultPort;

    private void ValidatePageOrigin(Uri uri, string resource)
    {
        // A reverse proxy can advertise HTTP links for an HTTPS API. Upgrade only
        // the same host's default-port link; credentials always use configured API.
        var proxyUpgrade = IsProxyUpgrade(uri);
        if ((uri.Scheme != api.Scheme && !proxyUpgrade) || !string.Equals(uri.Host, api.Host, StringComparison.OrdinalIgnoreCase) ||
            (uri.Port != api.Port && !proxyUpgrade) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != new Uri(api, resource + "/").AbsolutePath)
            throw new PaperlessException("Paperless pagination left the configured API origin or resource.");
    }

    private static (JsonElement Rows, int Count, JsonElement Next) ReadPage(JsonElement root, int size)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new PaperlessException("Paperless returned an invalid result page.");
        var count = ReadCount(root);
        if (!root.TryGetProperty("next", out var next) || rows.GetArrayLength() > size)
            throw new PaperlessException("Paperless returned an invalid result page.");
        return (rows, count, next);
    }
    private static int ReadCount(JsonElement root)
    {
        if (!root.TryGetProperty("count", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var value) || value < 0)
            throw new PaperlessException("Paperless returned an invalid result page.");
        return value;
    }

    private static bool Misordered(string resource, int? previous, int id, bool descending) =>
        resource == "documents" && previous is not null && (descending ? id >= previous : id <= previous);

    private async Task<(JsonElement[] Rows, int Count, bool More)> ReadCursorPageAsync(int page, int? tag,
        Dictionary<int, (JsonElement[] Rows, int Count, bool More)> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(page, out var cached)) return cached;
        if (cache.Count >= options.MaxPages) throw new PaperlessException("Paperless exceeded the pagination safety limit.");
        using var result = await JsonAsync(PageUri("documents", page, options.PageSize, tag), ct);
        var (rows, total, next) = ReadPage(result.RootElement, options.PageSize);
        var more = next.ValueKind != JsonValueKind.Null;
        if (more) ValidateNext(next, "documents", page + 1);
        var copied = rows.EnumerateArray().Select(x => x.Clone()).ToArray();
        int previous = 0;
        foreach (var row in copied)
        {
            var id = RequiredId(row, "id");
            if (id <= previous) throw new PaperlessException("Paperless pagination repeated or misordered a resource.");
            previous = id;
        }
        if (more && copied.Length == 0) throw new PaperlessException("Paperless pagination made no progress.");
        var value = (copied, total, more); cache.Add(page, value); return value;
    }

    private async Task<int> FindCursorPageAsync((JsonElement[] Rows, int Count, bool More) first, int afterId, int? tag,
        Dictionary<int, (JsonElement[] Rows, int Count, bool More)> cache, CancellationToken ct)
    {
        int low = 1, high = Math.Max(1, (int)(((long)first.Count + options.PageSize - 1) / options.PageSize));
        if (RequiredId(first.Rows[^1], "id") <= afterId)
        {
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                var candidate = await ReadCursorPageAsync(middle, tag, cache, ct);
                if (candidate.Rows.Length == 0) throw new PaperlessException("Paperless collection changed during discovery; retry.");
                if (RequiredId(candidate.Rows[^1], "id") <= afterId) low = middle + 1;
                else high = middle;
            }
        }
        return low;
    }

    private async Task DownloadToStageAsync(HttpResponseMessage response, string stage, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > options.MaxOriginalBytes) throw new PaperlessException("Original exceeded the download size limit.");
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var length = await CopyBoundedAsync(source, output, options.MaxOriginalBytes, ct);
            if (length == 0) throw new PaperlessException("Original document is empty.");
        }
    }

    public void Dispose() => http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct)
    {
        // Load a dedicated deployment secret; never inspect other applications' auth stores.
        string token;
        try
        {
            var info = new FileInfo(options.TokenFile);
            if (info.Length is < 1 or > 4096) throw new PaperlessException("Invalid Paperless token file.");
            token = (await File.ReadAllTextAsync(options.TokenFile, ct)).Trim();
            if (token.Length == 0 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
                throw new PaperlessException("Invalid Paperless token file.");
        }
        catch (IOException) { throw new PaperlessException("Paperless token file could not be read."); }
        catch (UnauthorizedAccessException) { throw new PaperlessException("Paperless token file could not be read."); }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("PaperlessLlm/1.0");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "paperless_authentication_failed",
                HttpStatusCode.TooManyRequests => "paperless_rate_limited",
                HttpStatusCode.NotFound => "paperless_not_found",
                _ => "paperless_http_error"
            };
            response.Dispose();
            throw new PaperlessException("Paperless request failed; check credentials, permissions, and connectivity.", code);
        }
        return response;
    }

    private CancellationTokenSource RequestCancellation(CancellationToken ct)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(options.RequestTimeout);
        return cancellation;
    }

    private async Task<JsonDocument> JsonAsync(Uri uri, CancellationToken ct)
    {
        using var cancellation = RequestCancellation(ct);
        try
        {
            using var response = await SendAsync(uri, cancellation.Token);
            if (response.Content.Headers.ContentLength > options.MaxJsonBytes)
                throw new PaperlessException("Paperless JSON response exceeded the size limit.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellation.Token);
            using var target = new MemoryStream();
            await CopyBoundedAsync(source, target, options.MaxJsonBytes, cancellation.Token);
            return JsonDocument.Parse(target.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PaperlessException("Paperless request timed out."); }
        catch (HttpRequestException) { throw new PaperlessException("Paperless connection failed."); }
        catch (JsonException) { throw new PaperlessException("Paperless returned invalid JSON."); }
        catch (IOException) { throw new PaperlessException("Paperless response could not be read."); }
    }

    internal static async Task<long> CopyBoundedAsync(Stream source, Stream target, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > limit) throw new PaperlessException("Paperless response exceeded the size limit.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }

    private Uri PageUri(string resource, int page, int size, int? tag = null, bool descending = false) =>
        new(api, $"{resource}/?page={page}&page_size={size}&ordering={(descending ? "-id" : "id")}" + (tag is null ? "" : $"&tags__id__all={tag}"));

    private void ValidateNext(JsonElement next, string resource, int expectedPage)
    {
        if (next.ValueKind != JsonValueKind.String || !Uri.TryCreate(new Uri(api, resource + "/"), next.GetString(), out var uri))
            throw new PaperlessException("Paperless returned invalid pagination.");
        ValidatePageOrigin(uri, resource);
        var pages = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
            .Where(x => x[0] == "page").ToArray();
        if (pages.Length != 1 || pages[0].Length != 2 || !int.TryParse(pages[0][1], out var value) || value != expectedPage)
            throw new PaperlessException("Paperless returned nonsequential pagination.");
    }

    private static bool ReachedLimit(int count, int? take) => take is not null && count >= take;

    private async Task<IReadOnlyList<JsonElement>> ListAsync(string resource, int? tag, CancellationToken ct,
        int? take = null, bool descending = false)
    {
        var values = new List<JsonElement>();
        var seen = new HashSet<int>();
        int? previousId = null;
        for (var page = 1; page <= options.MaxPages; page++)
        {
            var size = Math.Min(options.PageSize, take ?? options.PageSize);
            using var result = await JsonAsync(PageUri(resource, page, size, tag, descending), ct);
            var (rows, _, next) = ReadPage(result.RootElement, size);
            if (next.ValueKind != JsonValueKind.Null) ValidateNext(next, resource, page + 1);
            foreach (var row in rows.EnumerateArray())
            {
                var id = RequiredId(row, "id");
                // Taxonomy endpoints may ignore ordering=id and return name order.
                // Only document discovery relies on monotonic IDs; reject duplicates everywhere.
                if (!seen.Add(id) || Misordered(resource, previousId, id, descending))
                    throw new PaperlessException("Paperless pagination repeated or misordered a resource.");
                previousId = id;
                values.Add(row.Clone());
                if (ReachedLimit(values.Count, take)) return values;
            }
            if (next.ValueKind == JsonValueKind.Null) return values;
            if (rows.GetArrayLength() == 0) throw new PaperlessException("Paperless pagination made no progress.");
        }
        throw new PaperlessException("Paperless exceeded the pagination safety limit.");
    }

    // Paperless supports ordering and page offsets, but does not support id__gt.
    // Locate the cursor page by binary search so old archives do not consume the
    // request budget. Return oldest unseen IDs so a large backlog cannot be skipped.
    private async Task<IReadOnlyList<JsonElement>> ListAfterAsync(int? tag, int afterId, int take, CancellationToken ct)
    {
        var cache = new Dictionary<int, (JsonElement[] Rows, int Count, bool More)>();
        var first = await ReadCursorPageAsync(1, tag, cache, ct);
        if (first.Rows.Length == 0) return [];
        var low = await FindCursorPageAsync(first, afterId, tag, cache, ct);
        var values = new List<JsonElement>();
        int previousId = 0;
        for (int page = low; ; page++)
        {
            var current = await ReadCursorPageAsync(page, tag, cache, ct);
            foreach (var row in current.Rows)
            {
                int id = RequiredId(row, "id");
                if (id <= previousId) throw new PaperlessException("Paperless pagination repeated or misordered a resource.");
                previousId = id;
                if (id > afterId) values.Add(row);
                if (values.Count == take) return values;
            }
            if (!current.More) return values;
        }
    }
    public async Task<IReadOnlyList<PaperlessDocument>> ListDocumentsAsync(string? tagName = "needs review", int limit = 10, int? afterId = null, CancellationToken ct = default)
    {
        if (limit is < 1 or > 1000 || afterId < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        int? tag = null;
        if (tagName is not null)
        {
            if (string.IsNullOrWhiteSpace(tagName)) throw new ArgumentException("Review tag must be nonblank.");
            var tags = await TaxonomyListAsync("tags", ct);
            var matches = tags.Where(x => string.Equals(x.Name, tagName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new PaperlessException("Review tag was not found or is ambiguous.");
            tag = matches[0].Id;
        }
        return (afterId is { } cursor ? await ListAfterAsync(tag, cursor, limit, ct)
            : await ListAsync("documents", tag, ct, limit)).Select(ParseDocument).ToArray();
    }

    public async Task<int> GetLatestDocumentIdAsync(CancellationToken ct = default)
    {
        var rows = await ListAsync("documents", null, ct, 1, descending: true);
        return rows.Count == 0 ? 0 : RequiredId(rows[0], "id");
    }

    public async Task<PaperlessDocument> GetDocumentAsync(int id, CancellationToken ct = default)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        using var json = await JsonAsync(new Uri(api, $"documents/{id}/"), ct);
        var document = ParseDocument(json.RootElement);
        if (document.Id != id) throw new PaperlessException("Paperless returned a different document.");
        return document;
    }

    public async Task<PaperlessTaxonomy> GetTaxonomyAsync(CancellationToken ct = default) =>
        new(await TaxonomyListAsync("tags", ct), await TaxonomyListAsync("correspondents", ct), await TaxonomyListAsync("document_types", ct));

    private async Task<IReadOnlyList<NamedEntity>> TaxonomyListAsync(string resource, CancellationToken ct) =>
        (await ListAsync(resource, null, ct)).Select(x => new NamedEntity(RequiredId(x, "id"), RequiredString(x, "name"),
            x.TryGetProperty("is_inbox_tag", out var inbox) && inbox.ValueKind == JsonValueKind.True)).ToArray();

    private static int RequiredId(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out var id) || id <= 0)
            throw new PaperlessException("Paperless returned an invalid resource identity.");
        return id;
    }
    private static string RequiredString(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String)
            throw new PaperlessException("Paperless returned invalid document text or taxonomy.");
        return field.GetString()!;
    }
    private static string? OptionalString(JsonElement value, string key) =>
        !value.TryGetProperty(key, out var field) || field.ValueKind == JsonValueKind.Null ? null : RequiredString(value, key);
    private static int? OptionalId(JsonElement value, string key) =>
        !value.TryGetProperty(key, out var field) || field.ValueKind == JsonValueKind.Null ? null : RequiredId(value, key);

    private static PaperlessDocument ParseDocument(JsonElement value)
    {
        var id = RequiredId(value, "id");
        if (!value.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array ||
            tags.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out var n) || n <= 0))
            throw new PaperlessException("Paperless returned invalid document tags.");
        var document = new PaperlessDocument(id, RequiredString(value, "title"), RequiredString(value, "content"), OptionalString(value, "created"),
            OptionalString(value, "modified"), OptionalId(value, "correspondent"), OptionalId(value, "document_type"),
            tags.EnumerateArray().Select(x => x.GetInt32()).Distinct().Order().ToArray(), OptionalString(value, "mime_type"), OptionalString(value, "original_file_name"), "");
        // Preserve the metadata hash used by saved v1 jobs; notes are compared separately.
        return document with
        {
            RevisionHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document))),
            Notes = ParseNotes(value)
        };
    }

    private static IReadOnlyList<PaperlessNote>? ParseNotes(JsonElement value)
    {
        if (!value.TryGetProperty("notes", out var notes)) return null;
        if (notes.ValueKind != JsonValueKind.Array) throw new PaperlessException("Invalid document notes.");
        var parsed = notes.EnumerateArray().Select(n => new PaperlessNote(RequiredId(n, "id"), RequiredString(n, "note")))
            .OrderBy(n => n.Id).ToArray();
        if (parsed.Select(n => n.Id).Distinct().Count() != parsed.Length)
            throw new PaperlessException("Duplicate document note identity.");
        return parsed;
    }

    private static void RequireNewDestination(string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination)) throw new PaperlessException("Original destination already exists.");
    }

    public async Task<OriginalDocument> DownloadOriginalAsync(int id, string destination, CancellationToken ct = default)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        destination = Path.GetFullPath(destination);
        RequireNewDestination(destination);
        var stage = Path.Combine(Path.GetDirectoryName(destination)!, ".ppllm-" + Guid.NewGuid().ToString("N"));
        using var cancellation = RequestCancellation(ct);
        try
        {
            using var response = await SendAsync(new Uri(api, $"documents/{id}/download/?original=true"), cancellation.Token);
            await DownloadToStageAsync(response, stage, cancellation.Token);
            string hash;
            await using (var input = File.OpenRead(stage)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellation.Token));
            var bytes = new FileInfo(stage).Length;
            File.Move(stage, destination, false);
            return new OriginalDocument(destination, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", hash, bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PaperlessException("Original download timed out."); }
        catch (HttpRequestException) { throw new PaperlessException("Original download connection failed."); }
        catch (IOException) { throw new PaperlessException("Original download or local file operation failed."); }
        catch (UnauthorizedAccessException) { throw new PaperlessException("Original download could not access its destination."); }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
