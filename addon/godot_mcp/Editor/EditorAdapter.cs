#if TOOLS
using System.Collections.Generic;
using Godot;
using GodotMcp.Shared;

namespace GodotMcp.Editor;

[Tool]
public partial class EditorAdapter : AdapterBase
{
    protected override string Surface => "editor";
    protected override string ProjectPath => ProjectSettings.GlobalizePath("res://");

    protected override void RegisterHandlers(Dictionary<string, AdapterHandler> handlers)
    {
        EditorHandlers.Register(handlers);
    }
}
#endif
