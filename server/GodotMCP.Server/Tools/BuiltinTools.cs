using System.Text.Json.Nodes;
using GodotMCP.Server.Adapters;
using GodotMCP.Server.Mcp;

namespace GodotMCP.Server.Tools;

public static class BuiltinTools
{
    public static void Register(ToolRegistry registry, AdapterRegistry adapters)
    {
        registry.Add(new ToolDefinition(
            Name: "mcp_status",
            Description: "Reports which Godot adapters (editor, runtime) are currently connected to this MCP server. Call this first when you're unsure if the editor is running or a game session is live.",
            InputSchema: SchemaBuilder.Object(),
            RequiresSurface: null,
            Handler: (args, ct) => Task.FromResult<JsonNode?>(adapters.Snapshot())
        ));

        registry.Add(new ToolDefinition(
            Name: "mcp_list_tools_by_surface",
            Description: "Returns the catalog of MCP tools grouped by which Godot surface (editor or runtime) they target. Useful when you want to know what you can do without paging through the full tool list.",
            InputSchema: SchemaBuilder.Object(),
            RequiresSurface: null,
            Handler: (args, ct) =>
            {
                var editor = new JsonArray();
                var runtime = new JsonArray();
                var builtin = new JsonArray();
                foreach (var t in registry.ListWire())
                {
                    var name = (string?)t!["name"];
                    if (name is null) continue;
                    if (name.StartsWith("editor_", StringComparison.Ordinal)) editor.Add(name);
                    else if (name.StartsWith("runtime_", StringComparison.Ordinal)) runtime.Add(name);
                    else builtin.Add(name);
                }
                return Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["editor"] = editor,
                    ["runtime"] = runtime,
                    ["builtin"] = builtin,
                });
            }
        ));
    }
}
