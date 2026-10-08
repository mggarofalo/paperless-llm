using System.Globalization;
using System.Text.Json;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;

namespace PaperlessLlm.Intent;

/// <summary>Enforces shape and permitted operations; does not certify model factual accuracy.</summary>
public static class IntentValidator
{
    public static JsonElement Validate(string json, PaperlessDocument document, PaperlessTaxonomy taxonomy, int pageCount)
    {
        if (json.Length > 4 * 1024 * 1024) Fail("intent_too_large");
        if (pageCount < 0 || pageCount > IntentPrompt.MaxPages) Fail("invalid_page_count");
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new ProposalValidationException("invalid_intent_json"); }
        using (parsed)
        {
            var root = parsed.RootElement;
            ValidateVersion(root);
            var tags = Index(taxonomy.Tags);
            var correspondents = Index(taxonomy.Correspondents);
            var types = Index(taxonomy.DocumentTypes);
            ValidateFields(root, correspondents, types);
            ValidateTags(root.GetProperty("add_tags"), document, tags);
            ValidateOcr(root.GetProperty("ocr"), pageCount);
            ValidateNote(root, document);
            TextArray(root.GetProperty("uncertainty"));
            return root.Clone();
        }
    }

    private static void ValidateNote(JsonElement root, PaperlessDocument document)
    {
        if (!root.TryGetProperty("note", out var note)) return;
        DocumentNotes.Validate(note, document);
        if (note.GetProperty("action").GetString() == "set" && root.GetProperty("ocr").GetProperty("action").GetString() != "keep")
            Fail("note_requires_existing_ocr");
    }

    private static void ValidateVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema_version", out var version) ||
            version.ValueKind != JsonValueKind.String) Fail("unsupported_intent_version");
        string[] fields = ["schema_version", "title", "date", "correspondent", "document_type", "add_tags", "ocr", "uncertainty"];
        var text = root.GetProperty("schema_version").GetString();
        if (text == DocumentIntent.Version) fields = [.. fields, "note"];
        else if (text != "1") Fail("unsupported_intent_version");
        Fields(root, fields);
    }

    private static void ValidateFields(JsonElement root, Dictionary<int, NamedEntity> correspondents, Dictionary<int, NamedEntity> types)
    {
        foreach (var key in new[] { "title", "date", "correspondent", "document_type" })
            ValidateField(key, root.GetProperty(key), correspondents, types);
    }

    private static void ValidateField(string key, JsonElement fieldValue, Dictionary<int, NamedEntity> correspondents, Dictionary<int, NamedEntity> types)
    {
        var field = fieldValue;
        Fields(field, "action", "value", "evidence");
        var set = Set(field);
        var evidence = TextArray(field.GetProperty("evidence"));
        var value = field.GetProperty("value");
        if (!set) { if (value.ValueKind != JsonValueKind.Null) Fail("keep_requires_null"); return; }
        if (evidence == 0) Fail("field_evidence_required");
        if (key is "title" or "date")
        {
            var text = Text(value, key == "title" ? 128 : 10);
            if (InvalidDate(key, text))
                Fail("invalid_date");
        }
        else if (!TryId(value, out var id) || !(key == "correspondent" ? correspondents : types).ContainsKey(id))
            Fail("unknown_taxonomy_id");
    }

    private static bool InvalidDate(string key, string text) => key == "date" &&
        !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static void ValidateTags(JsonElement additions, PaperlessDocument document, Dictionary<int, NamedEntity> tags)
    {
        Array(additions, 100);
        HashSet<int> seen = [];
        foreach (var tag in additions.EnumerateArray())
        {
            Fields(tag, "id", "evidence");
            if (!TryId(tag.GetProperty("id"), out var id) || !tags.ContainsKey(id)) Fail("unknown_tag");
            if (ProtectedTags.IsProtected(tags[id])) Fail("protected_tag");
            if (!seen.Add(id) || document.Tags.Contains(id)) Fail("duplicate_tag_addition");
            if (TextArray(tag.GetProperty("evidence")) == 0) Fail("field_evidence_required");
        }
    }

    private static void ValidateOcr(JsonElement ocr, int pageCount)
    {
        Fields(ocr, "action", "pages", "evidence");
        var replace = Set(ocr);
        var pages = ocr.GetProperty("pages");
        Array(pages, IntentPrompt.MaxPages);
        var ocrEvidence = TextArray(ocr.GetProperty("evidence"));
        if (!replace && pages.GetArrayLength() != 0) Fail("keep_ocr_requires_empty_pages");
        if (replace)
        {
            if (pageCount == 0 || pages.GetArrayLength() != pageCount) Fail("incomplete_ocr_pages");
            if (ocrEvidence == 0) Fail("field_evidence_required");
            var number = 0;
            var hasText = false;
            foreach (var page in pages.EnumerateArray()) hasText |= ValidateOcrPage(page, ++number);
            if (!hasText) Fail("blank_ocr_replacement");
        }
    }

    private static bool ValidateOcrPage(JsonElement page, int number)
    {
        Fields(page, "page", "text", "complete", "uncertainty");
        if (!TryId(page.GetProperty("page"), out var index) || index != number) Fail("invalid_ocr_page_order");
        if (page.GetProperty("complete").ValueKind != JsonValueKind.True || TextArray(page.GetProperty("uncertainty")) != 0)
            Fail("incomplete_ocr_page");
        var text = page.GetProperty("text");
        if (text.ValueKind != JsonValueKind.String || text.GetString()!.Length > 500000) Fail("invalid_ocr_text");
        return !string.IsNullOrWhiteSpace(text.GetString());
    }

    private static void Fields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail("intent_object_required");
        var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != expected.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            !expected.ToHashSet(StringComparer.Ordinal).SetEquals(actual)) Fail("invalid_intent_fields");
    }

    private static bool Set(JsonElement field)
    {
        var action = field.GetProperty("action");
        if (action.ValueKind != JsonValueKind.String || action.GetString() is not ("keep" or "set")) Fail("invalid_intent_action");
        return action.GetString() == "set";
    }

    private static Dictionary<int, NamedEntity> Index(IReadOnlyList<NamedEntity> entities)
    {
        Dictionary<int, NamedEntity> index = [];
        foreach (var entity in entities)
            if (entity.Id <= 0 || string.IsNullOrWhiteSpace(entity.Name) || !index.TryAdd(entity.Id, entity)) Fail("invalid_taxonomy");
        return index;
    }

    private static bool TryId(JsonElement value, out int id) { id = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out id) && id > 0; }
    private static void Array(JsonElement value, int max)
    { if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > max) Fail("invalid_intent_array"); }
    private static string Text(JsonElement value, int max)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > max)
            Fail("invalid_intent_text");
        return value.GetString()!;
    }
    private static int TextArray(JsonElement value)
    {
        Array(value, 100);
        foreach (var entry in value.EnumerateArray()) Text(entry, 4096);
        return value.GetArrayLength();
    }
    private static void Fail(string code) => throw new ProposalValidationException(code);
}
