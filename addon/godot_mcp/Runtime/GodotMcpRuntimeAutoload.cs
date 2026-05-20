using System.Collections.Generic;
using Godot;
using GodotMcp.Shared;

namespace GodotMcp.Runtime;

public partial class GodotMcpRuntimeAutoload : AdapterBase
{
    protected override string Surface => "runtime";
    protected override string ProjectPath => ProjectSettings.GlobalizePath("res://");

    public override void _Ready()
    {
        // Autoloads are loaded for both editor and player. We only want to bind to
        // the MCP server when actually running the game; in the editor the editor
        // adapter handles things and registering twice would conflict.
        if (Engine.IsEditorHint())
        {
            ProcessMode = ProcessModeEnum.Disabled;
            return;
        }
    }

    public override void _EnterTree()
    {
        if (Engine.IsEditorHint()) return;
        base._EnterTree();
    }

    public override void _ExitTree()
    {
        if (Engine.IsEditorHint()) return;
        base._ExitTree();
    }

    protected override void RegisterHandlers(Dictionary<string, AdapterHandler> handlers)
    {
        RuntimeHandlers.Register(this, handlers);
    }

    protected override void RegisterAsyncHandlers(Dictionary<string, AsyncAdapterHandler> handlers)
    {
        RuntimeHandlers.RegisterAsync(this, handlers);
    }
}
