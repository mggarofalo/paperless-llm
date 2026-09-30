using System.Globalization;
using System.Text.Json;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Review;

public sealed class ProposalValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Checks local constraints, not factual accuracy. Never applies suggestions.</summary>
public static class ProposalValidator
{
    private static readonly HashSet<string> Required = ["title", "date", "correspondent", "document_type", "add_tags", "ocr_text", "evidence", "uncertainty"];
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        { "needs review", "inbox", "receipt to log", "hsa reimbursed", "hsa unreimbursed", "expense", "sweetgum", "wallingford" };

    public static JsonElement Schema { get; } = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"properties":{
          "title":{"type":["string","null"],"maxLength":128},
          "date":{"type":["string","null"],"description":"Explicit document date YYYY-MM-DD, or null to abstain."},
          "correspondent":{"type":["integer","null"]},
          "document_type":{"type":["integer","null"]},
          "add_tags":{"type":"array","items":{"type":"integer"}},
          "ocr_text":{"type":["string","null"],"description":"Literal complete transcription from supplied scans only, or null."},
          "evidence":{"type":"array","items":{"type":"string"}},
          "uncertainty":{"type":"array","items":{"type":"string"}}},
         "required":["title","date","correspondent","document_type","add_tags","ocr_text","evidence","uncertainty"]}
        """).RootElement.Clone();

    public static bool IsProtected(NamedEntity tag) => tag.IsInboxTag || Protected.Contains(string.Join(' ', tag.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

    public static JsonElement Validate(string json, PaperlessDocument document, PaperlessTaxonomy taxonomy, bool hasVisualSource)
    {
        if (json.Length > 4 * 1024 * 1024) throw new ProposalValidationException("proposal_too_large");
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ProposalValidationException("invalid_proposal_json"); }
        using (parsed)
        {
            var value = parsed.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw new ProposalValidationException("proposal_must_be_object");
            var fields = value.EnumerateObject().Select(p => p.Name).ToArray();
            if (fields.Length != Required.Count || !Required.SetEquals(fields)) throw new ProposalValidationException("invalid_proposal_fields");
            var tags = Index(taxonomy.Tags);
            var correspondents = Index(taxonomy.Correspondents);
            var types = Index(taxonomy.DocumentTypes);
            var title = OptionalText(value.GetProperty("title"), "invalid_title");
            if (title is { Length: > 128 }) throw new ProposalValidationException("title_too_long");
            var date = OptionalText(value.GetProperty("date"), "invalid_date");
            if (date is not null && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ProposalValidationException("invalid_date");
            OptionalIdentity(value.GetProperty("correspondent"), correspondents);
            OptionalIdentity(value.GetProperty("document_type"), types);
            var added = value.GetProperty("add_tags");
            if (added.ValueKind != JsonValueKind.Array) throw new ProposalValidationException("invalid_tag_additions");
            HashSet<int> additions = [];
            foreach (var tag in added.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.Number || !tag.TryGetInt32(out var id) || !tags.TryGetValue(id, out var entity))
                    throw new ProposalValidationException("unknown_tag");
                if (!additions.Add(id) || document.Tags.Contains(id)) throw new ProposalValidationException("duplicate_tag_addition");
                if (IsProtected(entity)) throw new ProposalValidationException("protected_tag");
            }
            var ocr = OptionalText(value.GetProperty("ocr_text"), "blank_or_invalid_ocr");
            if (ocr is not null && !hasVisualSource) throw new ProposalValidationException("ocr_requires_visual_source");
            var evidence = TextArray(value.GetProperty("evidence"));
            var uncertainty = TextArray(value.GetProperty("uncertainty"));
            var hasSuggestion = additions.Count > 0 || new[] { "title", "date", "correspondent", "document_type", "ocr_text" }
                .Any(key => value.GetProperty(key).ValueKind != JsonValueKind.Null);
            if (hasSuggestion && evidence.Count == 0) throw new ProposalValidationException("evidence_required");
            if (!hasSuggestion && uncertainty.Count == 0) throw new ProposalValidationException("abstention_explanation_required");
            return value.Clone();
        }
    }

    private static Dictionary<int, NamedEntity> Index(IReadOnlyList<NamedEntity> entities)
    {
        Dictionary<int, NamedEntity> result = [];
        foreach (var entity in entities)
            if (entity.Id <= 0 || string.IsNullOrWhiteSpace(entity.Name) || !result.TryAdd(entity.Id, entity))
                throw new ProposalValidationException("invalid_taxonomy");
        return result;
    }

    private static void OptionalIdentity(JsonElement value, Dictionary<int, NamedEntity> allowed)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || !allowed.ContainsKey(id))
            throw new ProposalValidationException("unknown_taxonomy_id");
    }

    private static string? OptionalText(JsonElement value, string error)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ProposalValidationException(error);
        return value.GetString();
    }

    private static List<string> TextArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new ProposalValidationException("invalid_evidence_or_uncertainty");
        return value.EnumerateArray().Select(item => OptionalText(item, "invalid_evidence_or_uncertainty")
            ?? throw new ProposalValidationException("invalid_evidence_or_uncertainty")).ToList();
    }
}
