namespace PaperlessLlm.Paperless;

public sealed record NamedEntity(int Id, string Name, bool IsInboxTag = false) { }
public sealed record PaperlessTaxonomy(IReadOnlyList<NamedEntity> Tags, IReadOnlyList<NamedEntity> Correspondents, IReadOnlyList<NamedEntity> DocumentTypes) { }
public sealed record PaperlessDocument(int Id, string Title, string Content, string? Created, string? Modified,
    int? CorrespondentId, int? DocumentTypeId, IReadOnlyList<int> Tags, string? MimeType,
    string? OriginalFileName, string RevisionHash)
{
    // Null means notes were not returned (or this is a pre-notes saved job).
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PaperlessNote>? Notes { get; init; }
}
public sealed record PaperlessNote(int Id, string Note) { }
public sealed record OriginalDocument(string Path, string MediaType, string Sha256, long Bytes) { }
public sealed record RenderedPage(string Path, string MediaType, string Sha256, int PageNumber) { }

public interface IPaperlessClient
{
    Task<IReadOnlyList<PaperlessDocument>> ListDocumentsAsync(string? tagName = "needs review", int limit = 10, int? afterId = null, CancellationToken ct = default);
    Task<int> GetLatestDocumentIdAsync(CancellationToken ct = default);
    Task<PaperlessDocument> GetDocumentAsync(int id, CancellationToken ct = default);
    Task<PaperlessTaxonomy> GetTaxonomyAsync(CancellationToken ct = default);
    Task<OriginalDocument> DownloadOriginalAsync(int id, string destination, CancellationToken ct = default);
}

public sealed class PaperlessOptions
{
    public required Uri BaseUrl { get; init; }
    public required string TokenFile { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxPages { get; init; } = 50;
    public int PageSize { get; init; } = 100;
    public long MaxOriginalBytes { get; init; } = 50 * 1024 * 1024;
    public int MaxJsonBytes { get; init; } = 8 * 1024 * 1024;
}

public sealed class PaperlessException(string message, string code = "paperless_error") : Exception(message)
{
    public string Code { get; } = code;
}
