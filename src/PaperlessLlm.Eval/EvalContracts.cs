using System.Text.Json;
using System.Text.Json.Serialization;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Eval;

public sealed record EvalCase
{
    public required string CaseId { get; init; }
    public required string Split { get; init; }
    public required EvalDocument Document { get; init; }
    public required EvalTaxonomy Taxonomy { get; init; }
    public required int PageCount { get; init; }
    public required EvalExpected Expected { get; init; }
    public string? Notes { get; init; }

    public PaperlessDocument ToDocument() => new(Document.Id, Document.Title, Document.Content,
        Document.Created, null, Document.CorrespondentId, Document.DocumentTypeId, Document.Tags,
        null, null, "offline-eval");
    public PaperlessTaxonomy ToTaxonomy() => new(Taxonomy.Tags.Select(x => new NamedEntity(x.Id, x.Name, x.IsInboxTag)).ToArray(),
        Taxonomy.Correspondents.Select(x => new NamedEntity(x.Id, x.Name)).ToArray(),
        Taxonomy.DocumentTypes.Select(x => new NamedEntity(x.Id, x.Name)).ToArray());
}

public sealed record EvalDocument
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public string? Created { get; init; }
    public int? CorrespondentId { get; init; }
    public int? DocumentTypeId { get; init; }
    public List<int> Tags { get; init; } = [];
    public List<string> PageImages { get; init; } = [];
}

public sealed record EvalTaxonomy
{
    public List<EvalNamedEntity> Tags { get; init; } = [];
    public List<EvalNamedEntity> Correspondents { get; init; } = [];
    public List<EvalNamedEntity> DocumentTypes { get; init; } = [];
}

public sealed record EvalNamedEntity(int Id, string Name, bool IsInboxTag = false);

public sealed record EvalExpected
{
    public required ExpectedField Title { get; init; }
    public required ExpectedField Date { get; init; }
    public required ExpectedField Correspondent { get; init; }
    public required ExpectedField DocumentType { get; init; }
    public List<int> AddTagIds { get; init; } = [];
    public List<int> ProtectedTagIds { get; init; } = [];
    public EvalOcrExpected Ocr { get; init; } = new();
    public bool Critical { get; init; }
}

public sealed record ExpectedField
{
    public required string Action { get; init; }
    public object? Value { get; init; }
}

public sealed record EvalOcrExpected
{
    public bool MustReplace { get; init; }
    public List<string> KeyFacts { get; init; } = [];
    public List<string> ForbiddenFacts { get; init; } = [];
}

public sealed record CandidateOutput
{
    public required string CaseId { get; init; }
    public required JsonElement Intent { get; init; }
}

public sealed record FieldScore(int Passed, int Total);
public sealed record CaseScore(string CaseId, string Split, bool Critical, bool Valid, string? Failure,
    int OcrKeyFactsMatched, int OcrKeyFactsTotal, Dictionary<string, bool> Checks);

public sealed record EvalReport
{
    public required string Version { get; init; }
    public string? Model { get; init; }
    public string? HarnessVersion { get; init; }
    public required string Split { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required int TotalCases { get; init; }
    public required int ImportedOutputs { get; init; }
    public required int MissingOutputs { get; init; }
    public required int InvalidJsonOutputs { get; init; }
    public required int SchemaFailures { get; init; }
    public required int ValidatorFailures { get; init; }
    public required int CriticalFailures { get; init; }
    public required int OcrKeyFactsMatched { get; init; }
    public required int OcrKeyFactsTotal { get; init; }
    public required Dictionary<string, FieldScore> Checks { get; init; }
    public required List<CaseScore> Cases { get; init; }
}

public static class EvalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException("JSON value was null.");
}
