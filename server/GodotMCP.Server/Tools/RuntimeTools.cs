using GodotMCP.Server.Adapters;
using GodotMCP.Server.Mcp;
using static GodotMCP.Server.Tools.SchemaBuilder;

namespace GodotMCP.Server.Tools;

public static class RuntimeTools
{
    public static void Register(ToolRegistry registry, AdapterRegistry adapters)
    {
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_get_tree",
            Description: "Returns the live SceneTree of the running game: {path, name, type, children, script}, reflecting actual instantiated node state — not the saved .tscn. Optionally include current property values when 'include_properties' is true.",
            InputSchema: Object(
                ("include_properties", Bool("If true, include current property values per node. Default false."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_get_node_property",
            Description: "Reads a property's current value from a live node in the running game.",
            InputSchema: Object(
                ("path", String("NodePath of the live node, rooted at the SceneTree (e.g. '/root/Main/Player')."), true),
                ("property", String("Property name."), true)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_set_node_property",
            Description: "Sets a property on a live node, queued to run between frames to avoid mid-physics-step mutation. Returns once the change has been applied.",
            InputSchema: Object(
                ("path", String("NodePath of the live node."), true),
                ("property", String("Property name."), true),
                ("value", AnyValue("New value. See editor_set_node_property for type conventions."), true)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_call_method",
            Description: "Invokes a method on a live node with the given arguments. Args is a JSON array of Variant-compatible values. Returns the method's return value.",
            InputSchema: Object(
                ("path", String("NodePath of the live node."), true),
                ("method", String("Method name."), true),
                ("args", new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Arguments in order. Defaults to empty.",
                    ["items"] = new System.Text.Json.Nodes.JsonObject(),
                }, false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_get_performance",
            Description: "Returns live performance metrics from Performance.GetMonitor: FPS, frame time, physics time, node count, draw calls, memory.",
            InputSchema: Object(),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_inject_action",
            Description: "Synthesizes a named InputMap action press, release, or full press+release. Lets you trigger gameplay input as if the user did. The action must exist in the project's input map.",
            InputSchema: Object(
                ("action", String("Input action name from the project's input map."), true),
                ("mode", String("'press', 'release', or 'tap' (default 'tap' = press then release one frame later)."), false),
                ("strength", new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Action strength for analog actions, 0..1. Default 1.",
                }, false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));
    }

    private static void Add(ToolRegistry registry, AdapterRegistry adapters, ToolDefinition partial)
    {
        var bound = partial with { Handler = SurfaceTools.ForwardAction(adapters, Surface.Runtime, partial.Name) };
        registry.Add(bound);
    }
}
