using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperlessLlm.Intent;

/// <summary>Model-declared dispositions, separate from the validated mutation contract.</summary>
public static class FieldDecisions
{
    private static readonly string[] Fields = ["title", "date", "correspondent", "document_type", "note"];
    public static JsonObject Schema() => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new JsonArray(Fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
        ["properties"] = new JsonObject(Fields.Select(f => KeyValuePair.Create<string, JsonNode?>(f,
            new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("unchanged", "uncertain", "policy", "not_applicable", "change") })))
    };
    public static IReadOnlyDictionary<string, string>? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("named_intent_object_required");
        if (!root.TryGetProperty("decisions", out var decisions)) return null; // Legacy/custom prompts remain usable.
        if (decisions.ValueKind != JsonValueKind.Object || !decisions.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(Fields.Order()))
            throw new InvalidDataException("invalid_field_decisions");
        var result = new Dictionary<string, string>();
        foreach (var field in Fields)
        {
            var value = decisions.GetProperty(field);
            var reason = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (reason is not ("unchanged" or "uncertain" or "policy" or "not_applicable" or "change"))
                throw new InvalidDataException("invalid_field_decision");
            ValidateAction(root, field, reason);
            result[field] = reason;
        }
        return result;
    }
    private static void ValidateAction(JsonElement root, string field, string reason)
    {
        var set = root.TryGetProperty(field, out var item) && item.GetProperty("action").GetString() == "set";
        if (set != (reason == "change")) throw new InvalidDataException("field_decision_action_mismatch");
    }
}
