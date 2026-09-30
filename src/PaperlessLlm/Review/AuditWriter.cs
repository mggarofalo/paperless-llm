using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Review;

public sealed record AuditAttachment(string SourcePath, string FileName);
public sealed record AuditRecord(
    DateTimeOffset RecordedAt, string Outcome, string Model, PaperlessDocument Source,
    PaperlessTaxonomy? Taxonomy, bool HasVisualSource, string? Prompt,
    string? RawResponse, JsonElement? Proposal, string? ErrorCode, string? SourceUrl = null);

/// <summary>Private local evidence only. The directory must live on a protected volume.</summary>
public sealed class AuditWriter(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<string> WriteAsync(AuditRecord record, IReadOnlyList<AuditAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        PrivateDirectory(DirectoryPath);
        var name = $"{record.RecordedAt:yyyyMMddTHHmmssfffffffZ}-{record.Source.Id}-{Guid.NewGuid():N}";
        var target = Path.Combine(DirectoryPath, name);
        var staging = Path.Combine(DirectoryPath, ".pending-" + Guid.NewGuid().ToString("N"));
        PrivateDirectory(staging);
        try
        {
            List<object> files = [];
            foreach (var attachment in attachments ?? [])
            {
                if (attachment.FileName != Path.GetFileName(attachment.FileName) || attachment.FileName is "." or ".." || string.IsNullOrWhiteSpace(attachment.FileName))
                    throw new InvalidOperationException("invalid_audit_attachment_name");
                var output = Path.Combine(staging, attachment.FileName);
                await using (var source = File.OpenRead(attachment.SourcePath))
                await using (var destination = PrivateFile(output))
                    await source.CopyToAsync(destination, cancellationToken);
                await using var content = File.OpenRead(output);
                files.Add(new { name = attachment.FileName, bytes = content.Length, sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken)) });
            }
            var json = JsonSerializer.Serialize(new { formatVersion = 1, record, attachments = files }, JsonOptions);
            await WritePrivateAsync(Path.Combine(staging, "audit.json"), json, cancellationToken);
            var html = await AuditReport.BuildAsync(record, attachments ?? [], staging, json, cancellationToken);
            await WritePrivateAsync(Path.Combine(staging, "review.html"), html, cancellationToken);
            Directory.Move(staging, target);
            return target;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    internal static void PrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static FileStream PrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    internal static async Task WritePrivateAsync(string path, string content, CancellationToken cancellationToken)
    {
        await using var stream = PrivateFile(path);
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }
}
