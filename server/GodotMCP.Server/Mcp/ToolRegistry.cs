using System.Text.Json.Nodes;
using GodotMCP.Server.Adapters;
using GodotMCP.Server.Tools;

namespace GodotMCP.Server.Mcp;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.Ordinal);

    public void Add(ToolDefinition tool)
    {
        if (!_tools.TryAdd(tool.Name, tool))
            throw new InvalidOperationException($"Tool '{tool.Name}' is registered twice.");
    }

    public bool TryGet(string name, out ToolDefinition tool) => _tools.TryGetValue(name, out tool!);

    public JsonArray ListWire()
    {
        var arr = new JsonArray();
        foreach (var t in _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            arr.Add(t.ToWire());
        return arr;
    }

    public int Count => _tools.Count;
}
