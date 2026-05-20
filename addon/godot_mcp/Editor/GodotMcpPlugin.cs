#if TOOLS
using Godot;

namespace GodotMcp.Editor;

[Tool]
public partial class GodotMcpPlugin : EditorPlugin
{
    private const string RuntimeAutoloadName = "GodotMcpRuntime";
    private const string RuntimeAutoloadPath = "res://addons/godot_mcp/Runtime/GodotMcpRuntimeAutoload.cs";

    private EditorAdapter? _adapter;

    public override void _EnterTree()
    {
        // Register the runtime autoload so the player-side adapter starts on game launch.
        AddAutoloadSingleton(RuntimeAutoloadName, RuntimeAutoloadPath);

        _adapter = new EditorAdapter { Name = "GodotMcpEditorAdapter" };
        AddChild(_adapter);

        GD.Print("[godot_mcp] Editor plugin enabled. The MCP server (godot-mcp-server) must be running for tool calls to reach this editor.");
    }

    public override void _ExitTree()
    {
        if (_adapter is not null)
        {
            _adapter.QueueFree();
            _adapter = null;
        }
        RemoveAutoloadSingleton(RuntimeAutoloadName);
    }
}
#endif
