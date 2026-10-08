using System.Net;
using System.Text.Json;
using PaperlessLlm.Paperless;
using PaperlessLlm.Sync;
using PaperlessLlm.Intent;

namespace PaperlessLlm.Tests;

public sealed class PaperlessWriterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-writer-" + Guid.NewGuid().ToString("N"));
    public PaperlessWriterTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "token"), "synthetic-token");
    }
    public void Dispose() => Directory.Delete(directory, true);
    private PaperlessOptions Options(int timeout = 5000) => new()
    {
        BaseUrl = new("https://paperless.example/prefix/"), TokenFile = Path.Combine(directory, "token"),
        RequestTimeout = TimeSpan.FromMilliseconds(timeout)
    };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => callback(request, ct);
    }
    [Fact]
    public async Task PatchIsRestrictedToDocumentEndpointAndExactFields()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("https://paperless.example/prefix/api/documents/42/", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Token", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Single(json.RootElement.EnumerateObject());
            Assert.Equal("Synthetic", json.RootElement.GetProperty("title").GetString());
            return new(HttpStatusCode.OK);
        });
        using var writer = new PaperlessWriter(Options(), handler);
        await writer.PatchAsync(42, new Dictionary<string, object?> { ["title"] = "Synthetic" }, default);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.PatchAsync(42,
            new Dictionary<string, object?> { ["owner"] = 2 }, default));
    }
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "paperless_authentication_failed")]
    [InlineData(HttpStatusCode.Forbidden, "paperless_authentication_failed")]
    [InlineData(HttpStatusCode.TooManyRequests, "paperless_rate_limited")]
    [InlineData(HttpStatusCode.Redirect, "paperless_write_unconfirmed")]
    public async Task ErrorsDoNotExposeResponseBodyOrFollowRedirect(HttpStatusCode status, string code)
    {
        int requests = 0;
        using var handler = new Handler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(status) { Content = new StringContent("sensitive-response") };
            response.Headers.Location = new("https://untrusted.example/");
            return Task.FromResult(response);
        });
        using var writer = new PaperlessWriter(Options(), handler);
        var error = await Assert.ThrowsAsync<PaperlessException>(() => writer.PatchAsync(42,
            new Dictionary<string, object?> { ["title"] = "Synthetic" }, default));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("sensitive-response", error.Message);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task NoteUsesAppendEndpointAndCannotReplaceHumanNotes()
    {
        var note = DocumentNotes.Label + "\nSynthetic repair details.";
        using var writer = new PaperlessWriter(Options(), new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://paperless.example/prefix/api/documents/42/notes/", request.RequestUri!.AbsoluteUri);
            Assert.Equal("synthetic-token", request.Headers.Authorization!.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Single(json.RootElement.EnumerateObject());
            Assert.Equal(note, json.RootElement.GetProperty("note").GetString());
            return new(HttpStatusCode.OK);
        }));
        await writer.AddNoteAsync(42, note, default);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.AddNoteAsync(0, note, default));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.AddNoteAsync(42, "Human note", default));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.AddNoteAsync(42, DocumentNotes.Label + "\n ", default));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.AddNoteAsync(42, DocumentNotes.Label + "\n" + new string('x', 1201), default));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.PatchAsync(42, new Dictionary<string, object?> { ["notes"] = new[] { note } }, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "paperless_authentication_failed")]
    [InlineData(HttpStatusCode.TooManyRequests, "paperless_rate_limited")]
    [InlineData(HttpStatusCode.InternalServerError, "paperless_write_unconfirmed")]
    public async Task NoteErrorsUseSafeCodes(HttpStatusCode status, string code)
    {
        using var writer = new PaperlessWriter(Options(), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent("private document text") })));
        var error = await Assert.ThrowsAsync<PaperlessException>(() => writer.AddNoteAsync(1, DocumentNotes.Label + "\nSummary", default));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("private document text", error.Message);
    }
    [Theory]
    [InlineData("")]
    [InlineData("token injected")]
    [InlineData("token\u0001")]
    public async Task InvalidCredentialFilesNeverReachNetwork(string token)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "token"), token);
        var calls = 0;
        using var writer = new PaperlessWriter(Options(), new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        await Assert.ThrowsAsync<PaperlessException>(() => writer.PatchAsync(1, new Dictionary<string, object?> { ["title"] = "New" }, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CallerCancellationIsPreservedForWriteReconciliation()
    {
        using var cancelled = new CancellationTokenSource();
        using var writer = new PaperlessWriter(Options(), new Handler(async (_, ct) => {
            cancelled.Cancel(); await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.PatchAsync(1,
            new Dictionary<string, object?> { ["title"] = "New" }, cancelled.Token));
    }

    [Fact]
    public async Task TimeoutRemainsAnUnconfirmedWriteForJournalReconciliation()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(10000, ct); return new(HttpStatusCode.OK); });
        using var writer = new PaperlessWriter(Options(20), handler);
        var error = await Assert.ThrowsAsync<PaperlessException>(() => writer.PatchAsync(42,
            new Dictionary<string, object?> { ["title"] = "Synthetic" }, default));
        Assert.Equal("paperless_write_unconfirmed", error.Code);
    }
}
