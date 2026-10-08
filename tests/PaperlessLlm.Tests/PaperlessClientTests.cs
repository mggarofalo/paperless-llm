using System.Net;
using System.Text;
using System.Text.Json;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Tests;

public sealed class PaperlessClientTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-http-test-" + Guid.NewGuid().ToString("N"));
    public PaperlessClientTests() { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "token"), "synthetic-secret"); }
    public void Dispose() => Directory.Delete(directory, true);
    private PaperlessOptions Options(int size = 2, int maxPages = 5, long maxOriginal = 100, int maxJson = 100000, int timeoutMs = 5000) => new()
    {
        BaseUrl = new Uri("https://paperless.example/prefix/"), TokenFile = Path.Combine(directory, "token"),
        PageSize = size, MaxPages = maxPages, MaxOriginalBytes = maxOriginal, MaxJsonBytes = maxJson, RequestTimeout = TimeSpan.FromMilliseconds(timeoutMs)
    };
    private static object Doc(int id, string text = "synthetic OCR") => new { id, title = "Synthetic", content = text, tags = new[] { 2 }, created = "2026-01-01", modified = "2026-01-02" };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Page(object[] rows, string? next = null) => Json(new { count = next is null ? rows.Length : rows.Length * 2, next, results = rows });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Token", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-secret", request.Headers.Authorization?.Parameter);
            Requests.Add(request.RequestUri!);
            return callback(request, ct);
        }
    }
    private static Handler Sequence(params HttpResponseMessage[] responses)
    {
        var queue = new Queue<HttpResponseMessage>(responses);
        return new Handler((_, _) => Task.FromResult(queue.Dequeue()));
    }

    [Fact]
    public async Task NotesAreReadWithoutChangingLegacyMetadataHash()
    {
        var noteDocument = JsonSerializer.SerializeToNode(Doc(1))!;
        noteDocument["notes"] = JsonSerializer.SerializeToNode(new[] { new { id = 7, note = "Human note", created = "2026-01-02", user = new { id = 5 } } });
        using var handler = Sequence(Json(Doc(1)), Json(noteDocument));
        using var client = new PaperlessClient(Options(), handler);
        var old = await client.GetDocumentAsync(1);
        var current = await client.GetDocumentAsync(1);
        Assert.Equal(old.RevisionHash, current.RevisionHash);
        Assert.Null(old.Notes);
        Assert.Equal(new PaperlessNote(7, "Human note"), Assert.Single(current.Notes!));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{\"id\":1,\"note\":42}]")]
    [InlineData("[{\"id\":1,\"note\":\"a\"},{\"id\":1,\"note\":\"b\"}]")]
    public async Task InvalidNotesFailClosed(string notes)
    {
        var document = JsonSerializer.SerializeToNode(Doc(1))!;
        document["notes"] = System.Text.Json.Nodes.JsonNode.Parse(notes);
        using var client = new PaperlessClient(Options(), Sequence(Json(document)));
        await Assert.ThrowsAsync<PaperlessException>(() => client.GetDocumentAsync(1));
    }

    [Fact]
    public async Task DiscoveryResolvesTagPreservesOldestOrderAndRebuildsSafePagination()
    {
        using var handler = Sequence(Page([new { id = 2, name = "Needs Review" }]),
            Page([Doc(1), Doc(2)], "http://paperless.example/prefix/api/documents/?page=2&arbitrary=ignored"), Page([Doc(3), Doc(4)]));
        using var client = new PaperlessClient(Options(), handler);
        var result = await client.ListDocumentsAsync(limit: 2, afterId: 1);
        Assert.Equal(new[] { 2, 3 }, result.Select(d => d.Id));
        Assert.All(handler.Requests, uri => Assert.Equal("https", uri.Scheme));
        Assert.Contains("tags__id__all=2", handler.Requests[1].Query);
        Assert.DoesNotContain("arbitrary", handler.Requests[2].Query);
        Assert.All(result, d => Assert.Equal(64, d.RevisionHash.Length));
    }

    [Fact]
    public async Task TaxonomyAcceptsServerOrderAcrossPages()
    {
        using var handler = Sequence(
            Page([new { id = 9, name = "A" }, new { id = 2, name = "B" }], "?page=2"),
            Page([new { id = 1, name = "C" }]),
            Page([new { id = 7, name = "D" }, new { id = 3, name = "E" }]),
            Page([new { id = 8, name = "F" }, new { id = 4, name = "G" }]));
        using var client = new PaperlessClient(Options(), handler);
        var taxonomy = await client.GetTaxonomyAsync();
        Assert.Equal(new[] { 9, 2, 1 }, taxonomy.Tags.Select(t => t.Id));
        Assert.Equal(new[] { 7, 3 }, taxonomy.Correspondents.Select(t => t.Id));
        Assert.Equal(new[] { 8, 4 }, taxonomy.DocumentTypes.Select(t => t.Id));
    }

    [Fact]
    public async Task TaxonomyStillRejectsDuplicateIdsAcrossPages()
    {
        using var handler = Sequence(Page([new { id = 9, name = "A" }], "?page=2"),
            Page([new { id = 9, name = "A" }]));
        using var client = new PaperlessClient(Options(), handler);
        await Assert.ThrowsAsync<PaperlessException>(() => client.GetTaxonomyAsync());
    }

    [Fact]
    public async Task DocumentsStillRejectMisorderedIds()
    {
        using var client = new PaperlessClient(Options(), Sequence(Page([Doc(2), Doc(1)])));
        await Assert.ThrowsAsync<PaperlessException>(() => client.ListDocumentsAsync(null));
    }

    [Theory]
    [InlineData("https://attacker.invalid/prefix/api/documents/?page=2")]
    [InlineData("https://paperless.example/other/api/documents/?page=2")]
    [InlineData("https://paperless.example/prefix/api/tags/?page=2")]
    [InlineData("https://paperless.example/prefix/api/documents/?page=1")]
    [InlineData("https://paperless.example/prefix/api/documents/?page=2&page=2")]
    [InlineData("https://user@paperless.example/prefix/api/documents/?page=2")]
    [InlineData("http://paperless.example:444/prefix/api/documents/?page=2")]
    public async Task UnsafePaginationNeverTriggersSecondRequest(string next)
    {
        using var handler = Sequence(Page([Doc(1)], next));
        using var client = new PaperlessClient(Options(), handler);
        await Assert.ThrowsAsync<PaperlessException>(() => client.ListDocumentsAsync(null));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BaselineUsesAllDocumentsNewestId()
    {
        using var handler = Sequence(Page([Doc(500)]));
        using var client = new PaperlessClient(Options(), handler);
        Assert.Equal(500, await client.GetLatestDocumentIdAsync());
        Assert.Contains("ordering=-id", handler.Requests[0].Query);
        Assert.Contains("page_size=1", handler.Requests[0].Query);
        Assert.DoesNotContain("tags", handler.Requests[0].Query);
    }

    [Fact]
    public async Task SmallNewDocumentBatchStillScansFullHistoricalPages()
    {
        using var handler = Sequence(Page([Doc(1), Doc(2)], "?page=2"), Page([Doc(3), Doc(4)]));
        using var client = new PaperlessClient(Options(size: 2, maxPages: 2), handler);
        var documents = await client.ListDocumentsAsync(null, limit: 1, afterId: 2);
        Assert.Equal(3, Assert.Single(documents).Id);
        Assert.All(handler.Requests, uri => Assert.Contains("page_size=2", uri.Query));
    }

    [Fact]
    public async Task PaginationCapAndDuplicateRecordsFailClosed()
    {
        using (var client = new PaperlessClient(Options(maxPages: 1), Sequence(Page([Doc(1)], "?page=2"))))
            await Assert.ThrowsAsync<PaperlessException>(() => client.ListDocumentsAsync(null, 10));
        using (var client = new PaperlessClient(Options(), Sequence(Page([Doc(1), Doc(1)]))))
            await Assert.ThrowsAsync<PaperlessException>(() => client.ListDocumentsAsync(null, 10));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"count\":\"wrong\",\"next\":null,\"results\":[]}")]
    [InlineData("{\"count\":1,\"next\":null,\"results\":[{\"id\":true}]}")]
    public async Task InvalidJsonShapeIsSanitized(string text)
    {
        using var client = new PaperlessClient(Options(), Sequence(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) }));
        await Assert.ThrowsAsync<PaperlessException>(() => client.ListDocumentsAsync(null));
    }

    [Fact]
    public async Task WrongDocumentIdentityIsRejected()
    {
        using var client = new PaperlessClient(Options(), Sequence(Json(Doc(2))));
        await Assert.ThrowsAsync<PaperlessException>(() => client.GetDocumentAsync(1));
    }

    [Fact]
    public async Task RevisionChangesWithContentButIgnoresJsonOrder()
    {
        using var client = new PaperlessClient(Options(), Sequence(Json(Doc(1)), Json(Doc(1)), Json(Doc(1, "changed"))));
        var first = await client.GetDocumentAsync(1);
        Assert.Equal(first.RevisionHash, (await client.GetDocumentAsync(1)).RevisionHash);
        Assert.NotEqual(first.RevisionHash, (await client.GetDocumentAsync(1)).RevisionHash);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ErrorBodyAndTokenNeverAppearInException(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("private OCR synthetic-secret") };
        response.Headers.Location = new Uri("https://attacker.invalid/");
        using var client = new PaperlessClient(Options(), Sequence(response));
        var error = await Assert.ThrowsAsync<PaperlessException>(() => client.GetDocumentAsync(1));
        Assert.DoesNotContain("private", error.ToString());
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Fact]
    public async Task TimeoutIsBoundedAndCallerCancellationIsPreserved()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json(Doc(1)); });
        using var client = new PaperlessClient(Options(timeoutMs: 20), handler);
        await Assert.ThrowsAsync<PaperlessException>(() => client.GetDocumentAsync(1));
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetDocumentAsync(1, ct.Token));
    }

    [Fact]
    public async Task DownloadCannotOverwriteAndCleansOversizedPartial()
    {
        var path = Path.Combine(directory, "original");
        using var client = new PaperlessClient(Options(maxOriginal: 5), Sequence(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) }));
        await Assert.ThrowsAsync<PaperlessException>(() => client.DownloadOriginalAsync(1, path));
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(directory));
        await File.WriteAllTextAsync(path, "keep");
        await Assert.ThrowsAsync<PaperlessException>(() => client.DownloadOriginalAsync(1, path));
        Assert.Equal("keep", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DownloadUsesCallerPathAndComputesHash()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("synthetic"u8.ToArray()) };
        response.Content.Headers.ContentType = new("application/pdf");
        response.Content.Headers.ContentDisposition = new("attachment") { FileName = "../../unsafe.pdf" };
        using var client = new PaperlessClient(Options(), Sequence(response));
        var output = await client.DownloadOriginalAsync(5, Path.Combine(directory, "original"));
        Assert.Equal(9, output.Bytes);
        Assert.Equal(64, output.Sha256.Length);
        Assert.Equal("application/pdf", output.MediaType);
        Assert.Equal("synthetic", await File.ReadAllTextAsync(output.Path));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }
    [Fact]
    public async Task CursorSearchRejectsMaliciousPaginationBeforeFollowingIt()
    {
        using var handler = Sequence(Page([Doc(1), Doc(2)], "https://attacker.invalid/api/documents/?page=2"));
        using var client = new PaperlessClient(Options(), handler);
        await Assert.ThrowsAsync<PaperlessException>(() => client.ListDocumentsAsync(null, 1, 10000));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LargeSparseArchiveFindsEveryNewBatchWithinRequestBudget()
    {
        var ids = Enumerable.Range(1, 10250).Select(x => x * 3).ToList();
        using var handler = new Handler((request, _) =>
        {
            var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
            Assert.False(query.ContainsKey("id__gt")); Assert.Equal("id", query["ordering"]);
            int page = int.Parse(query["page"]), size = int.Parse(query["page_size"]);
            var rows = ids.Skip((page - 1) * size).Take(size).Select(x => Doc(x)).ToArray();
            return Task.FromResult(Json(new { count = ids.Count, next = page * size < ids.Count ? $"?page={page + 1}" : null, results = rows }));
        });
        using var client = new PaperlessClient(Options(size: 100, maxPages: 12), handler);
        int cursor = 30000; var discovered = new List<int>();
        while (true)
        {
            int startRequests = handler.Requests.Count;
            var batch = await client.ListDocumentsAsync(null, 100, cursor);
            Assert.InRange(handler.Requests.Count - startRequests, 1, 12);
            if (batch.Count == 0) break;
            discovered.AddRange(batch.Select(x => x.Id)); cursor = batch.Max(x => x.Id);
        }
        Assert.Equal(ids.Where(x => x > 30000), discovered);
        // Rotation to the original baseline finds a newly visible ID without relying on modified/tags.
        ids.Add(30001); ids.Sort();
        var rotated = await client.ListDocumentsAsync(null, 100, 30000);
        Assert.Equal(30001, rotated[0].Id);
    }
}
