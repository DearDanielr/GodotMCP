using System.Text.Json.Nodes;
using GodotMCP.Server.Adapters;
using GodotMCP.Server.Mcp;
using static GodotMCP.Server.Tools.SchemaBuilder;

namespace GodotMCP.Server.Tools;

public static class RuntimeTools
{
    private static readonly JsonObject PathArg = String("Absolute NodePath from the SceneTree root, e.g. '/root/Main/Player'.");
    private static readonly JsonObject InstanceIdArg = String("Stable instance id (numeric string). Alternative to 'path'.");

    public static void Register(ToolRegistry registry, AdapterRegistry adapters)
    {
        // ─── introspection ─────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_get_tree",
            Description: "Returns the live SceneTree of the running game (actual instantiated state, not the .tscn). Pass 'max_depth' to cap recursion or 'include_properties' for full snapshots.\nExample: { include_properties: false, max_depth: 4 }.",
            InputSchema: Object(
                ("include_properties", Bool("Include all storage-flagged property values per node. Default false."), false),
                ("max_depth", Integer("Max recursion depth. -1 = unlimited. Default -1."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_get_node_property",
            Description: "Reads a property's current value from a live node.\nExample: { path: '/root/Main/Player', property: 'velocity' }.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("property", String("Property name."), true)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_resolve_id",
            Description: "Look up the current path of a live object by its instance id.",
            InputSchema: Object(("instance_id", InstanceIdArg, true)),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        // ─── listings ──────────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_list_node_methods",
            Description: "Lists method names on a live node (including script-defined). Useful before calling runtime_call_method.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("include_internal", Bool("Include methods starting with '_'. Default false."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_list_node_properties",
            Description: "Lists {name, type} pairs of properties on a live node (including script-defined).",
            InputSchema: Object(("path", PathArg, false), ("instance_id", InstanceIdArg, false)),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_list_node_signals",
            Description: "Lists signal names emitted by a live node.",
            InputSchema: Object(("path", PathArg, false), ("instance_id", InstanceIdArg, false)),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_list_node_groups",
            Description: "Lists the groups a live node belongs to.",
            InputSchema: Object(("path", PathArg, false), ("instance_id", InstanceIdArg, false)),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_get_nodes_in_group",
            Description: "Returns all live nodes that are members of the named group.",
            InputSchema: Object(("group", String("Group name."), true)),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        // ─── search ────────────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_find_nodes",
            Description: "Walks the live SceneTree from /root and returns nodes matching the filter (AND of all filters).\nExample: { type: 'Bullet', limit: 200 } to find every Bullet currently alive.",
            InputSchema: Object(
                ("type", String("Class name or ancestor class to match."), false),
                ("name_contains", String("Case-insensitive substring of node name."), false),
                ("in_group", String("Only nodes in this group."), false),
                ("limit", Integer("Max matches. Default 100."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        // ─── observation ───────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_get_logs",
            Description: "Returns lines from the Godot log file as the running game writes them. Set 'since_last_call: true' for incremental polling.\nExample: { since_last_call: true } after the AI takes an action to see what the game printed.",
            InputSchema: Object(
                ("tail_lines", Integer("Trailing lines in default mode. Default 200."), false),
                ("since_last_call", Bool("If true, return only new lines since last call. Default false."), false),
                ("path", String("Override the log path (absolute OS path)."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_screenshot",
            Description: "Captures the current game framebuffer as a PNG. Returns an image content block. Use 'max_side' to downscale for context budget.\nExample: { max_side: 800 }.",
            InputSchema: Object(("max_side", Integer("Optional downscale cap on longest dimension."), false)),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_get_performance",
            Description: "Live perf metrics from Performance.GetMonitor: fps, process_ms, physics_ms, nodes, objects, draw_calls, mem_static_mb.",
            InputSchema: Object(),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        // ─── mutation ──────────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_set_node_property",
            Description: "Sets a property on a live node, queued to run between frames so it never collides with a physics step.\nExample: { path: '/root/Main/Player', property: 'modulate', value: {r:1,g:0,b:0,a:1} }.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("property", String("Property name."), true),
                ("value", AnyValue("New value."), true)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_set_node_properties",
            Description: "Bulk runtime property set. Each item is {path|instance_id, property, value}. Each failure is reported per-item; the batch does not abort.",
            InputSchema: Object(
                ("sets", Array(
                    Object(
                        ("path", PathArg, false),
                        ("instance_id", InstanceIdArg, false),
                        ("property", String("Property name."), true),
                        ("value", AnyValue("New value."), true)
                    ),
                    "List of property assignments."), true)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_call_method",
            Description: "Invokes a method on a live node with the given arguments.\nExample: { path: '/root/Main/Player', method: 'take_damage', args: [5] }.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("method", String("Method name."), true),
                ("args", Array(AnyValue(), "Positional arguments in order. Empty array if none."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_inject_action",
            Description: "Synthesizes a named InputMap action as if the user pressed/released it. 'tap' = press + release on next frame.\nExample: { action: 'jump', mode: 'tap' }. Use editor_list_input_actions to learn what's available.",
            InputSchema: Object(
                ("action", String("Action name."), true),
                ("mode", String("'press' | 'release' | 'tap'. Default 'tap'."), false),
                ("strength", Number("Strength 0..1 for analog actions. Default 1."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_set_paused",
            Description: "Pauses or unpauses the SceneTree. Paused nodes follow their ProcessMode rules; the MCP adapters keep working because they're set to ProcessModeEnum.Always.\nExample: { paused: true }.",
            InputSchema: Object(("paused", Bool("True to pause, false to resume."), true)),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        // ─── experimentation ───────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_snapshot_state",
            Description: "Captures property values of a subtree into an in-memory snapshot, returning a snapshot_id. Skips Object-typed properties (resource refs) since they don't restore cleanly. Use this before experimenting with runtime_set_node_property, then runtime_restore_state to undo.\nExample: { path: '/root/Main' } or {} for the whole tree.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_restore_state",
            Description: "Restores property values from a previously-taken snapshot. Reports how many properties were applied vs skipped (skipped if a node disappeared or a value no longer fits).",
            InputSchema: Object(("snapshot_id", String("Id returned by runtime_snapshot_state."), true)),
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
