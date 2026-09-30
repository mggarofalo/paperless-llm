using System.Text.Json;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;

namespace PaperlessLlm.Tests;

public sealed class AuditReportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-audit-test-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a2ioAAAAASUVORK5CYII=");
    public AuditReportTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private static AuditRecord Record(string? sourceUrl = "https://paperless.example/archive", string? ocr = "SYNTHETIC RECEIPT\nTOTAL -15.99") => new(
        DateTimeOffset.Parse("2026-01-01T10:00:00Z"), "proposal_ready", "synthetic-model",
        new PaperlessDocument(42, "Synthetic <script>alert('title')</script>", "SYNTHETIC RECEIPT\nTOTAL 15.99", "2026-01-01T00:00:00Z", "2026-01-02", 1, 1, [2], "image/png", "original.png", "revision-hash"),
        new PaperlessTaxonomy([new(2, "needs review", true), new(12, "receipts")], [new(1, "Synthetic Shop"), new(3, "Correct Shop")], [new(1, "Receipt")]),
        true, "Synthetic prompt <img src=x onerror=alert(1)>", "Synthetic raw response </pre><script>secret()</script>",
        JsonSerializer.SerializeToElement(new
        {
            title = "Corrected receipt", date = (string?)null, correspondent = 3, document_type = (int?)null,
            add_tags = new[] { 12 }, ocr_text = ocr, evidence = new[] { "Printed refund amount is negative.", "<script>evidence()</script>" },
            uncertainty = new[] { "Check the handwritten note." }
        }), null, sourceUrl);

    [Fact]
    public async Task ReportComparesResolvedMetadataAndPreservesOriginalTags()
    {
        var path = await new AuditWriter(Path.Combine(directory, "audits")).WriteAsync(Record());
        var html = await File.ReadAllTextAsync(Path.Combine(path, "review.html"));
        Assert.Contains("Metadata comparison", html);
        Assert.Contains("Synthetic Shop", html);
        Assert.Contains("Correct Shop", html);
        Assert.Contains("needs review, receipts", html);
        Assert.Contains("Add only: receipts", html);
        Assert.Contains("No change proposed", html);
        Assert.Contains("Printed refund amount is negative.", html);
        Assert.Contains("Check the handwritten note.", html);
        Assert.Contains("Nothing was changed in Paperless.", html);
    }

    [Fact]
    public async Task OcrHighlightsChangedSignAndKeepsBothTexts()
    {
        var path = await new AuditWriter(Path.Combine(directory, "audits")).WriteAsync(Record());
        var html = await File.ReadAllTextAsync(Path.Combine(path, "review.html"));
        Assert.Contains("class=\"line removed\">TOTAL 15.99</span>", html);
        Assert.Contains("class=\"line added\">TOTAL -15.99</span>", html);
        Assert.Contains("aria-label=\"Existing OCR\"", html);
        Assert.Contains("aria-label=\"Proposed OCR\"", html);
        Assert.Contains("class=\"line \">SYNTHETIC RECEIPT</span>", html);
    }

    [Fact]
    public async Task NullOcrIsExplicitAbstentionNotEmptyReplacement()
    {
        var path = await new AuditWriter(Path.Combine(directory, "audits")).WriteAsync(Record(ocr: null));
        var html = await File.ReadAllTextAsync(Path.Combine(path, "review.html"));
        Assert.Contains("No OCR replacement proposed", html);
        Assert.Contains("No replacement proposed.", html);
        Assert.Contains("TOTAL 15.99", html);
        Assert.DoesNotContain("class=\"line removed\">", html);
    }

    [Theory]
    [InlineData("https://paperless.example/archive")]
    [InlineData("https://paperless.example/archive/")]
    public async Task SourceLinksPreserveDeploymentSubpath(string sourceUrl)
    {
        var path = await new AuditWriter(Path.Combine(directory, "audits")).WriteAsync(Record(sourceUrl));
        var html = await File.ReadAllTextAsync(Path.Combine(path, "review.html"));
        Assert.Contains("href=\"https://paperless.example/archive/documents/42/details\"", html);
        Assert.DoesNotContain("href=\"https://paperless.example/documents", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://secret@paperless.example/archive")]
    public async Task InvalidOrCredentialBearingSourceLinkIsNotRendered(string sourceUrl)
    {
        var path = await new AuditWriter(Path.Combine(directory, "audits")).WriteAsync(Record(sourceUrl));
        var html = await File.ReadAllTextAsync(Path.Combine(path, "review.html"));
        Assert.DoesNotContain("Open in Paperless", html);
    }

    [Fact]
    public async Task LocalReportEmbedsOnlyVerifiedRasterBytesUnderDataOnlyImageCsp()
    {
        var page = Path.Combine(directory, "source.png");
        await File.WriteAllBytesAsync(page, Png);
        var path = await new AuditWriter(Path.Combine(directory, "audits")).WriteAsync(Record(), [new(page, "page-1.png")]);
        var html = await File.ReadAllTextAsync(Path.Combine(path, "review.html"));
        Assert.Contains("img-src data:", html);
        Assert.DoesNotContain("img-src 'self'", html);
        Assert.Contains("src=\"data:image/png;base64," + Convert.ToBase64String(Png) + "\"", html);
        Assert.DoesNotContain("src=\"page-1.png\"", html);
        Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(path, "page-1.png")));
        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path, "audit.json")));
        Assert.Equal(64, audit.RootElement.GetProperty("attachments")[0].GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public async Task DisguisedHtmlAttachmentIsRetainedButNeverEmbeddedAsImage()
    {
        var page = Path.Combine(directory, "source.png");
        await File.WriteAllTextAsync(page, "<html><script>evil-image-payload()</script></html>");
        var path = await new AuditWriter(Path.Combine(directory, "audits")).WriteAsync(Record(), [new(page, "page-1.png")]);
        var html = await File.ReadAllTextAsync(Path.Combine(path, "review.html"));
        Assert.Contains("not a verified PNG or JPEG", html);
        Assert.DoesNotContain("src=\"data:", html);
        Assert.DoesNotContain("evil-image-payload", html);
    }

    [Fact]
    public async Task EveryUntrustedHtmlValueIsEscapedAndRawAuditIsCollapsed()
    {
        var path = await new AuditWriter(Path.Combine(directory, "audits")).WriteAsync(Record());
        var html = await File.ReadAllTextAsync(Path.Combine(path, "review.html"));
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<details><summary>Show the complete audit record", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("base-uri 'none'", html);
    }

    [Fact]
    public async Task FailedAttemptHasClearOutcomeWithoutClaimingValidatedProposal()
    {
        var record = Record() with { Outcome = "review_failed", Proposal = null, ErrorCode = "model_unavailable" };
        var path = await new AuditWriter(Path.Combine(directory, "audits")).WriteAsync(record);
        var html = await File.ReadAllTextAsync(Path.Combine(path, "review.html"));
        Assert.Contains("Review did not complete", html);
        Assert.Contains("No validated proposal was saved", html);
        Assert.Contains("model_unavailable", html);
        Assert.Contains("Existing OCR", html);
    }
}
