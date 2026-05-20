using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using GodotMcp.Shared;

namespace GodotMcp.Runtime;

/// Runtime handlers added in v0.3+: step_frames, signals, groups, physics queries,
/// animation, watches (push), scene ops, eval. Owner is the runtime AdapterBase.
/// 'partial' required because we hold a nested partial Node type (WatchTicker).
internal static partial class RuntimeHandlersExtra
{
    private static readonly ConcurrentDictionary<string, Watch> _watches = new();
    private static WatchTicker? _ticker;

    public static void RegisterSync(AdapterBase owner, Dictionary<string, AdapterHandler> h)
    {
        // ── signals ────────────────────────────────────────────────────────
        h["runtime_connect_signal"] = args => ConnectSignal(owner, args);
        h["runtime_disconnect_signal"] = args => DisconnectSignal(owner, args);
        h["runtime_emit_signal"] = args => EmitSignal(owner, args);

        // ── groups ─────────────────────────────────────────────────────────
        h["runtime_add_to_group"] = args => AddToGroup(owner, args);
        h["runtime_remove_from_group"] = args => RemoveFromGroup(owner, args);

        // ── physics ────────────────────────────────────────────────────────
        h["runtime_physics_raycast_2d"] = args => PhysicsRaycast2D(owner, args);
        h["runtime_physics_raycast_3d"] = args => PhysicsRaycast3D(owner, args);
        h["runtime_physics_overlap_point_2d"] = args => PhysicsOverlapPoint2D(owner, args);

        // ── animation ──────────────────────────────────────────────────────
        h["runtime_animation_list"] = args => AnimationList(owner, args);
        h["runtime_animation_play"] = args => AnimationPlay(owner, args);
        h["runtime_animation_stop"] = args => AnimationStop(owner, args);
        h["runtime_animation_seek"] = args => AnimationSeek(owner, args);

        // ── scene ops ──────────────────────────────────────────────────────
        h["runtime_instantiate_scene"] = args => InstantiateScene(owner, args);
        h["runtime_free_node"] = args => FreeNode(owner, args);
        h["runtime_change_scene"] = args => ChangeScene(owner, args);

        // ── watches (push) ─────────────────────────────────────────────────
        h["runtime_watch_property"] = args => WatchProperty(owner, args);
        h["runtime_unwatch_property"] = args => UnwatchProperty(args);
        h["runtime_list_watches"] = args => ListWatches(args);

        // ── eval (unsafe) ──────────────────────────────────────────────────
        h["runtime_eval_expression"] = args => EvalExpression(owner, args);
    }

    public static void RegisterAsync(AdapterBase owner, Dictionary<string, AsyncAdapterHandler> h)
    {
        h["runtime_step_frames"] = args => StepFrames(owner, args);
    }

    // ════════════════════════════════════════════════════════════════════
    // SIGNALS
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? ConnectSignal(AdapterBase owner, JsonObject args)
    {
        var src = ResolveNode(owner, args, "from_path", "from_instance_id");
        var dst = ResolveNode(owner, args, "to_path", "to_instance_id");
        string signal = ReqString(args, "signal");
        string method = ReqString(args, "method");
        long flags = args["flags"] is JsonValue fv && fv.TryGetValue<long>(out var f) ? f : 0;

        if (!HasSignal(src, signal))
            throw new AdapterError("node_not_found", $"Signal '{signal}' does not exist on {src.GetClass()}.");
        if (!dst.HasMethod(method))
            throw new AdapterError("method_not_found", $"Method '{method}' does not exist on {dst.GetClass()}.");

        var cb = new Callable(dst, method);
        var err = src.Connect(signal, cb, (uint)flags);
        if (err != Error.Ok && err != Error.AlreadyExists)
            throw new AdapterError("internal_error", $"Connect failed: {err}");
        return new JsonObject
        {
            ["from"] = src.GetPath().ToString(),
            ["signal"] = signal,
            ["to"] = dst.GetPath().ToString(),
            ["method"] = method,
            ["already_connected"] = err == Error.AlreadyExists,
        };
    }

    private static JsonNode? DisconnectSignal(AdapterBase owner, JsonObject args)
    {
        var src = ResolveNode(owner, args, "from_path", "from_instance_id");
        var dst = ResolveNode(owner, args, "to_path", "to_instance_id");
        string signal = ReqString(args, "signal");
        string method = ReqString(args, "method");
        var cb = new Callable(dst, method);
        if (!src.IsConnected(signal, cb))
            return new JsonObject { ["disconnected"] = false, ["reason"] = "not connected" };
        src.Disconnect(signal, cb);
        return new JsonObject { ["disconnected"] = true };
    }

    private static JsonNode? EmitSignal(AdapterBase owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        string signal = ReqString(args, "signal");
        if (!HasSignal(node, signal))
            throw new AdapterError("node_not_found", $"Signal '{signal}' does not exist on {node.GetClass()}.");
        var raw = args["args"] as JsonArray ?? new JsonArray();
        var argsArray = new Variant[raw.Count];
        for (int i = 0; i < raw.Count; i++) argsArray[i] = WireJson.FromJson(raw[i]);
        node.EmitSignal(signal, argsArray);
        return new JsonObject { ["emitted"] = signal, ["arg_count"] = argsArray.Length };
    }

    private static bool HasSignal(Node node, string signal)
    {
        foreach (var s in node.GetSignalList())
            if (s["name"].AsString() == signal) return true;
        return false;
    }

    // ════════════════════════════════════════════════════════════════════
    // GROUPS
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? AddToGroup(AdapterBase owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        string group = ReqString(args, "group");
        bool persistent = args["persistent"]?.GetValue<bool>() ?? false;
        node.AddToGroup(group, persistent);
        return new JsonObject { ["added"] = group, ["path"] = node.GetPath().ToString() };
    }

    private static JsonNode? RemoveFromGroup(AdapterBase owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        string group = ReqString(args, "group");
        node.RemoveFromGroup(group);
        return new JsonObject { ["removed"] = group, ["path"] = node.GetPath().ToString() };
    }

    // ════════════════════════════════════════════════════════════════════
    // PHYSICS
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? PhysicsRaycast2D(AdapterBase owner, JsonObject args)
    {
        var world = owner.GetTree().Root.World2D
            ?? throw new AdapterError("internal_error", "No active World2D.");
        var space = PhysicsServer2D.SpaceGetDirectState(world.Space)
            ?? throw new AdapterError("internal_error", "World2D has no direct space state.");
        var from = AsVector2(args["from"]
            ?? throw new AdapterError("invalid_args", "Missing 'from'."));
        var to = AsVector2(args["to"]
            ?? throw new AdapterError("invalid_args", "Missing 'to'."));
        var query = PhysicsRayQueryParameters2D.Create(from, to);
        if (args["mask"] is JsonValue m && m.TryGetValue<long>(out var mask)) query.CollisionMask = (uint)mask;
        if (args["collide_with_areas"] is JsonValue ca && ca.TryGetValue<bool>(out var bca)) query.CollideWithAreas = bca;
        if (args["collide_with_bodies"] is JsonValue cb && cb.TryGetValue<bool>(out var bcb)) query.CollideWithBodies = bcb;
        var result = space.IntersectRay(query);
        return EncodeRaycastResult(result);
    }

    private static JsonNode? PhysicsRaycast3D(AdapterBase owner, JsonObject args)
    {
        var world = owner.GetTree().Root.World3D
            ?? throw new AdapterError("internal_error", "No active World3D.");
        var space = PhysicsServer3D.SpaceGetDirectState(world.Space)
            ?? throw new AdapterError("internal_error", "World3D has no direct space state.");
        var from = AsVector3(args["from"]
            ?? throw new AdapterError("invalid_args", "Missing 'from'."));
        var to = AsVector3(args["to"]
            ?? throw new AdapterError("invalid_args", "Missing 'to'."));
        var query = PhysicsRayQueryParameters3D.Create(from, to);
        if (args["mask"] is JsonValue m && m.TryGetValue<long>(out var mask)) query.CollisionMask = (uint)mask;
        if (args["collide_with_areas"] is JsonValue ca && ca.TryGetValue<bool>(out var bca)) query.CollideWithAreas = bca;
        if (args["collide_with_bodies"] is JsonValue cb && cb.TryGetValue<bool>(out var bcb)) query.CollideWithBodies = bcb;
        var result = space.IntersectRay(query);
        return EncodeRaycastResult(result);
    }

    private static JsonNode? EncodeRaycastResult(Godot.Collections.Dictionary result)
    {
        if (result is null || result.Count == 0)
            return new JsonObject { ["hit"] = false };
        var obj = new JsonObject { ["hit"] = true };
        foreach (var key in result.Keys)
        {
            string k = key.AsString();
            try { obj[k] = WireJson.ToJson(result[key]); }
            catch { }
        }
        // Add a 'collider_path' for convenience if collider is a Node.
        if (result.ContainsKey("collider") && result["collider"].AsGodotObject() is Node n && n.IsInsideTree())
        {
            obj["collider_path"] = n.GetPath().ToString();
            obj["collider_id"] = n.GetInstanceId().ToString(CultureInfo.InvariantCulture);
        }
        return obj;
    }

    private static JsonNode? PhysicsOverlapPoint2D(AdapterBase owner, JsonObject args)
    {
        var world = owner.GetTree().Root.World2D
            ?? throw new AdapterError("internal_error", "No active World2D.");
        var space = PhysicsServer2D.SpaceGetDirectState(world.Space)
            ?? throw new AdapterError("internal_error", "World2D has no direct space state.");
        var point = AsVector2(args["point"]
            ?? throw new AdapterError("invalid_args", "Missing 'point'."));
        var q = new PhysicsPointQueryParameters2D { Position = point };
        if (args["mask"] is JsonValue m && m.TryGetValue<long>(out var mask)) q.CollisionMask = (uint)mask;
        int max = args["max_results"] is JsonValue mr && mr.TryGetValue<int>(out var mri) ? mri : 32;
        var arr = space.IntersectPoint(q, max);
        var hits = new JsonArray();
        foreach (var d in arr)
        {
            var entry = new JsonObject();
            foreach (var k in d.Keys)
            {
                try { entry[k.AsString()] = WireJson.ToJson(d[k]); } catch { }
            }
            if (d.ContainsKey("collider") && d["collider"].AsGodotObject() is Node n && n.IsInsideTree())
            {
                entry["collider_path"] = n.GetPath().ToString();
                entry["collider_id"] = n.GetInstanceId().ToString(CultureInfo.InvariantCulture);
            }
            hits.Add(entry);
        }
        return new JsonObject { ["count"] = hits.Count, ["hits"] = hits };
    }

    private static Vector2 AsVector2(JsonNode n)
    {
        if (n is JsonObject o) return new Vector2((float)(o["x"]?.GetValue<double>() ?? 0), (float)(o["y"]?.GetValue<double>() ?? 0));
        if (n is JsonArray a && a.Count >= 2) return new Vector2((float)(a[0]?.GetValue<double>() ?? 0), (float)(a[1]?.GetValue<double>() ?? 0));
        throw new AdapterError("invalid_args", "Expected {x,y} or [x,y].");
    }

    private static Vector3 AsVector3(JsonNode n)
    {
        if (n is JsonObject o) return new Vector3((float)(o["x"]?.GetValue<double>() ?? 0), (float)(o["y"]?.GetValue<double>() ?? 0), (float)(o["z"]?.GetValue<double>() ?? 0));
        if (n is JsonArray a && a.Count >= 3) return new Vector3((float)(a[0]?.GetValue<double>() ?? 0), (float)(a[1]?.GetValue<double>() ?? 0), (float)(a[2]?.GetValue<double>() ?? 0));
        throw new AdapterError("invalid_args", "Expected {x,y,z} or [x,y,z].");
    }

    // ════════════════════════════════════════════════════════════════════
    // ANIMATION
    // ════════════════════════════════════════════════════════════════════

    private static AnimationPlayer ResolveAnimationPlayer(AdapterBase owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        if (node is AnimationPlayer ap) return ap;
        throw new AdapterError("invalid_args", $"Node {node.GetPath()} is not an AnimationPlayer (class={node.GetClass()}).");
    }

    private static JsonNode? AnimationList(AdapterBase owner, JsonObject args)
    {
        var ap = ResolveAnimationPlayer(owner, args);
        var arr = new JsonArray();
        foreach (var n in ap.GetAnimationList()) arr.Add(n.ToString());
        return new JsonObject
        {
            ["path"] = ap.GetPath().ToString(),
            ["current"] = ap.CurrentAnimation.ToString(),
            ["count"] = arr.Count,
            ["animations"] = arr,
        };
    }

    private static JsonNode? AnimationPlay(AdapterBase owner, JsonObject args)
    {
        var ap = ResolveAnimationPlayer(owner, args);
        string? name = args["animation"]?.GetValue<string>();
        double speed = args["speed"] is JsonValue sv && sv.TryGetValue<double>(out var s) ? s : 1.0;
        bool fromEnd = args["from_end"]?.GetValue<bool>() ?? false;
        if (string.IsNullOrEmpty(name)) ap.Play();
        else ap.Play(name, -1.0, (float)speed, fromEnd);
        return new JsonObject { ["playing"] = ap.CurrentAnimation.ToString(), ["speed"] = speed };
    }

    private static JsonNode? AnimationStop(AdapterBase owner, JsonObject args)
    {
        var ap = ResolveAnimationPlayer(owner, args);
        bool keepState = args["keep_state"]?.GetValue<bool>() ?? false;
        ap.Stop(keepState);
        return new JsonObject { ["stopped"] = true };
    }

    private static JsonNode? AnimationSeek(AdapterBase owner, JsonObject args)
    {
        var ap = ResolveAnimationPlayer(owner, args);
        double t = args["time"]?.GetValue<double>()
            ?? throw new AdapterError("invalid_args", "Missing 'time'.");
        bool update = args["update"]?.GetValue<bool>() ?? true;
        ap.Seek(t, update);
        return new JsonObject { ["seek_to"] = t, ["current_position"] = ap.CurrentAnimationPosition };
    }

    // ════════════════════════════════════════════════════════════════════
    // SCENE OPS
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? InstantiateScene(AdapterBase owner, JsonObject args)
    {
        string scenePath = ReqString(args, "scene_path");
        string parentPath = ReqString(args, "parent_path");
        string? name = args["name"]?.GetValue<string>();

        if (!ResourceLoader.Exists(scenePath))
            throw new AdapterError("node_not_found", $"Scene '{scenePath}' does not exist.");
        var res = ResourceLoader.Load(scenePath);
        if (res is not PackedScene packed)
            throw new AdapterError("invalid_args", $"Resource at '{scenePath}' is not a PackedScene.");
        var inst = packed.Instantiate();
        if (inst is null)
            throw new AdapterError("internal_error", $"Could not instantiate '{scenePath}'.");

        Node parent = owner.GetTree().Root.GetNodeOrNull(parentPath.TrimStart('/'))
            ?? owner.GetTree().Root.GetNodeOrNull(parentPath)
            ?? throw new AdapterError("node_not_found", $"No live node at '{parentPath}'.");

        if (!string.IsNullOrEmpty(name)) inst.Name = name;
        parent.AddChild(inst);
        return new JsonObject
        {
            ["path"] = inst.GetPath().ToString(),
            ["name"] = inst.Name.ToString(),
            ["type"] = inst.GetClass(),
            ["id"] = inst.GetInstanceId().ToString(CultureInfo.InvariantCulture),
            ["from"] = scenePath,
        };
    }

    private static JsonNode? FreeNode(AdapterBase owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        if (node == owner.GetTree().Root)
            throw new AdapterError("invalid_args", "Refusing to free SceneTree.Root.");
        string path = node.GetPath().ToString();
        node.QueueFree();
        return new JsonObject { ["queued_free"] = path };
    }

    private static JsonNode? ChangeScene(AdapterBase owner, JsonObject args)
    {
        string scenePath = ReqString(args, "scene_path");
        if (!ResourceLoader.Exists(scenePath))
            throw new AdapterError("node_not_found", $"Scene '{scenePath}' does not exist.");
        var err = owner.GetTree().ChangeSceneToFile(scenePath);
        if (err != Error.Ok)
            throw new AdapterError("internal_error", $"ChangeSceneToFile returned {err}.");
        return new JsonObject { ["change_queued"] = scenePath };
    }

    // ════════════════════════════════════════════════════════════════════
    // WATCHES (push-based)
    // ════════════════════════════════════════════════════════════════════
    // A watch polls a property on every _Process tick (via WatchTicker) and
    // emits a "watch_changed" event when the JSON-serialized value changes.

    private sealed class Watch
    {
        public required string Id;
        public required ulong InstanceId;
        public required string Property;
        public JsonNode? Last;
    }

    private partial class WatchTicker : Node
    {
        public AdapterBase OwnerAdapter = null!;
        public override void _Ready() { ProcessMode = ProcessModeEnum.Always; }
        public override void _Process(double delta)
        {
            foreach (var kv in _watches)
            {
                var w = kv.Value;
                var inst = GodotObject.InstanceFromId(w.InstanceId);
                if (inst is not Node n)
                {
                    _watches.TryRemove(kv.Key, out _);
                    OwnerAdapter.EmitEvent("watch_ended", new JsonObject
                    {
                        ["watch_id"] = w.Id,
                        ["reason"] = "node_freed",
                    });
                    continue;
                }
                Variant current;
                try { current = n.Get(w.Property); }
                catch { continue; }
                JsonNode? curJson;
                try { curJson = WireJson.ToJson(current); }
                catch { continue; }
                if (!JsonEqual(curJson, w.Last))
                {
                    w.Last = curJson;
                    OwnerAdapter.EmitEvent("watch_changed", new JsonObject
                    {
                        ["watch_id"] = w.Id,
                        ["path"] = n.GetPath().ToString(),
                        ["property"] = w.Property,
                        ["value"] = curJson?.DeepClone(),
                    });
                }
            }
        }
    }

    private static bool JsonEqual(JsonNode? a, JsonNode? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.ToJsonString() == b.ToJsonString();
    }

    private static JsonNode? WatchProperty(AdapterBase owner, JsonObject args)
    {
        EnsureTicker(owner);
        var node = ResolveNode(owner, args);
        string property = ReqString(args, "property");
        if (!HasProperty(node, property))
            throw new AdapterError("property_not_found", $"Property '{property}' not on {node.GetClass()}.");
        string id = Guid.NewGuid().ToString("N");
        JsonNode? initial;
        try { initial = WireJson.ToJson(node.Get(property)); } catch { initial = null; }
        var w = new Watch
        {
            Id = id,
            InstanceId = node.GetInstanceId(),
            Property = property,
            Last = initial,
        };
        _watches[id] = w;
        return new JsonObject
        {
            ["watch_id"] = id,
            ["path"] = node.GetPath().ToString(),
            ["property"] = property,
            ["initial"] = initial?.DeepClone(),
        };
    }

    private static JsonNode? UnwatchProperty(JsonObject args)
    {
        string id = ReqString(args, "watch_id");
        bool removed = _watches.TryRemove(id, out _);
        return new JsonObject { ["watch_id"] = id, ["removed"] = removed };
    }

    private static JsonNode? ListWatches(JsonObject args)
    {
        var arr = new JsonArray();
        foreach (var (id, w) in _watches)
        {
            var inst = GodotObject.InstanceFromId(w.InstanceId);
            arr.Add(new JsonObject
            {
                ["watch_id"] = id,
                ["instance_id"] = w.InstanceId.ToString(CultureInfo.InvariantCulture),
                ["property"] = w.Property,
                ["alive"] = inst is Node,
                ["path"] = (inst as Node)?.GetPath().ToString() ?? "",
            });
        }
        return new JsonObject { ["count"] = arr.Count, ["watches"] = arr };
    }

    private static void EnsureTicker(AdapterBase owner)
    {
        if (_ticker is not null && IsInstanceValid(_ticker)) return;
        _ticker = new WatchTicker { Name = "GodotMcpWatchTicker", OwnerAdapter = owner };
        owner.AddChild(_ticker);
    }

    private static bool IsInstanceValid(GodotObject obj)
    {
        return obj is not null && GodotObject.IsInstanceValid(obj);
    }

    // ════════════════════════════════════════════════════════════════════
    // EVAL (UNSAFE)
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? EvalExpression(AdapterBase owner, JsonObject args)
    {
        string source = ReqString(args, "expression");
        var inputNames = new List<string>();
        var inputValues = new List<Variant>();
        if (args["inputs"] is JsonObject inObj)
        {
            foreach (var kv in inObj)
            {
                inputNames.Add(kv.Key);
                inputValues.Add(WireJson.FromJson(kv.Value));
            }
        }
        // Default base instance: the SceneTree.Root, so expressions like
        // 'get_node("Main/Player").position' resolve.
        GodotObject? baseInst = owner.GetTree()?.Root;
        if (args["base_path"]?.GetValue<string>() is { } basePath && !string.IsNullOrEmpty(basePath))
        {
            baseInst = owner.GetTree().Root.GetNodeOrNull(basePath.TrimStart('/'));
            if (baseInst is null)
                throw new AdapterError("node_not_found", $"base_path '{basePath}' not found.");
        }

        var expr = new Expression();
        var parseErr = expr.Parse(source, inputNames.ToArray());
        if (parseErr != Error.Ok)
            throw new AdapterError("invalid_args", $"Parse failed: {expr.GetErrorText()}");
        var valuesArray = new Godot.Collections.Array();
        foreach (var v in inputValues) valuesArray.Add(v);
        var result = expr.Execute(valuesArray, baseInst);
        if (expr.HasExecuteFailed())
            throw new AdapterError("internal_error", $"Execute failed: {expr.GetErrorText()}");
        return new JsonObject
        {
            ["type"] = result.VariantType.ToString(),
            ["value"] = WireJson.ToJson(result),
        };
    }

    // ════════════════════════════════════════════════════════════════════
    // STEP FRAMES (async)
    // ════════════════════════════════════════════════════════════════════

    private static async Task<JsonNode?> StepFrames(AdapterBase owner, JsonObject args)
    {
        int frames = args["frames"] is JsonValue fv && fv.TryGetValue<int>(out var f) ? f : 1;
        if (frames < 1) frames = 1;
        if (frames > 240) frames = 240;
        bool unpauseFirst = args["unpause_first"]?.GetValue<bool>() ?? true;
        bool repauseAfter = args["repause_after"]?.GetValue<bool>() ?? true;

        var tree = owner.GetTree();
        bool wasPaused = tree.Paused;
        if (unpauseFirst) tree.Paused = false;

        for (int i = 0; i < frames; i++)
            await owner.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        if (repauseAfter) tree.Paused = true;
        return new JsonObject
        {
            ["stepped"] = frames,
            ["was_paused"] = wasPaused,
            ["now_paused"] = tree.Paused,
        };
    }

    // ════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════

    private static Node ResolveNode(AdapterBase owner, JsonObject args, string pathKey = "path", string idKey = "instance_id")
    {
        if (args[idKey] is JsonValue iv && iv.TryGetValue<string>(out var idStr) && ulong.TryParse(idStr, out var id))
        {
            var inst = GodotObject.InstanceFromId(id);
            if (inst is Node n) return n;
            throw new AdapterError("node_not_found", $"Instance {id} is not a live Node.");
        }
        string? path = args[pathKey]?.GetValue<string>();
        if (string.IsNullOrEmpty(path))
            throw new AdapterError("invalid_args", $"Provide '{pathKey}' or '{idKey}'.");
        var root = owner.GetTree().Root;
        var node = root.GetNodeOrNull(path);
        if (node is null && path.StartsWith("/root/", StringComparison.Ordinal))
            node = root.GetNodeOrNull(path.Substring("/root/".Length));
        if (node is null) throw new AdapterError("node_not_found", $"No live node at '{path}'.");
        return node;
    }

    private static bool HasProperty(Node node, string property)
    {
        foreach (var d in node.GetPropertyList())
            if (d["name"].AsString() == property) return true;
        return false;
    }

    private static string ReqString(JsonObject args, string name)
    {
        var v = args[name]?.GetValue<string>();
        if (string.IsNullOrEmpty(v)) throw new AdapterError("invalid_args", $"Missing '{name}'.");
        return v;
    }
}
