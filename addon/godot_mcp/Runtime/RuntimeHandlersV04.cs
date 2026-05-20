// TileMap is intentionally still supported alongside TileMapLayer; suppress
// the obsolete warning that comes with mentioning it.
#pragma warning disable CS0618

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Shared;

namespace GodotMcp.Runtime;

/// Runtime handlers added in v0.4: signal subscription (event push), raw input
/// simulation (mouse / keyboard / text), TileMap queries and edits, focus a Control.
internal static class RuntimeHandlersV04
{
    private sealed class Listener
    {
        public required string Id;
        public required ulong InstanceId;
        public required string Signal;
        public required Callable Callback;
        public required Callable ExitedCallback;
    }
    private static readonly ConcurrentDictionary<string, Listener> _listeners = new();

    public static void Register(AdapterBase owner, Dictionary<string, AdapterHandler> h)
    {
        // ── signal subscription ────────────────────────────────────────────
        h["runtime_listen_signal"] = args => ListenSignal(owner, args);
        h["runtime_unlisten_signal"] = args => UnlistenSignal(args);
        h["runtime_list_signal_listeners"] = args => ListSignalListeners(args);

        // ── raw input ──────────────────────────────────────────────────────
        h["runtime_mouse_move"] = args => MouseMove(args);
        h["runtime_mouse_button"] = args => MouseButton(owner, args);
        h["runtime_mouse_scroll"] = args => MouseScroll(owner, args);
        h["runtime_key"] = args => KeyEvent(args);
        h["runtime_text_input"] = args => TextInput(args);
        h["runtime_focus_control"] = args => FocusControl(owner, args);

        // ── tilemap ────────────────────────────────────────────────────────
        h["runtime_tilemap_get_cell"] = args => TileMapGetCell(owner, args);
        h["runtime_tilemap_set_cell"] = args => TileMapSetCell(owner, args);
        h["runtime_tilemap_get_used_cells"] = args => TileMapGetUsedCells(owner, args);
    }

    // ════════════════════════════════════════════════════════════════════
    // SIGNAL SUBSCRIPTION
    // ════════════════════════════════════════════════════════════════════
    // Connects to a Godot signal on a live node and forwards each fire as an
    // adapter "signal_fired" event (which the server turns into
    // notifications/message). Auto-cleans on the source's tree_exiting.

    private static JsonNode? ListenSignal(AdapterBase owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        string signal = ReqString(args, "signal");

        int argCount = SignalArgCount(node, signal);
        if (argCount < 0)
            throw new AdapterError("node_not_found", $"Signal '{signal}' does not exist on {node.GetClass()}.");

        string id = Guid.NewGuid().ToString("N");
        ulong instanceId = node.GetInstanceId();
        var cb = BuildSignalCallback(id, instanceId, signal, argCount, owner);
        var err = node.Connect(signal, cb);
        if (err != Error.Ok && err != Error.AlreadyExists)
            throw new AdapterError("internal_error", $"Connect failed: {err}");

        // Hook tree_exiting so we can announce the end and remove the entry
        // — the engine auto-disconnects when the source is freed, so this is
        // purely for client-visible bookkeeping.
        var exitedCb = Callable.From(() => OnSourceExited(id, owner));
        node.Connect(Node.SignalName.TreeExiting, exitedCb, (uint)GodotObject.ConnectFlags.OneShot);

        _listeners[id] = new Listener
        {
            Id = id,
            InstanceId = instanceId,
            Signal = signal,
            Callback = cb,
            ExitedCallback = exitedCb,
        };
        return new JsonObject
        {
            ["listener_id"] = id,
            ["path"] = node.GetPath().ToString(),
            ["signal"] = signal,
            ["arg_count"] = argCount,
        };
    }

    private static void OnSourceExited(string id, AdapterBase owner)
    {
        if (_listeners.TryRemove(id, out var l))
        {
            owner.EmitEvent("listen_ended", new JsonObject
            {
                ["listener_id"] = id,
                ["signal"] = l.Signal,
                ["reason"] = "source_tree_exiting",
            });
        }
    }

    private static Callable BuildSignalCallback(string id, ulong instanceId, string signal, int argCount, AdapterBase owner)
    {
        // One overload per arity; Godot supports up to 8 args on a signal.
        void Fire(Variant[] callArgs)
        {
            var jsonArgs = new JsonArray();
            foreach (var v in callArgs)
            {
                try { jsonArgs.Add(WireJson.ToJson(v)); }
                catch { jsonArgs.Add(null); }
            }
            owner.EmitEvent("signal_fired", new JsonObject
            {
                ["listener_id"] = id,
                ["instance_id"] = instanceId.ToString(CultureInfo.InvariantCulture),
                ["signal"] = signal,
                ["args"] = jsonArgs,
            });
        }
        return argCount switch
        {
            0 => Callable.From(() => Fire(Array.Empty<Variant>())),
            1 => Callable.From<Variant>(a => Fire(new[] { a })),
            2 => Callable.From<Variant, Variant>((a, b) => Fire(new[] { a, b })),
            3 => Callable.From<Variant, Variant, Variant>((a, b, c) => Fire(new[] { a, b, c })),
            4 => Callable.From<Variant, Variant, Variant, Variant>((a, b, c, d) => Fire(new[] { a, b, c, d })),
            5 => Callable.From<Variant, Variant, Variant, Variant, Variant>((a, b, c, d, e) => Fire(new[] { a, b, c, d, e })),
            6 => Callable.From<Variant, Variant, Variant, Variant, Variant, Variant>((a, b, c, d, e, f) => Fire(new[] { a, b, c, d, e, f })),
            7 => Callable.From<Variant, Variant, Variant, Variant, Variant, Variant, Variant>((a, b, c, d, e, f, g) => Fire(new[] { a, b, c, d, e, f, g })),
            _ => Callable.From<Variant, Variant, Variant, Variant, Variant, Variant, Variant, Variant>((a, b, c, d, e, f, g, hh) => Fire(new[] { a, b, c, d, e, f, g, hh })),
        };
    }

    private static int SignalArgCount(Node node, string signal)
    {
        foreach (var s in node.GetSignalList())
        {
            if (s["name"].AsString() != signal) continue;
            return s["args"].AsGodotArray().Count;
        }
        return -1;
    }

    private static JsonNode? UnlistenSignal(JsonObject args)
    {
        string id = ReqString(args, "listener_id");
        if (!_listeners.TryRemove(id, out var l))
            return new JsonObject { ["listener_id"] = id, ["removed"] = false, ["reason"] = "unknown id" };
        var inst = GodotObject.InstanceFromId(l.InstanceId);
        if (inst is Node n)
        {
            if (n.IsConnected(l.Signal, l.Callback)) n.Disconnect(l.Signal, l.Callback);
            if (n.IsConnected(Node.SignalName.TreeExiting, l.ExitedCallback)) n.Disconnect(Node.SignalName.TreeExiting, l.ExitedCallback);
        }
        return new JsonObject { ["listener_id"] = id, ["removed"] = true };
    }

    private static JsonNode? ListSignalListeners(JsonObject args)
    {
        var arr = new JsonArray();
        foreach (var (id, l) in _listeners)
        {
            var inst = GodotObject.InstanceFromId(l.InstanceId);
            arr.Add(new JsonObject
            {
                ["listener_id"] = id,
                ["instance_id"] = l.InstanceId.ToString(CultureInfo.InvariantCulture),
                ["signal"] = l.Signal,
                ["alive"] = inst is Node,
                ["path"] = (inst as Node)?.GetPath().ToString() ?? "",
            });
        }
        return new JsonObject { ["count"] = arr.Count, ["listeners"] = arr };
    }

    // ════════════════════════════════════════════════════════════════════
    // RAW INPUT
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? MouseMove(JsonObject args)
    {
        var pos = AsVector2(args["position"]
            ?? throw new AdapterError("invalid_args", "Missing 'position' {x,y}."));
        bool warp = args["warp_cursor"]?.GetValue<bool>() ?? false;
        var rel = args["relative"] is JsonNode rn ? AsVector2(rn) : Vector2.Zero;

        var ev = new InputEventMouseMotion
        {
            Position = pos,
            GlobalPosition = pos,
            Relative = rel,
        };
        if (warp) Input.WarpMouse(pos);
        Input.ParseInputEvent(ev);
        return new JsonObject { ["position"] = new JsonObject { ["x"] = pos.X, ["y"] = pos.Y }, ["warped"] = warp };
    }

    private static JsonNode? MouseButton(AdapterBase owner, JsonObject args)
    {
        int button = args["button"] is JsonValue bv && bv.TryGetValue<int>(out var b) ? b : (int)Godot.MouseButton.Left;
        string mode = args["mode"]?.GetValue<string>() ?? "click";
        Vector2 pos = args["position"] is JsonNode pn ? AsVector2(pn) : CurrentMousePosition(owner);

        void Fire(bool pressed)
        {
            var ev = new InputEventMouseButton
            {
                ButtonIndex = (Godot.MouseButton)button,
                Pressed = pressed,
                Position = pos,
                GlobalPosition = pos,
            };
            Input.ParseInputEvent(ev);
        }

        switch (mode)
        {
            case "press": Fire(true); break;
            case "release": Fire(false); break;
            case "click":
            default:
                Fire(true);
                Callable.From(() => Fire(false)).CallDeferred();
                break;
        }
        return new JsonObject { ["button"] = button, ["mode"] = mode, ["position"] = new JsonObject { ["x"] = pos.X, ["y"] = pos.Y } };
    }

    private static JsonNode? MouseScroll(AdapterBase owner, JsonObject args)
    {
        string direction = args["direction"]?.GetValue<string>() ?? "down";
        int amount = args["amount"] is JsonValue av && av.TryGetValue<int>(out var a) ? a : 1;
        Vector2 pos = args["position"] is JsonNode pn ? AsVector2(pn) : CurrentMousePosition(owner);

        Godot.MouseButton btn = direction switch
        {
            "up" => Godot.MouseButton.WheelUp,
            "down" => Godot.MouseButton.WheelDown,
            "left" => Godot.MouseButton.WheelLeft,
            "right" => Godot.MouseButton.WheelRight,
            _ => throw new AdapterError("invalid_args", "'direction' must be one of 'up','down','left','right'."),
        };
        for (int i = 0; i < Math.Max(1, amount); i++)
        {
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = btn, Pressed = true, Position = pos, GlobalPosition = pos });
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = btn, Pressed = false, Position = pos, GlobalPosition = pos });
        }
        return new JsonObject { ["direction"] = direction, ["amount"] = amount };
    }

    private static JsonNode? KeyEvent(JsonObject args)
    {
        string keyName = ReqString(args, "key");
        string mode = args["mode"]?.GetValue<string>() ?? "tap";
        var keycode = OS.FindKeycodeFromString(keyName);
        if (keycode == Key.None)
            throw new AdapterError("invalid_args", $"Unknown key '{keyName}'.");

        bool shift = args["shift"]?.GetValue<bool>() ?? false;
        bool ctrl = args["ctrl"]?.GetValue<bool>() ?? false;
        bool alt = args["alt"]?.GetValue<bool>() ?? false;
        bool meta = args["meta"]?.GetValue<bool>() ?? false;

        InputEventKey Make(bool pressed)
        {
            var ev = new InputEventKey
            {
                Keycode = keycode,
                PhysicalKeycode = keycode,
                Pressed = pressed,
                ShiftPressed = shift,
                CtrlPressed = ctrl,
                AltPressed = alt,
                MetaPressed = meta,
            };
            return ev;
        }
        switch (mode)
        {
            case "press": Input.ParseInputEvent(Make(true)); break;
            case "release": Input.ParseInputEvent(Make(false)); break;
            case "tap":
            default:
                Input.ParseInputEvent(Make(true));
                Callable.From(() => Input.ParseInputEvent(Make(false))).CallDeferred();
                break;
        }
        return new JsonObject { ["key"] = keyName, ["mode"] = mode };
    }

    private static JsonNode? TextInput(JsonObject args)
    {
        string text = ReqString(args, "text");
        int sent = 0;
        foreach (char ch in text)
        {
            var ev = new InputEventKey
            {
                Pressed = true,
                Unicode = ch,
            };
            // Best-effort: derive a keycode for letters/digits so InputMap rules
            // that match Keycode also see the press. Plain unicode is enough
            // for Control text input handling.
            var kc = OS.FindKeycodeFromString(ch.ToString());
            if (kc != Key.None) { ev.Keycode = kc; ev.PhysicalKeycode = kc; }
            Input.ParseInputEvent(ev);

            var rel = new InputEventKey { Pressed = false, Unicode = ch };
            if (kc != Key.None) { rel.Keycode = kc; rel.PhysicalKeycode = kc; }
            Input.ParseInputEvent(rel);
            sent++;
        }
        return new JsonObject { ["typed"] = sent };
    }

    private static JsonNode? FocusControl(AdapterBase owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        if (node is not Control control)
            throw new AdapterError("invalid_args", $"Node {node.GetPath()} is not a Control (class={node.GetClass()}).");
        control.GrabFocus();
        return new JsonObject { ["focused"] = control.GetPath().ToString(), ["has_focus"] = control.HasFocus() };
    }

    // ════════════════════════════════════════════════════════════════════
    // TILEMAP (runtime)
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? TileMapGetCell(AdapterBase owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        var coords = AsVector2I(args["coords"] ?? throw new AdapterError("invalid_args", "Missing 'coords'."));
        int layer = args["layer"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 0;

        int sourceId; Vector2I atlas; int alt;
        if (node is TileMapLayer tml)
        {
            sourceId = tml.GetCellSourceId(coords);
            atlas = tml.GetCellAtlasCoords(coords);
            alt = tml.GetCellAlternativeTile(coords);
        }
        else if (node is TileMap tm)
        {
            sourceId = tm.GetCellSourceId(layer, coords);
            atlas = tm.GetCellAtlasCoords(layer, coords);
            alt = tm.GetCellAlternativeTile(layer, coords);
        }
        else throw NotTileMap(node);

        return new JsonObject
        {
            ["coords"] = WireJson.ToJson(coords),
            ["source_id"] = sourceId,
            ["atlas_coords"] = WireJson.ToJson(atlas),
            ["alternative_tile"] = alt,
            ["empty"] = sourceId < 0,
        };
    }

    private static JsonNode? TileMapSetCell(AdapterBase owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        var coords = AsVector2I(args["coords"] ?? throw new AdapterError("invalid_args", "Missing 'coords'."));
        int sourceId = args["source_id"] is JsonValue sv && sv.TryGetValue<int>(out var s) ? s : -1;
        var atlas = args["atlas_coords"] is JsonNode ac ? AsVector2I(ac) : Vector2I.Zero;
        int alt = args["alternative_tile"] is JsonValue av && av.TryGetValue<int>(out var a) ? a : 0;
        int layer = args["layer"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 0;

        if (node is TileMapLayer tml) tml.SetCell(coords, sourceId, atlas, alt);
        else if (node is TileMap tm) tm.SetCell(layer, coords, sourceId, atlas, alt);
        else throw NotTileMap(node);

        return new JsonObject
        {
            ["coords"] = WireJson.ToJson(coords),
            ["source_id"] = sourceId,
            ["cleared"] = sourceId < 0,
        };
    }

    private static JsonNode? TileMapGetUsedCells(AdapterBase owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        int layer = args["layer"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 0;
        Godot.Collections.Array<Vector2I> cells;
        if (node is TileMapLayer tml) cells = tml.GetUsedCells();
        else if (node is TileMap tm) cells = tm.GetUsedCells(layer);
        else throw NotTileMap(node);

        var arr = new JsonArray();
        foreach (var c in cells) arr.Add(WireJson.ToJson(c));
        return new JsonObject { ["count"] = arr.Count, ["cells"] = arr };
    }

    private static AdapterError NotTileMap(Node node) =>
        new AdapterError("invalid_args", $"Node {node.GetPath()} is not a TileMap or TileMapLayer (class={node.GetClass()}).");

    // ════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════

    private static Node ResolveNode(AdapterBase owner, JsonObject args)
    {
        if (args["instance_id"] is JsonValue iv && iv.TryGetValue<string>(out var idStr) && ulong.TryParse(idStr, out var id))
        {
            var inst = GodotObject.InstanceFromId(id);
            if (inst is Node n) return n;
            throw new AdapterError("node_not_found", $"Instance {id} is not a live Node.");
        }
        string? path = args["path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(path))
            throw new AdapterError("invalid_args", "Provide 'path' or 'instance_id'.");
        var root = owner.GetTree().Root;
        var node = root.GetNodeOrNull(path);
        if (node is null && path.StartsWith("/root/", StringComparison.Ordinal))
            node = root.GetNodeOrNull(path.Substring("/root/".Length));
        if (node is null) throw new AdapterError("node_not_found", $"No live node at '{path}'.");
        return node;
    }

    private static Vector2 CurrentMousePosition(AdapterBase owner)
    {
        var vp = owner.GetViewport();
        return vp is not null ? vp.GetMousePosition() : Vector2.Zero;
    }

    private static Vector2 AsVector2(JsonNode n)
    {
        if (n is JsonObject o) return new Vector2(AsFloat(o["x"]), AsFloat(o["y"]));
        if (n is JsonArray a && a.Count >= 2) return new Vector2(AsFloat(a[0]), AsFloat(a[1]));
        throw new AdapterError("invalid_args", "Expected {x,y} or [x,y].");
    }

    private static Vector2I AsVector2I(JsonNode n)
    {
        if (n is JsonObject o) return new Vector2I(AsInt(o["x"]), AsInt(o["y"]));
        if (n is JsonArray a && a.Count >= 2) return new Vector2I(AsInt(a[0]), AsInt(a[1]));
        throw new AdapterError("invalid_args", "Expected {x,y} or [x,y] integer coordinates.");
    }

    private static float AsFloat(JsonNode? n)
    {
        if (n is null) return 0f;
        if (n.AsValue().TryGetValue<double>(out var d)) return (float)d;
        if (n.AsValue().TryGetValue<long>(out var l)) return l;
        return 0f;
    }

    private static int AsInt(JsonNode? n)
    {
        if (n is null) return 0;
        if (n.AsValue().TryGetValue<long>(out var l)) return (int)l;
        if (n.AsValue().TryGetValue<double>(out var d)) return (int)d;
        return 0;
    }

    private static string ReqString(JsonObject args, string name)
    {
        var v = args[name]?.GetValue<string>();
        if (string.IsNullOrEmpty(v)) throw new AdapterError("invalid_args", $"Missing '{name}'.");
        return v;
    }
}
