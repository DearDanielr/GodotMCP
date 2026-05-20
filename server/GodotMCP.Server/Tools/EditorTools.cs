using System.Text.Json.Nodes;
using GodotMCP.Server.Adapters;
using GodotMCP.Server.Mcp;
using static GodotMCP.Server.Tools.SchemaBuilder;

namespace GodotMCP.Server.Tools;

public static class EditorTools
{
    public static void Register(ToolRegistry registry, AdapterRegistry adapters)
    {
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_get_state",
            Description: "Returns the current editor state: open scenes, the active edited scene, selected node paths, dirty flag, and the project's Godot version. The AI's situational awareness check before mutating anything.",
            InputSchema: Object(),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_get_scene_tree",
            Description: "Returns the hierarchy of the currently edited scene as a tree of {path, name, type, children, script}. Optionally include all exported and built-in property values when 'include_properties' is true (much larger payload). This is the AI's eyes on the scene structure.",
            InputSchema: Object(
                ("include_properties", Bool("If true, include all property values for each node. Default false."), false),
                ("scene_path", String("Optional res:// path of a scene to inspect instead of the active one."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_get_class_reference",
            Description: "Introspects a Godot class via ClassDB: returns its parent, methods (name, args, return type), properties, signals, and enum constants. Use this to discover any engine API surface live instead of guessing signatures.",
            InputSchema: Object(
                ("class_name", String("The Godot class name, e.g. 'Node2D', 'RigidBody3D', 'AnimationPlayer'."), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_add_node",
            Description: "Creates a new node of the given Godot class and adds it under the parent. Sets the new node's owner to the edited scene root so it persists on save. Returns the new node's path.",
            InputSchema: Object(
                ("parent_path", String("NodePath of the parent inside the edited scene, e.g. '/root/Main' or '.'  for the scene root."), true),
                ("type", String("Godot class name to instantiate, e.g. 'Node2D', 'Sprite2D', 'AnimationPlayer'."), true),
                ("name", String("Name for the new node. Falls back to the class name if omitted."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_remove_node",
            Description: "Removes (queue_free) a node from the edited scene by path. Errors if the path does not resolve.",
            InputSchema: Object(
                ("path", String("NodePath of the node to remove."), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_set_node_property",
            Description: "Sets a property on a node in the edited scene. The value is interpreted in Godot's variant system; pass numbers, booleans, strings, or arrays/objects for compound types like Vector2 (e.g. {x:1,y:2}).",
            InputSchema: Object(
                ("path", String("NodePath of the node."), true),
                ("property", String("Property name, e.g. 'position', 'modulate', 'text'."), true),
                ("value", AnyValue("New value. Vectors as {x,y[,z[,w]]}, colors as {r,g,b[,a]} or '#rrggbb'."), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_get_node_property",
            Description: "Reads one property's current saved value from a node in the edited scene.",
            InputSchema: Object(
                ("path", String("NodePath of the node."), true),
                ("property", String("Property name."), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_save_scene",
            Description: "Saves the currently edited scene (or the one at the given path if open). The scene must have a file path; new scenes need editor_save_scene_as first.",
            InputSchema: Object(
                ("scene_path", String("Optional res:// path of an open scene to save. Defaults to the active scene."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));
    }

    private static void Add(ToolRegistry registry, AdapterRegistry adapters, ToolDefinition partial)
    {
        var bound = partial with { Handler = SurfaceTools.ForwardAction(adapters, Surface.Editor, partial.Name) };
        registry.Add(bound);
    }
}
