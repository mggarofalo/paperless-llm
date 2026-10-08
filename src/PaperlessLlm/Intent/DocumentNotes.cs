using System.Text.Json;
using PaperlessLlm.Paperless;
using PaperlessLlm.Review;

namespace PaperlessLlm.Intent;

/// <summary>One append-only summary per document; quotations establish grounding, not factual correctness.</summary>
public static class DocumentNotes
{
    public const string Label = "AI-generated document summary (Paperless LLM)";
    public const int MaxCharacters = 1200;

    public static bool HasSummary(PaperlessDocument document) =>
        document.Notes?.Any(n => n.Note.StartsWith(Label, StringComparison.Ordinal)) == true;

    public static string? Proposed(JsonElement intent, PaperlessDocument document)
    {
        if (!intent.TryGetProperty("note", out var note) || note.GetProperty("action").GetString() == "keep" || HasSummary(document))
            return null;
        return Label + "\n" + note.GetProperty("value").GetString()!.Trim();
    }

    public static bool SameNotes(PaperlessDocument before, PaperlessDocument after) =>
        before.Notes is null || (after.Notes is not null && before.Notes.OrderBy(n => n.Id).SequenceEqual(after.Notes.OrderBy(n => n.Id)));

    public static void Validate(JsonElement note, PaperlessDocument document)
    {
        if (note.ValueKind != JsonValueKind.Object || !note.EnumerateObject().Select(p => p.Name).Order()
                .SequenceEqual(new[] { "action", "evidence", "value" })) Fail("invalid_note_fields");
        var evidence = note.GetProperty("evidence");
        if (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() > 20) Fail("invalid_note_evidence");
        var action = note.GetProperty("action");
        if (action.ValueKind != JsonValueKind.String) Fail("invalid_note_action");
        if (action.GetString() == "keep")
        {
            if (note.GetProperty("value").ValueKind != JsonValueKind.Null || evidence.GetArrayLength() != 0) Fail("invalid_note_keep");
            return;
        }
        if (action.GetString() != "set") Fail("invalid_note_action");
        ValidateSummary(note.GetProperty("value"), document);
        ValidateEvidence(evidence, document.Content);
    }

    private static void ValidateSummary(JsonElement value, PaperlessDocument document)
    {
        if (value.ValueKind != JsonValueKind.String) Fail("invalid_note_text");
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxCharacters || text.Any(char.IsControl)) Fail("invalid_note_text");
        if (document.Notes is null) Fail("note_context_missing");
        if (document.Content.Length > IntentPrompt.MaxOcrCharacters) Fail("note_source_truncated");
    }

    private static void ValidateEvidence(JsonElement evidence, string content)
    {
        if (evidence.GetArrayLength() == 0) Fail("note_evidence_required");
        foreach (var entry in evidence.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) Fail("invalid_note_evidence");
            var quote = entry.GetString()!;
            if (string.IsNullOrWhiteSpace(quote) || quote.Length > 4096 || !content.Contains(quote, StringComparison.Ordinal))
                Fail("note_evidence_not_in_ocr");
        }
    }

    private static void Fail(string code) => throw new ProposalValidationException(code);
}
