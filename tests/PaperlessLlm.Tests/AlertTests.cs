using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PaperlessLlm.Alerts;
using PaperlessLlm.Auth;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Tests;

internal sealed class RecordingSink : IAlertSink
{
    public List<OperatorAlert> Sent { get; } = [];
    public bool Fail;
    public Task SendAsync(OperatorAlert alert, CancellationToken ct)
    {
        if (Fail) throw new AlertDeliveryException("alert_transport_failed");
        lock (Sent) Sent.Add(alert);
        return Task.CompletedTask;
    }
}

public sealed class AlertTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ppllm-alerts-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingSink sink = new();
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private AuthAlertMonitor Monitor() => new(sink, root, "Claude", NullLogger.Instance);

    [Fact]
    public async Task OneAlertPerOutageSurvivesRestartsAndNamesTheAccount()
    {
        await Monitor().RaiseAsync("auth_required", default);
        await Monitor().RaiseAsync("auth_required", default); // A restarted worker reads the durable state.
        var alert = Assert.Single(sink.Sent);
        Assert.Equal(AlertPriority.High, alert.Priority);
        Assert.Contains("Claude sign-in required", alert.Title);
        await Monitor().RaiseAsync("paperless_authentication_failed", default);
        Assert.Equal(2, sink.Sent.Count);
        Assert.Contains("Paperless token", sink.Sent[1].Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("rate_limited")]
    [InlineData("organization_prompt_unreadable_or_invalid")]
    public async Task NonCredentialPausesNeverAlert(string? code)
    {
        await Monitor().RaiseAsync(code, default);
        Assert.Empty(sink.Sent);
        Assert.False(File.Exists(Path.Combine(root, "alerts.json")));
    }

    [Fact]
    public async Task FailedDeliveryNeitherThrowsNorSuppressesTheNextAttempt()
    {
        sink.Fail = true;
        await Monitor().RaiseAsync("auth_required", default);
        sink.Fail = false;
        await Monitor().RaiseAsync("auth_required", default);
        Assert.Single(sink.Sent);
    }

    [Fact]
    public async Task HarnessOutageResolvesOnlyAfterAnInferenceSucceeds()
    {
        await Monitor().ResolveAsync(true, default);
        Assert.Empty(sink.Sent); // Nothing outstanding, nothing to announce.
        await Monitor().RaiseAsync("auth_required", default);
        await Monitor().ResolveAsync(false, default);
        Assert.Single(sink.Sent);
        await Monitor().ResolveAsync(true, default);
        Assert.Equal(("Paperless LLM resumed", AlertPriority.Default), (sink.Sent[1].Title, sink.Sent[1].Priority));
        Assert.Contains("Claude sign-in", sink.Sent[1].Message);
        await Monitor().ResolveAsync(true, default);
        Assert.Equal(2, sink.Sent.Count);
    }

    [Fact]
    public async Task PaperlessOutageResolvesOnAnyHealthyCycle()
    {
        await Monitor().RaiseAsync("paperless_authentication_failed", default);
        await Monitor().ResolveAsync(false, default);
        Assert.Equal(2, sink.Sent.Count);
        Assert.False(File.Exists(Path.Combine(root, "alerts.json")));
    }

    [Fact]
    public async Task UnreadableStateIsTreatedAsNoOutstandingAlert()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "alerts.json"), "not json");
        await Monitor().RaiseAsync("auth_required", default);
        Assert.Single(sink.Sent);
    }

    [Fact]
    public async Task StartupWatchAlertsOnCredentialFailuresAndRethrows()
    {
        var monitor = Monitor();
        await Assert.ThrowsAsync<AuthException>(() => AlertsCli.WatchAsync<int>(monitor, () => throw new AuthException("x", true), default));
        await Assert.ThrowsAsync<PaperlessException>(() => AlertsCli.WatchAsync<int>(monitor, () => throw new PaperlessException("x", "paperless_authentication_failed"), default));
        await Assert.ThrowsAsync<PaperlessException>(() => AlertsCli.WatchAsync<int>(monitor, () => throw new PaperlessException("x", "paperless_unavailable"), default));
        Assert.Equal(2, sink.Sent.Count);
        Assert.Equal(7, await AlertsCli.WatchAsync(null, () => Task.FromResult(7), default));
    }

    [Theory]
    [InlineData("", "")]
    public void AlertsAreDisabledWithoutATopic(string url, string token) => Assert.Null(AlertsCli.CreateSink(url, token));

    [Theory]
    [InlineData("", "/run/secrets/ntfy_token")]
    [InlineData("ntfy.sh/topic", "")]
    [InlineData("ftp://ntfy.sh/topic", "")]
    [InlineData("https://user:pass@ntfy.sh/topic", "")]
    [InlineData("https://ntfy.sh/topic?auth=secret", "")]
    [InlineData("https://ntfy.sh/", "")]
    [InlineData("https://ntfy.sh/bad.topic", "")]
    [InlineData("http://ntfy.lan/topic", "/run/secrets/ntfy_token")]
    public void InvalidOrUnsafeConfigurationFailsWithoutFallback(string url, string token) =>
        Assert.Throws<ArgumentException>(() => AlertsCli.CreateSink(url, token));

    [Fact]
    public async Task NtfyRequestCarriesOnlyTheAlertAndBearerToken()
    {
        Directory.CreateDirectory(root);
        var tokenFile = Path.Combine(root, "token");
        await File.WriteAllTextAsync(tokenFile, "tk_synthetic\n");
        var handler = new Handler(HttpStatusCode.OK);
        using var ntfy = new NtfyAlertSink(new(new Uri("https://ntfy.example/ppllm"), tokenFile), handler);
        await ntfy.SendAsync(new("Title", "Body", AlertPriority.High, "warning"), default);
        var request = handler.Request!;
        Assert.Equal((HttpMethod.Post, "https://ntfy.example/ppllm"), (request.Method, request.RequestUri!.AbsoluteUri));
        Assert.Equal(("Title", "high", "warning"), (request.Headers.GetValues("Title").Single(), request.Headers.GetValues("Priority").Single(), request.Headers.GetValues("Tags").Single()));
        Assert.Equal("Bearer tk_synthetic", request.Headers.Authorization!.ToString());
        Assert.Equal("Body", handler.Body);
    }

    [Fact]
    public async Task NtfyFailuresAreReportedByCodeOnly()
    {
        using var rejected = new NtfyAlertSink(new(new Uri("https://ntfy.example/ppllm")), new Handler(HttpStatusCode.Forbidden));
        Assert.Equal("alert_rejected_403", (await Assert.ThrowsAsync<AlertDeliveryException>(() => rejected.SendAsync(Alert, default))).Message);
        using var down = new NtfyAlertSink(new(new Uri("https://ntfy.example/ppllm")), new Handler(throws: true));
        Assert.Equal("alert_transport_failed", (await Assert.ThrowsAsync<AlertDeliveryException>(() => down.SendAsync(Alert, default))).Message);
        using var slow = new NtfyAlertSink(new(new Uri("https://ntfy.example/ppllm")) { Timeout = TimeSpan.FromMilliseconds(100) }, new Handler(hang: true));
        Assert.Equal("alert_timeout", (await Assert.ThrowsAsync<AlertDeliveryException>(() => slow.SendAsync(Alert, default))).Message);
        using var missing = new NtfyAlertSink(new(new Uri("https://ntfy.example/ppllm"), Path.Combine(root, "absent")), new Handler(HttpStatusCode.OK));
        Assert.Equal("alert_token_unreadable", (await Assert.ThrowsAsync<AlertDeliveryException>(() => missing.SendAsync(Alert, default))).Message);
    }

    private static readonly OperatorAlert Alert = new("Title", "Body", AlertPriority.Default, "bell");

    private sealed class Handler(HttpStatusCode status = HttpStatusCode.OK, bool throws = false, bool hang = false) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            if (throws) throw new HttpRequestException("private https://ntfy.example/ppllm");
            if (hang) await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(status);
        }
    }
}
