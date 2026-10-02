using System.Text.Json.Nodes;
using PaperlessLlm.Intent;
using PaperlessLlm.Paperless;

namespace PaperlessLlm.Eval;

/// <summary>Offline experiment only: models select exact names; code binds IDs.</summary>
public static class NamedIntentContract
{
    public static string Schema()
    {
        var schema = JsonNode.Parse(DocumentIntent.Schema.GetRawText())!;
        foreach (var field in new[] { "correspondent", "document_type" })
            schema["properties"]![field]!["properties"]!["value"]!["type"] = new JsonArray("string", "null");
        var tag = schema["properties"]!["add_tags"]!["items"]!;
        var properties = tag["properties"]!.AsObject();
        properties.Remove("id");
        properties["name"] = new JsonObject { ["type"] = "string" };
        tag["required"] = new JsonArray("name", "evidence");
        return schema.ToJsonString();
    }

    public static string Payload(string productionPayload, PaperlessTaxonomy taxonomy)
    {
        var payload = JsonNode.Parse(productionPayload)!;
        var document = payload["document"]!;
        document["correspondent"] = CurrentName(document["correspondent"], taxonomy.Correspondents);
        document["document_type"] = CurrentName(document["document_type"], taxonomy.DocumentTypes);
        document["tags"] = new JsonArray(document["tags"]!.AsArray()
            .Select(id => (JsonNode?)JsonValue.Create(CurrentName(id, taxonomy.Tags))).ToArray());
        foreach (var field in new[] { "tags", "correspondents", "document_types" })
        {
            var entities = payload["allowed_taxonomy"]![field]!.AsArray();
            payload["allowed_taxonomy"]![field] = new JsonArray(entities.Select(e =>
                (JsonNode?)JsonValue.Create(e!["name"]!.GetValue<string>())).ToArray());
        }
        return payload.ToJsonString();
    }

    public static string Resolve(string raw, PaperlessDocument document, PaperlessTaxonomy taxonomy, int pageCount)
    {
        // Parse with JsonDocument first to reject duplicate keys before JsonNode lookup.
        using var parsed = System.Text.Json.JsonDocument.Parse(raw);
        RejectDuplicateKeys(parsed.RootElement);
        var intent = JsonNode.Parse(raw) as JsonObject ?? throw new InvalidDataException("named_intent_object_required");
        foreach (var (field, entities) in new[] { ("correspondent", taxonomy.Correspondents), ("document_type", taxonomy.DocumentTypes) })
        {
            if (intent[field] is not JsonObject item) throw new InvalidDataException("named_field_required");
            if (item["value"] is not null) item["value"] = ResolveName(item["value"], entities);
        }
        if (intent["add_tags"] is not JsonArray additions) throw new InvalidDataException("named_tags_required");
        foreach (var addition in additions)
        {
            if (addition is not JsonObject tag || tag.Count != 2 || !tag.ContainsKey("name") || !tag.ContainsKey("evidence"))
                throw new InvalidDataException("named_tag_fields_invalid");
            var id = ResolveName(tag["name"], taxonomy.Tags);
            tag.Remove("name");
            tag["id"] = id;
        }
        // No cleanup: duplicate/existing/protected tags, keep/value errors and other
        // schema errors must still fail the production validator.
        return IntentValidator.Validate(intent.ToJsonString(), document, taxonomy, pageCount).GetRawText();
    }

    private static string? CurrentName(JsonNode? id, IReadOnlyList<NamedEntity> entities) => id is null ? null :
        entities.Single(e => e.Id == id.GetValue<int>()).Name;

    private static int ResolveName(JsonNode? value, IReadOnlyList<NamedEntity> entities)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var name))
            throw new InvalidDataException("taxonomy_name_required");
        var matches = entities.Where(e => string.Equals(e.Name, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("taxonomy_name_unknown_or_ambiguous");
        return matches[0].Id;
    }

    private static void RejectDuplicateKeys(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw new InvalidDataException("duplicate_json_property");
            foreach (var property in properties) RejectDuplicateKeys(property.Value);
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateKeys(child);
    }
}
