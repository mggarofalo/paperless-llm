using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperlessLlm.Organizer;

internal static class IntentDiagnostics
{
    private static readonly string[] Fields = ["title", "date", "correspondent", "document_type", "note"];
    public static DecisionSummary Summarize(JsonElement intent, string? namedRaw = null)
    {
        var actions = Fields.Where(f => intent.TryGetProperty(f, out _))
            .Select(f => intent.GetProperty(f).GetProperty("action").GetString()).ToArray();
        var uncertainty = intent.TryGetProperty("uncertainty", out var reasons) ? reasons.GetArrayLength() : 0;
        using var named = namedRaw is null ? null : JsonDocument.Parse(namedRaw);
        var decisions = named is null ? null : Intent.FieldDecisions.Read(named.RootElement);
        return new(actions.Count(a => a == "keep"), actions.Count(a => a == "set"), uncertainty, decisions);
    }
    // Applied only AFTER full validation. No model instruction can expand a notes-only request.
    public static JsonElement NotesOnly(JsonElement validated)
    {
        var intent = JsonNode.Parse(validated.GetRawText())!;
        foreach (var field in Fields.Where(f => f != "note"))
            intent[field] = new JsonObject { ["action"] = "keep", ["value"] = null, ["evidence"] = new JsonArray() };
        intent["add_tags"] = new JsonArray();
        intent["ocr"] = new JsonObject { ["action"] = "keep", ["pages"] = new JsonArray(), ["evidence"] = new JsonArray() };
        return JsonSerializer.SerializeToElement(intent);
    }
}
