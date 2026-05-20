# GodotMCP

An MCP (Model Context Protocol) server that exposes a live Godot 4.6 (C#) project to MCP clients such as Claude. Two adapter surfaces — **editor** (drive the editor to build your game) and **runtime** (manipulate a live running game) — share a server core but speak to different APIs because that's what Godot actually requires.

```
MCP client (Claude, etc.)
        │  JSON-RPC 2.0 over stdio
        ▼
┌─────────────────────────────┐
│   godot-mcp-server (.NET 8)  │  protocol, tool registry, routing
└─────────────────────────────┘
        │  framed JSON over local TCP (default 127.0.0.1:4936)
        │
   ┌────┴─────┐
   ▼          ▼
Editor       Runtime
adapter      adapter
(EditorPlugin) (autoload singleton)
```

The server is a plain .NET process — no Godot dependency, no engine launch required to test it. The adapters are C# scripts that ship inside an addon you drop into your Godot project.

## Repo layout

```
server/
  GodotMCP.Server.sln
  GodotMCP.Server/         the .NET MCP server (target: net8.0)
addon/
  godot_mcp/               Godot addon — copy into <your_project>/addons/godot_mcp/
    plugin.cfg
    Editor/                EditorPlugin + editor-side handlers
    Runtime/               Autoload + runtime-side handlers
    Shared/                Transport, JSON↔Variant, main-thread dispatcher
```

## Quick start

### 1. Build the server

```bash
cd server
dotnet build -c Release
```

The binary lands at `server/GodotMCP.Server/bin/Release/net8.0/godot-mcp-server.dll`. Run it with `dotnet GodotMCP.Server.dll` or publish a self-contained executable.

### 2. Install the addon in your Godot project

```bash
cp -r addon/godot_mcp <your_project>/addons/
```

Open the project in Godot 4.6, then **Project → Project Settings → Plugins** and enable **Godot MCP**. This:

- adds the editor adapter under the EditorPlugin, which connects to the server immediately
- registers a `GodotMcpRuntime` autoload so the runtime adapter starts whenever you Play the game

The Godot project must be C# enabled (it is the moment you create your first C# script). The addon's `.cs` files are picked up by your project's main assembly.

### 3. Wire it up to your MCP client

Example Claude Desktop config (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "godot": {
      "command": "dotnet",
      "args": ["/abs/path/to/server/GodotMCP.Server/bin/Release/net8.0/GodotMCP.Server.dll"]
    }
  }
}
```

For other clients, point them at the same executable. The server speaks newline-delimited JSON-RPC 2.0 on stdio per the MCP spec.

Connection order doesn't matter. The adapters retry with backoff, so you can launch the server before or after Godot is open.

## Server options

| Flag / Env | Default | Meaning |
| --- | --- | --- |
| `--port <n>` / `GODOT_MCP_PORT` | `4936` | TCP port for adapters to connect to |
| `--read-only` / `GODOT_MCP_READ_ONLY=1` | off | Reject any tool call that would mutate editor or runtime state |
| `--allow-unsafe` / `GODOT_MCP_ALLOW_UNSAFE=1` | off | Enable tools tagged `unsafe` (currently `editor_eval_expression`, `runtime_eval_expression`) |
| `--help` | — | Print usage |

Set `GODOT_MCP_PORT` in your shell before launching Godot if you've changed the server's port — the editor and runtime adapters read the same variable.

## Available tools (v0.3)

**Built-in**

- `mcp_status` — which adapters are connected, with Godot version and project path
- `mcp_list_tools_by_surface` — tool catalog grouped by surface

**Editor surface**

State / introspection
- `editor_get_state` — open scenes, edited scene, selected nodes
- `editor_get_scene_tree` — hierarchy (with `max_depth` and optional properties)
- `editor_get_class_reference` — ClassDB methods/properties/signals for any class
- `editor_resolve_id` — instance id → current path

Listings
- `editor_list_classes` — filterable by inheritance / substring / concrete-only
- `editor_list_node_methods` / `editor_list_node_properties` / `editor_list_node_signals` / `editor_list_node_groups`
- `editor_list_input_actions`
- `editor_list_project_settings` — filterable by prefix
- `editor_list_files` — directory walk with optional recursion / extension filter

Search
- `editor_find_nodes` — by type, name substring, group membership
- `editor_grep_project` — pattern search across .gd/.cs/.tscn/.tres/.gdshader

Observation (the AI's eyes and ears)
- `editor_get_logs` — tails the Godot log file, with `since_last_call` for incremental polling
- `editor_screenshot_viewport` — main viewport PNG, returned as an MCP image content block

Scene mutation
- `editor_add_node`, `editor_remove_node`
- `editor_set_node_property`, `editor_get_node_property`
- `editor_set_node_properties` — **bulk** version, one round trip for many edits
- `editor_save_scene`
- `editor_open_scene` — open a `.tscn`
- `editor_create_scene` — pack a subtree into a new `.tscn`
- `editor_instantiate_scene` — add a `.tscn` as child of a node
- `editor_attach_script`

Scripts & build
- `editor_read_script`, `editor_write_script`, `editor_patch_script` (anchor-based)
- `editor_reload_scripts` — trigger a filesystem rescan
- `editor_build_project` — `dotnet build`; returns **structured `errors`/`warnings`** arrays parsed from MSBuild output ({file, line, severity, code, message}), plus raw stdout/stderr

Project settings
- `editor_get_project_setting`, `editor_set_project_setting`, `editor_save_project_settings`

Input-map editing
- `editor_add_input_action`, `editor_remove_input_action`
- `editor_bind_input_event` (event kinds: `key`, `mouse_button`, `joy_button`, `joy_axis`)
- `editor_unbind_input_events`

Resources (.tres / .res)
- `editor_read_resource`, `editor_write_resource` (create with `create: true, type: ...`)

Play control
- `editor_play_scene` — play current, main, or a specific scene
- `editor_stop_play`, `editor_is_playing`

Eval (UNSAFE — requires `--allow-unsafe`)
- `editor_eval_expression` — runs a Godot Expression in the editor

**Runtime surface**

Introspection
- `runtime_get_tree` — live SceneTree with `max_depth`
- `runtime_get_node_property` / `runtime_set_node_property`
- `runtime_set_node_properties` — bulk
- `runtime_call_method`
- `runtime_get_performance` — FPS, frame time, physics time, node/draw counts
- `runtime_resolve_id`, `runtime_list_node_methods`, `runtime_list_node_properties`, `runtime_list_node_signals`, `runtime_list_node_groups`, `runtime_get_nodes_in_group`
- `runtime_find_nodes`

Observation
- `runtime_get_logs`, `runtime_screenshot`

Input
- `runtime_inject_action` — fire an InputMap action
- `runtime_set_paused`
- `runtime_step_frames` — advance N frames deterministically (async; multi-frame await)

Signals
- `runtime_connect_signal`, `runtime_disconnect_signal`, `runtime_emit_signal`

Groups
- `runtime_add_to_group`, `runtime_remove_from_group`

Animation
- `runtime_animation_list`, `runtime_animation_play`, `runtime_animation_stop`, `runtime_animation_seek`

Physics queries
- `runtime_physics_raycast_2d`, `runtime_physics_raycast_3d`
- `runtime_physics_overlap_point_2d`

Scene ops
- `runtime_instantiate_scene` — load `.tscn` and add at runtime
- `runtime_free_node` — `QueueFree`
- `runtime_change_scene` — `SceneTree.ChangeSceneToFile`

State experimentation
- `runtime_snapshot_state` / `runtime_restore_state` — "try, observe, undo" loop

Push-based property watches
- `runtime_watch_property` — server pushes `notifications/message` with `name: 'watch_changed'` each time the JSON value differs (and `watch_ended` when the node is freed)
- `runtime_unwatch_property`, `runtime_list_watches`

Eval (UNSAFE — requires `--allow-unsafe`)
- `runtime_eval_expression`

Mutation tools have `Mutates = true` in the registry and are rejected when the server is started with `--read-only`. Tools tagged `Unsafe = true` are additionally gated by `--allow-unsafe`.

### Stable instance ids

Every tool that returns a node also returns an `id` (Godot's instance id, as a numeric string). Every tool that takes a `path` also accepts `instance_id` as an alternative. Use ids when you need a reference that survives the user renaming or reparenting the node within a session — they fail cleanly if the node has been freed.

### Error hints

When a `node_not_found` error fires for a path or property or method or class, the error carries a `hints.did_you_mean` array of the 3 closest existing names. So a wrong NodePath usually self-corrects on the next call.

### Image content

Screenshot tools return an MCP `image` content block (base64 PNG with `mimeType`) plus a small structured echo with width/height/byte count. Clients that support image content (Claude included) see the actual image.

## Design notes worth knowing

**Threading.** All handlers run on Godot's main thread, marshaled through a per-adapter `MainThreadDispatcher` that drains its queue in `_Process`. Mutations therefore land between idle frames, never mid-physics-step. The TCP read loop runs on a background thread.

**JSON ↔ Variant.** `addon/godot_mcp/Shared/WireJson.cs` is best-effort: primitives, vectors, colors, arrays, dictionaries, packed arrays. Compound types accept either `{x,y,z}`-style objects or arrays. Object references serialize to a `{__type:"Object", class, id}` envelope but can't be rehydrated on the client.

**Addressing.** Nodes are addressed by `NodePath`. At edit time, paths are relative to the edited scene's root (the leading `/` is tolerated and stripped). At runtime, paths are absolute from the `SceneTree.Root` (e.g. `/root/Main/Player`).

**C# build cycle.** Creating a script and instantiating its new type in the same MCP call doesn't work — C# scripts need an assembly rebuild before the new class is visible. `editor_add_node` therefore only adds existing engine classes; a `build_project` action plus script-write tools are tracked as the next obvious additions.

**Read-only mode.** `--read-only` blocks any tool tagged `Mutates: true` at the server before it reaches the adapter. Useful when you want the AI to inspect freely without risk of touching the scene.

**Unsafe mode.** `--allow-unsafe` is a separate gate for tools tagged `Unsafe: true` (currently `editor_eval_expression` and `runtime_eval_expression`). Off by default because Expression eval has unbounded reach (any registered class, any method).

**Push notifications.** The server forwards adapter events to the MCP client as `notifications/message` (level=info, data.kind="godot.event"). `runtime_watch_property` uses this channel to push `watch_changed` / `watch_ended` events instead of forcing the client to poll.

**Async handlers.** Adapters can register async handlers (`Task<JsonNode?>`) that span multiple frames — used by `runtime_step_frames`, which awaits N `SceneTree.ProcessFrame` signals. The main-thread dispatcher routes both sync and async handlers; signals resume on the main thread because Godot fires them there.

**Structured build errors.** `editor_build_project` parses MSBuild output into `errors[]` / `warnings[]` arrays of `{file, line, column?, severity, code, message}`. File paths are normalized to `res://` when inside the project. Raw `stdout`/`stderr` remain in the response for cases the regex misses.

## Status

v0.3 — push notifications, watches, step_frames, signals/groups/animation/physics queries at runtime, input-map editing, project settings R/W, scene CRUD, structured build errors, play/stop scene, eval (unsafe). Compiles cleanly against Godot SDK 4.6.x; if you run into an API drift on a different patch release, please open an issue with the Godot version and the failing handler.
