using System.Text.Json.Nodes;
using GodotMCP.Server.Adapters;
using GodotMCP.Server.Mcp;
using static GodotMCP.Server.Tools.SchemaBuilder;

namespace GodotMCP.Server.Tools;

public static class EditorTools
{
    private static readonly JsonObject PathArg = String("NodePath of the node, relative to the edited scene root, e.g. '/Player' or 'Player/Sprite'. '.' = root.");
    private static readonly JsonObject InstanceIdArg = String("Stable instance id (numeric string) returned by other tools. Alternative to 'path'; survives renames within a session.");

    public static void Register(ToolRegistry registry, AdapterRegistry adapters)
    {
        // ─── state / introspection ─────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_get_state",
            Description: "Returns the current editor state: open scenes, the active edited scene, selected node paths, dirty flag, project Godot version. Situational-awareness check before mutating anything.\nExample call: {} (no args).",
            InputSchema: Object(),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_get_scene_tree",
            Description: "Returns the hierarchy of the currently edited scene as a tree of {path, name, type, id, script, children}. Pass 'max_depth' to cap recursion. Set 'include_properties' true to include storage-flagged property values (much larger payload).\nExample: { include_properties: false, max_depth: 5 }.",
            InputSchema: Object(
                ("include_properties", Bool("Include all storage-flagged property values per node. Default false."), false),
                ("max_depth", Integer("Maximum recursion depth. -1 = unlimited. Default -1."), false),
                ("scene_path", String("Optional res:// path of a non-active open scene to inspect."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_get_class_reference",
            Description: "Introspects a Godot class via ClassDB: parent, methods, properties, signals. Use this to discover engine API surface live instead of guessing.\nExample: { class_name: 'RigidBody2D' }.",
            InputSchema: Object(
                ("class_name", String("Godot class name, e.g. 'Node2D', 'RigidBody3D'."), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_resolve_id",
            Description: "Given a stable instance id (from another tool's result), returns the current path, name and class. Use to recover a reference after the user may have renamed/moved the node.\nExample: { instance_id: '4294967296' }.",
            InputSchema: Object(("instance_id", InstanceIdArg, true)),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        // ─── listings ──────────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_list_classes",
            Description: "Lists Godot class names, optionally filtered by inheritance and substring. Use this to find what node types exist before adding one — instead of guessing.\nExample: { inherits_from: 'Node2D', concrete_only: true, name_contains: 'Sprite' }.",
            InputSchema: Object(
                ("inherits_from", String("Only classes that inherit from this class (anywhere in the chain)."), false),
                ("name_contains", String("Case-insensitive substring filter."), false),
                ("concrete_only", Bool("Only classes that can be instantiated. Default true."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_list_node_methods",
            Description: "Lists method names available on a specific node (including ones from any attached script).\nExample: { path: 'Player' }.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("include_internal", Bool("Include methods starting with '_'. Default false."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_list_node_properties",
            Description: "Lists property {name, type} pairs available on a node (including script-defined). Includes editor-visible and storage properties.\nExample: { path: 'Player/Sprite' }.",
            InputSchema: Object(("path", PathArg, false), ("instance_id", InstanceIdArg, false)),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_list_node_signals",
            Description: "Lists signal names emitted by a specific node (including script-defined).",
            InputSchema: Object(("path", PathArg, false), ("instance_id", InstanceIdArg, false)),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_list_node_groups",
            Description: "Lists the groups a node belongs to in the edited scene.",
            InputSchema: Object(("path", PathArg, false), ("instance_id", InstanceIdArg, false)),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_list_input_actions",
            Description: "Lists the project's InputMap: every action with its deadzone and bound events. Use this before runtime_inject_action to learn what actions are defined.",
            InputSchema: Object(),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        // ─── search ────────────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_find_nodes",
            Description: "Walks the edited scene tree and returns nodes matching the filter. AND of all provided filters.\nExample: { type: 'CharacterBody2D', name_contains: 'enemy', in_group: 'hostile', limit: 50 }.",
            InputSchema: Object(
                ("type", String("Match nodes whose class is this or inherits from it."), false),
                ("name_contains", String("Case-insensitive substring of node name."), false),
                ("in_group", String("Only nodes that are members of this group."), false),
                ("limit", Integer("Max matches to return. Default 100."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_grep_project",
            Description: "Pattern search across project files (.gd, .cs, .tscn, .tres, .gdshader by default). Returns {file, line, text} hits. Use 'regex: true' to use .NET regex syntax.\nExample: { pattern: 'func _ready', regex: false, limit: 100 }.",
            InputSchema: Object(
                ("pattern", String("Substring or regex pattern to search for."), true),
                ("regex", Bool("Treat 'pattern' as regex. Default false (literal substring)."), false),
                ("extensions", String("Comma-separated extensions to include, e.g. '.gd,.cs'. Default '.gd,.cs,.tscn,.tres,.gdshader'."), false),
                ("limit", Integer("Max hits. Default 200."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        // ─── observation ───────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_get_logs",
            Description: "Returns lines from the Godot log file. By default the last 200 lines; set 'since_last_call: true' to only get new lines since the last call (cheaper for tail-following).\nExample: { tail_lines: 100 } or { since_last_call: true }.",
            InputSchema: Object(
                ("tail_lines", Integer("Number of trailing lines to return when not in since-last-call mode. Default 200."), false),
                ("since_last_call", Bool("If true, return only lines appended since the last get_logs call. Default false."), false),
                ("path", String("Override the log file path (must be an absolute OS path). Default uses the project's configured log path."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_screenshot_viewport",
            Description: "Captures the edited scene's main viewport as a PNG. Returns an image content block.\nExample: { max_side: 1024 } to downscale so the largest dimension is <= 1024.",
            InputSchema: Object(
                ("max_side", Integer("Optional downscale: longest dimension is clamped to this many pixels."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        // ─── mutation ──────────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_add_node",
            Description: "Instantiates a Godot class and adds it under a parent. Sets owner to scene root so it persists on save. Returns {path, name, type, id} where 'id' is the stable instance id.\nExample: { parent_path: '.', type: 'Node2D', name: 'Player' }.",
            InputSchema: Object(
                ("parent_path", String("Parent NodePath in the edited scene. '.' for scene root."), true),
                ("type", String("Godot class to instantiate, e.g. 'Node2D'. Use editor_list_classes to discover valid types."), true),
                ("name", String("Name for the new node. Defaults to the class name."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_remove_node",
            Description: "Removes a node from the edited scene.",
            InputSchema: Object(("path", PathArg, false), ("instance_id", InstanceIdArg, false)),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_set_node_property",
            Description: "Sets one property on one node. Vectors as {x,y,z}, colors as {r,g,b,a} or '#rrggbb'. Use editor_set_node_properties for bulk edits — it's much faster than many round-trips.\nExample: { path: 'Player', property: 'position', value: {x: 100, y: 200} }.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("property", String("Property name."), true),
                ("value", AnyValue("New value."), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_set_node_properties",
            Description: "Bulk version of editor_set_node_property. Each set item is {path|instance_id, property, value}. Failures don't abort the whole batch — results array reports each one.\nExample: { sets: [{path:'Player', property:'position', value:{x:0,y:0}}, {path:'Player', property:'rotation', value:1.57}] }.",
            InputSchema: Object(
                ("sets", Array(
                    Object(
                        ("path", PathArg, false),
                        ("instance_id", InstanceIdArg, false),
                        ("property", String("Property name."), true),
                        ("value", AnyValue("New value."), true)
                    ),
                    "List of property assignments to apply."), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_get_node_property",
            Description: "Reads one property's current saved value from a node.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("property", String("Property name."), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_save_scene",
            Description: "Saves the currently edited scene, or the scene at 'scene_path' if given.",
            InputSchema: Object(("scene_path", String("Optional res:// path of an open scene to save."), false)),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_attach_script",
            Description: "Attaches an existing script resource to a node. The script file must already exist (use editor_write_script first).\nExample: { path: 'Player', script_path: 'res://scripts/player.gd' }.",
            InputSchema: Object(
                ("path", PathArg, false),
                ("instance_id", InstanceIdArg, false),
                ("script_path", String("res:// path of a Script resource."), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        // ─── scripts / build ───────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_read_script",
            Description: "Reads a script file as text. Allowed extensions: .gd, .cs, .gdshader.\nExample: { path: 'res://scripts/player.gd' }.",
            InputSchema: Object(("path", String("res:// path of the script file."), true)),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_write_script",
            Description: "Writes (creates or overwrites) a script file. Prefer editor_patch_script for surgical changes to existing files. Triggers a filesystem rescan so the new file becomes visible to the editor.\nExample: { path: 'res://scripts/player.gd', content: 'extends Node2D\\n\\nfunc _ready():\\n\\tpass\\n' }.",
            InputSchema: Object(
                ("path", String("res:// path. Must end in .gd, .cs, or .gdshader."), true),
                ("content", String("Full file content."), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_patch_script",
            Description: "Applies anchor-based edits to an existing script. Each edit replaces an exact 'old' substring with 'new'. The 'old' anchor must match uniquely in the file — otherwise the edit fails (include surrounding lines if needed to disambiguate). Stops on the first failed edit.\nExample: { path: 'res://scripts/player.gd', edits: [{ old: 'speed = 100', new: 'speed = 250' }] }.",
            InputSchema: Object(
                ("path", String("res:// path of the script to patch."), true),
                ("edits", Array(
                    Object(
                        ("old", String("Exact text to find. Must be unique in the file."), true),
                        ("new", String("Replacement text."), true)
                    ),
                    "Sequence of {old, new} replacements applied in order."), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_build_project",
            Description: "Runs 'dotnet build' in the project root. Required after creating/modifying C# scripts before the new types become instantiable. Returns structured 'errors' / 'warnings' arrays of {file, line, severity, code, message} parsed from MSBuild output, plus raw stdout/stderr. Takes 5-60s; use the long timeout.\nExample: {} (no args).",
            InputSchema: Object(),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        // ─── play / stop ───────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_play_scene",
            Description: "Tells the editor to start playing. With no args plays the currently edited scene (or main scene if none open). With 'scene_path' plays that scene specifically. Spawns a child Godot process; observe via runtime_* tools once the game's autoload connects.\nExample: { scene_path: 'res://scenes/main.tscn' } or {} for current.",
            InputSchema: Object(("scene_path", String("Optional res:// path to a specific scene to play."), false)),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_stop_play",
            Description: "Stops the currently playing scene (if any).",
            InputSchema: Object(),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_is_playing",
            Description: "Returns whether the editor is currently playing a scene, and which scene path is running.",
            InputSchema: Object(),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        // ─── scene open / create / instantiate ─────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_open_scene",
            Description: "Opens a .tscn file in the editor (becomes the edited scene).\nExample: { path: 'res://scenes/level1.tscn' }.",
            InputSchema: Object(("path", String("res:// path of a .tscn file."), true)),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_create_scene",
            Description: "Packs the currently edited scene root (or a subtree) into a new .tscn file. Use this to extract a node tree into a reusable scene.\nExample: { out_path: 'res://scenes/enemy.tscn', path: 'Enemies/Goblin' } or { out_path: '...', path: '.' } for the whole scene.",
            InputSchema: Object(
                ("out_path", String("Output res:// path. Must end in '.tscn'."), true),
                ("path", String("Subtree root to pack. '.' or omitted = edited scene root."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_instantiate_scene",
            Description: "Loads a .tscn and instantiates it as a child of an existing node in the edited scene.\nExample: { scene_path: 'res://scenes/enemy.tscn', parent_path: 'Enemies', name: 'Goblin1' }.",
            InputSchema: Object(
                ("scene_path", String("res:// path of the .tscn to instantiate."), true),
                ("parent_path", String("Parent NodePath in the edited scene. '.' = root."), true),
                ("name", String("Optional name for the instantiated node."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        // ─── project settings ──────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_get_project_setting",
            Description: "Reads a ProjectSettings value, e.g. 'application/config/name', 'display/window/size/viewport_width'.\nExample: { key: 'application/config/name' }.",
            InputSchema: Object(("key", String("Setting key (slash-delimited)."), true)),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_set_project_setting",
            Description: "Writes a ProjectSettings value. Pass persist:true to write project.godot (otherwise the change is only in memory).\nExample: { key: 'display/window/size/viewport_width', value: 1920, persist: true }.",
            InputSchema: Object(
                ("key", String("Setting key."), true),
                ("value", AnyValue("New value."), true),
                ("persist", Bool("Save to project.godot. Default false."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_list_project_settings",
            Description: "Lists all ProjectSettings keys (optionally filtered by prefix). Use this to discover what's available.\nExample: { prefix: 'physics/' }.",
            InputSchema: Object(("prefix", String("Filter to keys starting with this prefix."), false)),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_save_project_settings",
            Description: "Persists in-memory ProjectSettings changes to project.godot.",
            InputSchema: Object(),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        // ─── input map editing ─────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_add_input_action",
            Description: "Defines a new action in the InputMap and persists it. Use editor_bind_input_event to add keys/buttons.\nExample: { action: 'jump', deadzone: 0.2 }.",
            InputSchema: Object(
                ("action", String("Action name."), true),
                ("deadzone", Number("Deadzone for analog inputs (default 0.5)."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_remove_input_action",
            Description: "Removes an action from the InputMap.",
            InputSchema: Object(("action", String("Action name."), true)),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_bind_input_event",
            Description: "Adds a key/button/axis event to an existing action.\n'event' shapes: {kind:'key', key:'space'|'a'|'escape'|...}, {kind:'mouse_button', button:1}, {kind:'joy_button', button:0}, {kind:'joy_axis', axis:0, value:1.0}.\nExample: { action: 'jump', event: { kind: 'key', key: 'space' } }.",
            InputSchema: Object(
                ("action", String("Action name (must exist)."), true),
                ("event", Object(
                    ("kind", String("One of 'key', 'mouse_button', 'joy_button', 'joy_axis'."), true),
                    ("key", String("For 'key': key string like 'space', 'a', 'escape'."), false),
                    ("button", Integer("For 'mouse_button' / 'joy_button': button index."), false),
                    ("axis", Integer("For 'joy_axis': axis index."), false),
                    ("value", Number("For 'joy_axis': axis value, e.g. 1.0 or -1.0."), false)
                ), true)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_unbind_input_events",
            Description: "Removes all events bound to an action (action itself remains).",
            InputSchema: Object(("action", String("Action name."), true)),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        // ─── resources & files ─────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_read_resource",
            Description: "Reads a .tres / .res / built-in resource as JSON. Returns {path, class, properties}.\nExample: { path: 'res://data/enemy_stats.tres' }.",
            InputSchema: Object(("path", String("res:// path."), true)),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_write_resource",
            Description: "Writes (creates or modifies) a Resource file. To create new, set create:true and 'type' to the resource class. 'properties' is a {name: value} map applied before save.\nExample (modify): { path: 'res://data/foo.tres', properties: { hp: 100 } }.\nExample (create): { path: 'res://data/bar.tres', create: true, type: 'Resource', properties: { ... } }.",
            InputSchema: Object(
                ("path", String("res:// path."), true),
                ("create", Bool("Create new resource. Default false."), false),
                ("type", String("Resource class name (required if create:true)."), false),
                ("properties", AnyValue("Object map of properties to set."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_list_files",
            Description: "Lists files in a project directory. Use 'recursive' to walk subdirectories; 'extension' to filter by extension.\nExample: { dir: 'res://scenes', recursive: true, extension: '.tscn' }.",
            InputSchema: Object(
                ("dir", String("res:// directory. Default 'res://'."), false),
                ("recursive", Bool("Walk subdirectories. Default false."), false),
                ("extension", String("Filter to this extension (include the dot)."), false),
                ("limit", Integer("Max files to return. Default 500."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!));

        Add(registry, adapters, new ToolDefinition(
            Name: "editor_reload_scripts",
            Description: "Triggers an editor filesystem rescan so newly created/written files become visible. Call this after writing files outside the editor's own write path.",
            InputSchema: Object(),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true));

        // ─── eval (UNSAFE) ─────────────────────────────────────────────
        Add(registry, adapters, new ToolDefinition(
            Name: "editor_eval_expression",
            Description: "Evaluates a Godot Expression string in the editor process. Powerful but unbounded — disabled by default; start server with --allow-unsafe to enable. 'inputs' is a {name: value} map of named inputs that appear in the expression.\nExample: { expression: '2 + 2' } or { expression: 'a * b', inputs: { a: 3, b: 4 } }.",
            InputSchema: Object(
                ("expression", String("Expression source (GDScript-like)."), true),
                ("inputs", AnyValue("Optional {name: value} map of named inputs."), false)
            ),
            RequiresSurface: Surface.Editor,
            Handler: null!,
            Mutates: true,
            Unsafe: true));
    }

    private static void Add(ToolRegistry registry, AdapterRegistry adapters, ToolDefinition partial)
    {
        // editor_build_project needs a longer timeout — pin it on registration.
        var timeout = partial.Name == "editor_build_project" ? SurfaceTools.LongTimeout : SurfaceTools.DefaultTimeout;
        var bound = partial with { Handler = SurfaceTools.ForwardAction(adapters, Surface.Editor, partial.Name, timeout) };
        registry.Add(bound);
    }
}
