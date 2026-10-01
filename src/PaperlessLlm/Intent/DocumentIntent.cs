using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperlessLlm.Intent;

/// <summary>Managed fields only. There is deliberately no clear, delete or taxonomy creation operation.</summary>
public static class DocumentIntent
{
    public const string Version = "1";
    public static JsonElement Schema { get; } = CreateSchema();

    private static JsonElement CreateSchema()
    {
        static JsonObject Texts() => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        static JsonObject Object(params (string Name, JsonNode Value)[] properties) => new()
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject(properties.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Name, p.Value))),
            ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray())
        };
        static JsonObject Action() => new() { ["type"] = "string", ["enum"] = new JsonArray("keep", "set") };
        static JsonObject Field(string type) => Object(("action", Action()),
            ("value", new JsonObject { ["type"] = new JsonArray(type, "null") }), ("evidence", Texts()));
        var schema = Object(
            ("schema_version", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Version) }),
            ("title", Field("string")), ("date", Field("string")),
            ("correspondent", Field("integer")), ("document_type", Field("integer")),
            ("add_tags", new JsonObject { ["type"] = "array", ["items"] = Object(
                ("id", new JsonObject { ["type"] = "integer" }), ("evidence", Texts())) }),
            ("ocr", Object(("action", Action()), ("pages", new JsonObject { ["type"] = "array", ["items"] = Object(
                ("page", new JsonObject { ["type"] = "integer" }), ("text", new JsonObject { ["type"] = "string" }),
                ("complete", new JsonObject { ["type"] = "boolean" }), ("uncertainty", Texts())) }), ("evidence", Texts()))),
            ("uncertainty", Texts()));
        return JsonSerializer.SerializeToElement(schema);
    }
}
