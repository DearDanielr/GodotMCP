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

        // ─── step frames (deterministic stepping) ───────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_step_frames",
            Description: "Advances the game by N idle frames, then (by default) pauses again. Use this for deterministic 'tick once and observe' loops. unpause_first defaults true (so it works whether already paused); repause_after defaults true.\nExample: { frames: 5 } steps 5 frames then pauses.",
            InputSchema: Object(
                ("frames", Integer("Number of process_frame ticks to advance. Capped at 240. Default 1."), false),
                ("unpause_first", Bool("Set Paused=false before stepping. Default true."), false),
                ("repause_after", Bool("Set Paused=true after stepping. Default true."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        // ─── signals ────────────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_connect_signal",
            Description: "Connects a signal emitted by 'from' to a method on 'to'. Both targets accept path or instance_id.\nExample: { from_path: '/root/Main/Player', signal: 'died', to_path: '/root/Main/HUD', method: '_on_player_died' }.",
            InputSchema: Object(
                ("from_path", String("Source node path."), false),
                ("from_instance_id", String("Source instance id."), false),
                ("signal", String("Signal name on the source node."), true),
                ("to_path", String("Target node path."), false),
                ("to_instance_id", String("Target instance id."), false),
                ("method", String("Method on the target node to call."), true),
                ("flags", Integer("Optional Godot connect flags bitmask (e.g. 1=deferred, 4=one-shot)."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_disconnect_signal",
            Description: "Disconnects a previously-connected signal binding. Same shape as connect; method must match.",
            InputSchema: Object(
                ("from_path", String("Source node path."), false),
                ("from_instance_id", String("Source instance id."), false),
                ("signal", String("Signal name."), true),
                ("to_path", String("Target node path."), false),
                ("to_instance_id", String("Target instance id."), false),
                ("method", String("Bound method name."), true)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_emit_signal",
            Description: "Emits a signal on a live node with the given positional arguments. Useful for triggering game events from the AI.\nExample: { path: '/root/Main/Player', signal: 'damaged', args: [10] }.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("signal", String("Signal name."), true),
                ("args", Array(AnyValue(), "Positional args for the signal."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        // ─── groups ─────────────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_add_to_group",
            Description: "Adds a live node to a group.\nExample: { path: '/root/Main/Enemy42', group: 'hostile' }.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("group", String("Group name."), true),
                ("persistent", Bool("Persistent membership (default false)."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_remove_from_group",
            Description: "Removes a live node from a group.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("group", String("Group name."), true)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        // ─── physics queries ────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_physics_raycast_2d",
            Description: "Casts a 2D ray through the active World2D and returns {hit, position, normal, collider_path, collider_id, ...} or {hit:false}.\nExample: { from: {x:0,y:0}, to: {x:100,y:0}, mask: 1 }.",
            InputSchema: Object(
                ("from", AnyValue("Origin {x,y}."), true),
                ("to", AnyValue("End point {x,y}."), true),
                ("mask", Integer("Collision mask bitmask. Default = all."), false),
                ("collide_with_areas", Bool("Include Area2D in results. Default false."), false),
                ("collide_with_bodies", Bool("Include CollisionObject2D bodies. Default true."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_physics_raycast_3d",
            Description: "Casts a 3D ray through the active World3D. Same shape as the 2D version but with {x,y,z}.",
            InputSchema: Object(
                ("from", AnyValue("Origin {x,y,z}."), true),
                ("to", AnyValue("End point {x,y,z}."), true),
                ("mask", Integer("Collision mask bitmask."), false),
                ("collide_with_areas", Bool("Default false."), false),
                ("collide_with_bodies", Bool("Default true."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_physics_overlap_point_2d",
            Description: "Returns 2D colliders that overlap a point.\nExample: { point: {x:50,y:50}, max_results: 16 }.",
            InputSchema: Object(
                ("point", AnyValue("Point {x,y}."), true),
                ("mask", Integer("Collision mask."), false),
                ("max_results", Integer("Default 32."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        // ─── animation ──────────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_animation_list",
            Description: "Lists the animations available on an AnimationPlayer node.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_animation_play",
            Description: "Plays an animation on an AnimationPlayer. Omit 'animation' to resume the current one.\nExample: { path: '/root/Main/Player/AnimationPlayer', animation: 'idle', speed: 1.0 }.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("animation", String("Animation name (optional)."), false),
                ("speed", Number("Playback speed multiplier. Default 1."), false),
                ("from_end", Bool("Play in reverse. Default false."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_animation_stop",
            Description: "Stops the AnimationPlayer. 'keep_state' true preserves current property values.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("keep_state", Bool("Default false."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_animation_seek",
            Description: "Seeks the AnimationPlayer to a specific time.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("time", Number("Time in seconds."), true),
                ("update", Bool("Apply animation effects immediately. Default true."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        // ─── scene ops ──────────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_instantiate_scene",
            Description: "Loads a .tscn at runtime and adds an instance as child of a live node.\nExample: { scene_path: 'res://scenes/enemy.tscn', parent_path: '/root/Main/Enemies', name: 'Goblin1' }.",
            InputSchema: Object(
                ("scene_path", String("res:// path of the .tscn."), true),
                ("parent_path", String("Absolute path to the parent."), true),
                ("name", String("Optional name."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_free_node",
            Description: "Queues a live node for deletion (Node.QueueFree).",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_change_scene",
            Description: "Calls SceneTree.ChangeSceneToFile to swap the current scene at runtime.",
            InputSchema: Object(("scene_path", String("res:// path of the next scene."), true)),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true));

        // ─── watches (push) ─────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_watch_property",
            Description: "Subscribes to changes on a node property. The server pushes 'notifications/message' with name='watch_changed' each time the JSON-serialized value differs, and 'watch_ended' if the node is freed. Returns a watch_id used with runtime_unwatch_property.\nExample: { path: '/root/Main/Player', property: 'position' }.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("property", String("Property to watch."), true)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_unwatch_property",
            Description: "Cancels a previously-registered watch.",
            InputSchema: Object(("watch_id", String("Id from runtime_watch_property."), true)),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_list_watches",
            Description: "Lists currently active property watches.",
            InputSchema: Object(),
            RequiresSurface: Surface.Runtime,
            Handler: null!));

        // ─── eval (UNSAFE) ──────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "runtime_eval_expression",
            Description: "Evaluates a Godot Expression in the runtime process with the SceneTree root as the default base. Unbounded — disabled unless server started with --allow-unsafe.\nExample: { expression: 'get_node(\"Main/Player\").position' } or { expression: 'a + b', inputs: { a: 1, b: 2 } }.",
            InputSchema: Object(
                ("expression", String("Expression source."), true),
                ("inputs", AnyValue("Optional {name: value} map of named inputs."), false),
                ("base_path", String("Optional node path to use as the base instance. Defaults to SceneTree.Root."), false)
            ),
            RequiresSurface: Surface.Runtime,
            Handler: null!,
            Mutates: true,
            Unsafe: true));
    }

    private static void Add(ToolRegistry registry, AdapterRegistry adapters, ToolDefinition partial)
    {
        var bound = partial with { Handler = SurfaceTools.ForwardAction(adapters, Surface.Runtime, partial.Name) };
        registry.Add(bound);
    }
}
