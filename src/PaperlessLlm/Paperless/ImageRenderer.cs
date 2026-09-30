using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PaperlessLlm.Paperless;

public interface IDocumentRenderer
{
    Task<IReadOnlyList<RenderedPage>> RenderAsync(OriginalDocument original, string outputDirectory, CancellationToken ct = default);
}

public sealed class RenderOptions
{
    public int MaxPages { get; init; } = 10;
    public int MaxDimension { get; init; } = 2000;
    public long MaxTotalBytes { get; init; } = 30 * 1024 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);
    public string PdfInfoExecutable { get; init; } = "pdfinfo";
    public string PdfToPpmExecutable { get; init; } = "pdftoppm";
}

/// <summary>Produces all pages or fails. Never treats a truncated PDF as complete evidence.</summary>
public sealed partial class ImageRenderer : IDocumentRenderer
{
    private readonly RenderOptions options;
    public ImageRenderer(RenderOptions? options = null)
    {
        this.options = options ?? new RenderOptions();
        if (this.options.MaxPages is < 1 or > 100 || this.options.MaxDimension is < 100 or > 4000 ||
            this.options.MaxTotalBytes <= 0 || this.options.Timeout <= TimeSpan.Zero || this.options.Timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentException("Invalid document rendering limits.");
    }

    public async Task<IReadOnlyList<RenderedPage>> RenderAsync(OriginalDocument original, string outputDirectory, CancellationToken ct = default)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory)) throw new PaperlessException("Rendered page destination already exists.");
        var stage = Path.Combine(Path.GetDirectoryName(outputDirectory)!, ".ppllm-render-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = File.OpenRead(original.Path))
            {
                if (file.Length != original.Bytes || file.Length <= 0 ||
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)) != original.Sha256)
                    throw new PaperlessException("Original changed before rendering.");
            }
            Directory.CreateDirectory(stage);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var prefix = new byte[8];
            await using (var file = File.OpenRead(original.Path)) _ = await file.ReadAsync(prefix, ct);
            int pages;
            if (prefix.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            {
                var info = await RunAsync(options.PdfInfoExecutable, [Path.GetFullPath(original.Path)], ct);
                var match = PageCountRegex().Match(info);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out pages) || pages < 1 || pages > options.MaxPages)
                    throw new PaperlessException("PDF page count is invalid or exceeds the rendering limit.");
                await RunAsync(options.PdfToPpmExecutable,
                    ["-png", "-r", "120", "-scale-to", options.MaxDimension.ToString(), "-f", "1", "-l", pages.ToString(),
                        Path.GetFullPath(original.Path), Path.Combine(stage, "page")], ct);
            }
            else
            {
                pages = 1;
                var type = ValidateImage(original.Path);
                if (original.Bytes > options.MaxTotalBytes) throw new PaperlessException("Original image exceeds the rendering size limit.");
                File.Copy(original.Path, Path.Combine(stage, "page-1." + (type == "image/png" ? "png" : "jpg")), false);
            }
            var files = Directory.GetFiles(stage);
            if (files.Length != pages || Directory.GetDirectories(stage).Length != 0)
                throw new PaperlessException("Renderer did not produce exactly all document pages.");
            var results = new List<RenderedPage>();
            long total = 0;
            foreach (var file in files)
            {
                var match = RenderedNameRegex().Match(Path.GetFileName(file));
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out var page) || page < 1 || page > pages || results.Any(x => x.PageNumber == page))
                    throw new PaperlessException("Renderer produced an unexpected page filename.");
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new PaperlessException("Rendered page cannot be a link.");
                total += new FileInfo(file).Length;
                if (total > options.MaxTotalBytes) throw new PaperlessException("Rendered pages exceed the size limit.");
                var mediaType = ValidateImage(file);
                await using var input = File.OpenRead(file);
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
                results.Add(new RenderedPage(Path.Combine(outputDirectory, Path.GetFileName(file)), mediaType, hash, page));
            }
            Directory.Move(stage, outputDirectory);
            return results.OrderBy(x => x.PageNumber).ToArray();
        }
        catch (IOException) { throw new PaperlessException("Document rendering could not access its files."); }
        catch (UnauthorizedAccessException) { throw new PaperlessException("Document rendering could not access its files."); }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    internal static string ValidateImage(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        if (file.Read(header) < 24) throw new PaperlessException("Image data is incomplete.");
        if (header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && header.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            if (file.Length < 45 || BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8, 4)) != 13)
                throw new PaperlessException("PNG data is incomplete.");
            var width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
            CheckDimensions(width, height);
            file.Position = file.Length - 12;
            Span<byte> ending = stackalloc byte[12];
            file.ReadExactly(ending);
            if (!ending.SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 }))
                throw new PaperlessException("PNG data is incomplete.");
            return "image/png";
        }
        if (header[0] != 0xff || header[1] != 0xd8) throw new PaperlessException("Only PDF, PNG, and JPEG originals are supported.");
        file.Position = 2;
        // Parse JPEG frame dimensions without decoding or invoking arbitrary image delegates.
        while (file.Position < file.Length)
        {
            if (file.ReadByte() != 0xff) throw new PaperlessException("JPEG markers are invalid.");
            int marker;
            do { marker = file.ReadByte(); } while (marker == 0xff);
            if (marker < 0 || marker is 0xd9 or 0xda) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            var high = file.ReadByte();
            var low = file.ReadByte();
            if (high < 0 || low < 0) break;
            var length = high * 256 + low;
            if (length < 2 || file.Position + length - 2 > file.Length) break;
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8) break;
                _ = file.ReadByte();
                var height = file.ReadByte() * 256 + file.ReadByte();
                var width = file.ReadByte() * 256 + file.ReadByte();
                CheckDimensions((uint)width, (uint)height);
                file.Position = file.Length - 2;
                if (file.ReadByte() != 0xff || file.ReadByte() != 0xd9) throw new PaperlessException("JPEG data is incomplete.");
                return "image/jpeg";
            }
            file.Position += length - 2;
        }
        throw new PaperlessException("JPEG frame dimensions could not be verified.");
    }

    private static void CheckDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > 20000 || height > 20000 || (ulong)width * height > 40_000_000)
            throw new PaperlessException("Image dimensions exceed the decoding safety limit.");
    }

    private async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(options.Timeout);
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["LC_ALL"] = "C";
        try
        {
            if (!process.Start()) throw new PaperlessException("PDF renderer could not be started.");
            var stdout = ReadBoundedAsync(process.StandardOutput, cancellation.Token);
            var stderr = ReadBoundedAsync(process.StandardError, cancellation.Token);
            var exit = process.WaitForExitAsync(cancellation.Token);
            await Task.WhenAll(stdout, stderr, exit);
            if (process.ExitCode != 0) throw new PaperlessException("PDF renderer failed; document may be encrypted, damaged, or unsupported.");
            return await stdout;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PaperlessException("PDF rendering timed out."); }
        catch (System.ComponentModel.Win32Exception) { throw new PaperlessException("PDF rendering tools are not installed or could not start."); }
        finally
        {
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (text.Length + read > 65536) throw new PaperlessException("PDF renderer exceeded diagnostic output limits.");
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    [GeneratedRegex(@"^Pages:\s+(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PageCountRegex();
    [GeneratedRegex(@"^page-(\d+)\.(png|jpg)$", RegexOptions.CultureInvariant)]
    private static partial Regex RenderedNameRegex();
}
