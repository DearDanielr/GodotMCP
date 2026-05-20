using System.Text.Json.Nodes;
using GodotMCP.Server.Adapters;

namespace GodotMCP.Server.Tools;

public delegate Task<JsonNode?> ToolHandler(JsonObject args, CancellationToken ct);

/// One tool exposed to the MCP client.
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonObject InputSchema,
    Surface? RequiresSurface,
    ToolHandler Handler,
    bool Mutates = false)
{
    public JsonObject ToWire()
    {
        return new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["inputSchema"] = InputSchema.DeepClone(),
        };
    }
}

public static class SchemaBuilder
{
    public static JsonObject Object(params (string name, JsonObject schema, bool required)[] fields)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema, req) in fields)
        {
            props[name] = schema.DeepClone();
            if (req) required.Add(name);
        }
        var obj = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0) obj["required"] = required;
        return obj;
    }

    public static JsonObject String(string? description = null)
    {
        var o = new JsonObject { ["type"] = "string" };
        if (description is not null) o["description"] = description;
        return o;
    }

    public static JsonObject Integer(string? description = null)
    {
        var o = new JsonObject { ["type"] = "integer" };
        if (description is not null) o["description"] = description;
        return o;
    }

    public static JsonObject Bool(string? description = null)
    {
        var o = new JsonObject { ["type"] = "boolean" };
        if (description is not null) o["description"] = description;
        return o;
    }

    public static JsonObject AnyValue(string? description = null)
    {
        var o = new JsonObject();
        if (description is not null) o["description"] = description;
        return o;
    }

    public static JsonObject StringArray(string? description = null)
    {
        var o = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string" },
        };
        if (description is not null) o["description"] = description;
        return o;
    }

    public static JsonObject Array(JsonObject itemSchema, string? description = null)
    {
        var o = new JsonObject
        {
            ["type"] = "array",
            ["items"] = itemSchema.DeepClone(),
        };
        if (description is not null) o["description"] = description;
        return o;
    }

    public static JsonObject Number(string? description = null)
    {
        var o = new JsonObject { ["type"] = "number" };
        if (description is not null) o["description"] = description;
        return o;
    }
}
