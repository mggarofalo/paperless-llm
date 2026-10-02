using System.Security.Cryptography;
using System.Text;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Tests;

public sealed class ImageRendererTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ppllm-render-test-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a2ioAAAAASUVORK5CYII=");
    public ImageRendererTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private OriginalDocument Original(byte[] bytes)
    {
        var path = Path.Combine(directory, "original");
        File.WriteAllBytes(path, bytes);
        return new(path, "application/octet-stream", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
    }

    [Fact]
    public async Task RasterBecomesOneVerifiedPageWithoutExternalProcess()
    {
        var original = Original(Png);
        var pages = await new ImageRenderer().RenderAsync(original, Path.Combine(directory, "pages"));
        var page = Assert.Single(pages);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal("image/png", page.MediaType);
        Assert.Equal(original.Sha256, page.Sha256);
        Assert.Equal(Png, await File.ReadAllBytesAsync(page.Path));
    }

    [Fact]
    public async Task ChangedOriginalIsRejectedBeforeRendering()
    {
        var original = Original(Png);
        await File.AppendAllTextAsync(original.Path, "changed");
        await Assert.ThrowsAsync<PaperlessException>(() => new ImageRenderer().RenderAsync(original, Path.Combine(directory, "pages")));
        Assert.Empty(Directory.GetDirectories(directory));
    }

    [Fact]
    public async Task IncompleteAndUnsupportedImagesAreRejectedAndCleaned()
    {
        foreach (var bytes in new[] { Png[..24], "<html>not an image or PDF</html>"u8.ToArray() })
        {
            await Assert.ThrowsAsync<PaperlessException>(() => new ImageRenderer().RenderAsync(Original(bytes), Path.Combine(directory, "pages")));
            Assert.Empty(Directory.GetDirectories(directory));
        }
    }

    [Fact]
    public async Task ExistingOutputIsNeverOverwritten()
    {
        var target = Path.Combine(directory, "pages");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "keep"), "keep");
        await Assert.ThrowsAsync<PaperlessException>(() => new ImageRenderer().RenderAsync(Original(Png), target));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, "keep")));
    }

    [Fact]
    public async Task OversizedRasterFailsBeforePublishingPages()
    {
        await Assert.ThrowsAsync<PaperlessException>(() => new ImageRenderer(new() { MaxTotalBytes = 10 }).RenderAsync(Original(Png), Path.Combine(directory, "pages")));
        Assert.Empty(Directory.GetDirectories(directory));
    }

    [Fact]
    public async Task MissingPdfToolsFailClearlyWithoutPartialEvidence()
    {
        var renderer = new ImageRenderer(new() { PdfInfoExecutable = "ppllm-nonexistent-pdfinfo" });
        var error = await Assert.ThrowsAsync<PaperlessException>(() => renderer.RenderAsync(Original("%PDF-1.4\nsynthetic invalid pdf"u8.ToArray()), Path.Combine(directory, "pages")));
        Assert.Contains("not installed", error.Message);
        Assert.Empty(Directory.GetDirectories(directory));
    }

    [PopplerFact]
    public async Task RealPopplerRendersEverySyntheticPdfPage()
    {
        var pages = await new ImageRenderer().RenderAsync(Original(SyntheticPdf()), Path.Combine(directory, "pages"));
        Assert.Equal(new[] { 1, 2 }, pages.Select(p => p.PageNumber));
        Assert.All(pages, page =>
        {
            Assert.Equal("image/png", page.MediaType);
            Assert.True(new FileInfo(page.Path).Length > 100);
            Assert.Equal(64, page.Sha256.Length);
        });
        Assert.NotEqual(pages[0].Sha256, pages[1].Sha256);
    }

    [PopplerFact]
    public async Task PdfPageCapRefusesWholeDocumentInsteadOfTruncating()
    {
        var error = await Assert.ThrowsAsync<PaperlessException>(() => new ImageRenderer(new() { MaxPages = 1 })
            .RenderAsync(Original(SyntheticPdf()), Path.Combine(directory, "pages")));
        Assert.Contains("page count", error.Message);
        Assert.Empty(Directory.GetDirectories(directory));
    }

    [Fact]
    public void JpegFrameDimensionsAndEndMarkerAreVerified()
    {
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xe0, 0, 8, 1, 2, 3, 4, 5, 6,
            0xff, 0xc0, 0, 8, 8, 0, 1, 0, 1, 0, 0xff, 0xd9];
        Assert.Equal("image/jpeg", ImageRenderer.ValidateImage(Original(jpeg).Path));
        jpeg[^1] = 0;
        Assert.Throws<PaperlessException>(() => ImageRenderer.ValidateImage(Original(jpeg).Path));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(20001, 1)]
    [InlineData(10000, 10000)]
    public void OversizedOrZeroPngDimensionsAreRejected(uint width, uint height)
    {
        var bytes = (byte[])Png.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        Assert.Throws<PaperlessException>(() => ImageRenderer.ValidateImage(Original(bytes).Path));
    }

    [Fact]
    public void TruncatedJpegSegmentCannotSkipPastFileBounds()
    {
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xe0, 0xff, 0xff, 0, 0, 0, 0, 0, 0,
            0xff, 0xc0, 0, 8, 8, 0, 1, 0, 1, 0, 0xff, 0xd9];
        Assert.Throws<PaperlessException>(() => ImageRenderer.ValidateImage(Original(jpeg).Path));
    }

    private static byte[] SyntheticPdf()
    {
        var content1 = "BT /F1 20 Tf 20 100 Td (SYNTHETIC PAGE ONE) Tj ET\n";
        var content2 = "BT /F1 20 Tf 20 100 Td (SYNTHETIC PAGE TWO) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content1.Length} >>\nstream\n{content1}endstream",
            $"<< /Length {content2.Length} >>\nstream\n{content2}endstream"
        };
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}

public sealed class PopplerFactAttribute : FactAttribute
{
    public PopplerFactAttribute()
    {
        static bool Exists(string executable) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(path => File.Exists(Path.Combine(path, executable + (OperatingSystem.IsWindows() ? ".exe" : ""))));
        if (!Exists("pdfinfo") || !Exists("pdftoppm")) Skip = "Poppler is not installed; Docker/Linux CI must run these synthetic PDF checks.";
    }
}
