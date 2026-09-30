using System.Net;
using System.Text;
using System.Text.Json;
using PaperlessLlm.Auth;
using PaperlessLlm.Inference;

namespace PaperlessLlm.Tests;

public sealed class ResponsesClientTests
{
    [Fact]
    public async Task RequestUsesOAuthAndOmitsToolsAndReturnsOnlyCompletedOutput()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.False(root.TryGetProperty("tools", out _));
            Assert.False(root.TryGetProperty("max_output_tokens", out _));
            Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            return StreamResponse(Event(new { type = "response.output_text.delta", delta = "UNTRUSTED PARTIAL" }) + Completed());
        }));
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        string result = await new ResponsesClient(http, new Tokens()).CompleteAsync("gpt-6-luna", "Instructions", [new("untrusted scan")], schema.RootElement);
        Assert.Equal("{\"answer\":42}", result);
    }

    [Theory]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    [InlineData("error")]
    public async Task LateFailureNeverReleasesPartialText(string failure)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            Event(new { type = "response.output_text.delta", delta = "looks valid" }) + Event(new { type = failure })));
        await Assert.ThrowsAsync<InferenceException>(() => ResponsesClient.ReadCompletedAsync(stream));
    }

    [Fact]
    public async Task TruncatedStreamIsRejected()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Event(new { type = "response.output_text.delta", delta = "{}" })));
        await Assert.ThrowsAsync<InferenceException>(() => ResponsesClient.ReadCompletedAsync(stream));
    }

    [Fact]
    public async Task ToolOutputEvenInCompletedResponseIsRejected()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Event(new { type = "response.completed", response = new
        { status = "completed", output = new[] { new { type = "function_call", name = "danger" } } } })));
        await Assert.ThrowsAsync<InferenceException>(() => ResponsesClient.ReadCompletedAsync(stream));
    }

    [Fact]
    public async Task ExternalImageUrlsRejectedBeforeTokenRead()
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("Must not send")));
        await Assert.ThrowsAsync<ArgumentException>(() => new ResponsesClient(http, new Tokens()).CompleteAsync("gpt-6-luna", "x", [new("x", ["https://private.example/scan"])]));
    }

    [Fact]
    public async Task HttpUnauthorizedHasTypedSignInRequiredSignalAndNoRemoteBody()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("private error") })));
        var error = await Assert.ThrowsAsync<AuthException>(() => new ResponsesClient(http, new Tokens())
            .CompleteAsync("gpt-6-luna", "x", [new("x")]));
        Assert.True(error.RequiresSignIn);
        Assert.DoesNotContain("private error", error.Message);
    }

    private static string Completed() => Event(new { type = "response.completed", response = new
    {
        status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "{\"answer\":42}" } } } }
    } });
    private static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private static HttpResponseMessage StreamResponse(string content) => new(HttpStatusCode.OK)
    { Content = new StringContent(content, Encoding.UTF8, "text/event-stream") };
    private sealed class Tokens : IAccessTokenProvider
    { public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("synthetic-token"); }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
