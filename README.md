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
| `--help` | — | Print usage |

Set `GODOT_MCP_PORT` in your shell before launching Godot if you've changed the server's port — the editor and runtime adapters read the same variable.

## Available tools (slice in v0.1)

This release ships a representative slice of the action catalog. The framework — surface routing, threading, JSON ↔ Variant, handler registration — is designed so adding more handlers is a small edit.

**Built-in**

- `mcp_status` — which adapters are connected, with Godot version and project path
- `mcp_list_tools_by_surface` — tool catalog grouped by surface

**Editor surface**

- `editor_get_state` — open scenes, edited scene, selected nodes
- `editor_get_scene_tree` — hierarchy of the edited scene, optionally with all properties
- `editor_get_class_reference` — `ClassDB` introspection: methods, properties, signals for any class
- `editor_add_node` — instantiate a node and add it under a parent (owner set to scene root so it saves)
- `editor_remove_node`
- `editor_set_node_property` / `editor_get_node_property` — typed property edits
- `editor_save_scene`

**Runtime surface**

- `runtime_get_tree` — live SceneTree (actual instantiated state, not the saved .tscn)
- `runtime_get_node_property` / `runtime_set_node_property`
- `runtime_call_method` — invoke any method on a live node
- `runtime_get_performance` — FPS, frame time, physics time, node/draw counts
- `runtime_inject_action` — fire a named InputMap action as if the user pressed it

Mutation tools have `Mutates = true` in the registry and are rejected when the server is started with `--read-only`.

## Design notes worth knowing

**Threading.** All handlers run on Godot's main thread, marshaled through a per-adapter `MainThreadDispatcher` that drains its queue in `_Process`. Mutations therefore land between idle frames, never mid-physics-step. The TCP read loop runs on a background thread.

**JSON ↔ Variant.** `addon/godot_mcp/Shared/WireJson.cs` is best-effort: primitives, vectors, colors, arrays, dictionaries, packed arrays. Compound types accept either `{x,y,z}`-style objects or arrays. Object references serialize to a `{__type:"Object", class, id}` envelope but can't be rehydrated on the client.

**Addressing.** Nodes are addressed by `NodePath`. At edit time, paths are relative to the edited scene's root (the leading `/` is tolerated and stripped). At runtime, paths are absolute from the `SceneTree.Root` (e.g. `/root/Main/Player`).

**C# build cycle.** Creating a script and instantiating its new type in the same MCP call doesn't work — C# scripts need an assembly rebuild before the new class is visible. `editor_add_node` therefore only adds existing engine classes; a `build_project` action plus script-write tools are tracked as the next obvious additions.

**Read-only mode.** `--read-only` blocks any tool tagged `Mutates: true` at the server before it reaches the adapter. Useful when you want the AI to inspect freely without risk of touching the scene.

## What's not in v0.1

The architecture supports them; they're future handlers, not future redesigns:

- script create/read/patch (with a C# build hook)
- signal connect/disconnect, group management
- animation track creation
- screenshot of editor viewport / running game
- input map editing
- physics raycast / overlap queries at runtime
- snapshot / restore of a subtree's state
- `eval_expression` (deliberately gated behind config when added)

## Status

v0.1 — the architecture and the listed tools. Not yet exercised against a real Godot 4.6 build in this repo's CI; if you run into an API drift, please open an issue with the Godot version and the failing handler.
