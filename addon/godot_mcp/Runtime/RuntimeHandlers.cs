using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Shared;

namespace GodotMcp.Runtime;

internal static class RuntimeHandlers
{
    /// Snapshots stored in-process so the AI can do "try, observe, restore" loops.
    private static readonly ConcurrentDictionary<string, JsonObject> _snapshots = new();
    private static long _logOffset = 0;

    public static void Register(AdapterBase owner, Dictionary<string, AdapterHandler> h)
    {
        // Sync handlers added in v0.3: signals, groups, physics, animation, scene ops,
        // watches (push), eval. Async handlers register in RegisterAsync below.
        RuntimeHandlersExtra.RegisterSync(owner, h);
        // ── introspection ──────────────────────────────────────────────────
        h["runtime_get_tree"] = args => GetTree(owner, args);
        h["runtime_get_node_property"] = args => GetNodeProperty(owner, args);
        h["runtime_resolve_id"] = args => ResolveId(args);

        // ── listings ───────────────────────────────────────────────────────
        h["runtime_list_node_methods"] = args => ListNodeMethods(owner, args);
        h["runtime_list_node_properties"] = args => ListNodeProperties(owner, args);
        h["runtime_list_node_signals"] = args => ListNodeSignals(owner, args);
        h["runtime_list_node_groups"] = args => ListNodeGroups(owner, args);
        h["runtime_get_nodes_in_group"] = args => GetNodesInGroup(owner, args);

        // ── search ─────────────────────────────────────────────────────────
        h["runtime_find_nodes"] = args => FindNodes(owner, args);

        // ── observation ────────────────────────────────────────────────────
        h["runtime_get_logs"] = args => GetLogs(args);
        h["runtime_screenshot"] = args => ScreenshotGame(owner, args);
        h["runtime_get_performance"] = args => GetPerformance();

        // ── mutation ───────────────────────────────────────────────────────
        h["runtime_set_node_property"] = args => SetNodeProperty(owner, args);
        h["runtime_set_node_properties"] = args => SetNodeProperties(owner, args);
        h["runtime_call_method"] = args => CallMethod(owner, args);
        h["runtime_inject_action"] = args => InjectAction(args);
        h["runtime_set_paused"] = args => SetPaused(owner, args);

        // ── experimentation ────────────────────────────────────────────────
        h["runtime_snapshot_state"] = args => SnapshotState(owner, args);
        h["runtime_restore_state"] = args => RestoreState(owner, args);
    }

    public static void RegisterAsync(AdapterBase owner, Dictionary<string, AsyncAdapterHandler> h)
    {
        RuntimeHandlersExtra.RegisterAsync(owner, h);
    }

    // ════════════════════════════════════════════════════════════════════
    // INTROSPECTION
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? GetTree(Node owner, JsonObject args)
    {
        bool withProps = args["include_properties"]?.GetValue<bool>() ?? false;
        int maxDepth = args["max_depth"] is JsonValue mdv && mdv.TryGetValue<int>(out var md) ? md : -1;
        var root = owner.GetTree().Root;
        return BuildTree(root, withProps, maxDepth, 0);
    }

    private static JsonObject BuildTree(Node node, bool withProps, int maxDepth, int depth)
    {
        var obj = new JsonObject
        {
            ["path"] = node.GetPath().ToString(),
            ["name"] = node.Name.ToString(),
            ["type"] = node.GetClass(),
            ["id"] = InstanceIdString(node),
        };

        var script = node.GetScript();
        if (script.AsGodotObject() is Script s)
            obj["script"] = s.ResourcePath;

        if (withProps)
        {
            var props = new JsonObject();
            foreach (var d in node.GetPropertyList())
            {
                var usage = (long)d["usage"];
                if ((usage & (long)PropertyUsageFlags.Storage) == 0) continue;
                string pname = d["name"].AsString();
                try { props[pname] = WireJson.ToJson(node.Get(pname)); }
                catch { /* skip unreadable */ }
            }
            obj["properties"] = props;
        }

        var children = new JsonArray();
        if (maxDepth < 0 || depth < maxDepth)
        {
            foreach (var child in node.GetChildren())
                children.Add(BuildTree(child, withProps, maxDepth, depth + 1));
        }
        else if (node.GetChildCount() > 0)
        {
            obj["children_truncated"] = node.GetChildCount();
        }
        obj["children"] = children;
        return obj;
    }

    private static JsonNode? GetNodeProperty(Node owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        string property = ReqString(args, "property");
        if (!NodeHasProperty(node, property))
            throw NotFound("Property", property, NodePropertyNames(node));
        var value = node.Get(property);
        return new JsonObject
        {
            ["property"] = property,
            ["type"] = value.VariantType.ToString(),
            ["value"] = WireJson.ToJson(value),
        };
    }

    private static JsonNode? ResolveId(JsonObject args)
    {
        ulong id = ReqInstanceId(args);
        var inst = GodotObject.InstanceFromId(id);
        if (inst is null)
            throw new AdapterError("node_not_found", $"No live object with instance id {id}.");
        if (inst is not Node node)
            return new JsonObject { ["id"] = id.ToString(), ["class"] = inst.GetClass(), ["is_node"] = false };
        return new JsonObject
        {
            ["id"] = id.ToString(),
            ["class"] = node.GetClass(),
            ["name"] = node.Name.ToString(),
            ["is_node"] = true,
            ["path"] = node.GetPath().ToString(),
        };
    }

    // ════════════════════════════════════════════════════════════════════
    // LISTINGS
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? ListNodeMethods(Node owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        var arr = new JsonArray();
        bool internalToo = args["include_internal"]?.GetValue<bool>() ?? false;
        foreach (var d in node.GetMethodList())
        {
            string name = d["name"].AsString();
            if (!internalToo && name.StartsWith("_", StringComparison.Ordinal)) continue;
            arr.Add(name);
        }
        return new JsonObject { ["path"] = node.GetPath().ToString(), ["count"] = arr.Count, ["methods"] = arr };
    }

    private static JsonNode? ListNodeProperties(Node owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        var arr = new JsonArray();
        foreach (var d in node.GetPropertyList())
        {
            var usage = (long)d["usage"];
            if ((usage & (long)PropertyUsageFlags.Storage) == 0 && (usage & (long)PropertyUsageFlags.Editor) == 0) continue;
            arr.Add(new JsonObject
            {
                ["name"] = d["name"].AsString(),
                ["type"] = ((Variant.Type)(long)d["type"]).ToString(),
            });
        }
        return new JsonObject { ["path"] = node.GetPath().ToString(), ["count"] = arr.Count, ["properties"] = arr };
    }

    private static JsonNode? ListNodeSignals(Node owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        var arr = new JsonArray();
        foreach (var sig in node.GetSignalList())
            arr.Add(sig["name"].AsString());
        return new JsonObject { ["path"] = node.GetPath().ToString(), ["signals"] = arr };
    }

    private static JsonNode? ListNodeGroups(Node owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        var arr = new JsonArray();
        foreach (var g in node.GetGroups()) arr.Add(g.ToString());
        return new JsonObject { ["path"] = node.GetPath().ToString(), ["groups"] = arr };
    }

    private static JsonNode? GetNodesInGroup(Node owner, JsonObject args)
    {
        string group = ReqString(args, "group");
        var nodes = owner.GetTree().GetNodesInGroup(group);
        var arr = new JsonArray();
        foreach (var n in nodes)
        {
            arr.Add(new JsonObject
            {
                ["path"] = n.GetPath().ToString(),
                ["name"] = n.Name.ToString(),
                ["type"] = n.GetClass(),
                ["id"] = InstanceIdString(n),
            });
        }
        return new JsonObject { ["group"] = group, ["count"] = arr.Count, ["nodes"] = arr };
    }

    // ════════════════════════════════════════════════════════════════════
    // SEARCH
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? FindNodes(Node owner, JsonObject args)
    {
        string? typeFilter = args["type"]?.GetValue<string>();
        string? nameContains = args["name_contains"]?.GetValue<string>();
        string? inGroup = args["in_group"]?.GetValue<string>();
        int limit = args["limit"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 100;

        var matches = new JsonArray();
        Walk(owner.GetTree().Root, typeFilter, nameContains, inGroup, limit, matches);
        return new JsonObject { ["count"] = matches.Count, ["matches"] = matches };
    }

    private static void Walk(Node node, string? typeFilter, string? nameContains, string? inGroup, int limit, JsonArray matches)
    {
        if (matches.Count >= limit) return;
        bool ok = true;
        if (typeFilter is not null && !ClassDB.IsParentClass(node.GetClass(), typeFilter) && node.GetClass() != typeFilter) ok = false;
        if (ok && nameContains is not null && !node.Name.ToString().Contains(nameContains, StringComparison.OrdinalIgnoreCase)) ok = false;
        if (ok && inGroup is not null && !node.IsInGroup(inGroup)) ok = false;
        if (ok)
        {
            matches.Add(new JsonObject
            {
                ["path"] = node.GetPath().ToString(),
                ["name"] = node.Name.ToString(),
                ["type"] = node.GetClass(),
                ["id"] = InstanceIdString(node),
            });
        }
        foreach (var c in node.GetChildren())
        {
            if (matches.Count >= limit) break;
            Walk(c, typeFilter, nameContains, inGroup, limit, matches);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // OBSERVATION
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? GetLogs(JsonObject args)
    {
        bool sinceLastCall = args["since_last_call"]?.GetValue<bool>() ?? false;
        int tail = args["tail_lines"] is JsonValue tv && tv.TryGetValue<int>(out var t) ? t : 200;
        string? overridePath = args["path"]?.GetValue<string>();
        string path = string.IsNullOrEmpty(overridePath) ? LogTail.DefaultLogPath() : overridePath;

        if (sinceLastCall)
        {
            var (newLines, newOffset, p) = LogTail.ReadSince(path, _logOffset);
            _logOffset = newOffset;
            var arr = new JsonArray();
            foreach (var line in newLines) arr.Add(line);
            return new JsonObject { ["path"] = p, ["lines"] = arr, ["offset"] = newOffset.ToString() };
        }
        else
        {
            var (lines, total, p) = LogTail.ReadTail(path, tail);
            var arr = new JsonArray();
            foreach (var line in lines) arr.Add(line);
            _logOffset = total;
            return new JsonObject { ["path"] = p, ["lines"] = arr, ["total_bytes"] = total };
        }
    }

    private static JsonNode? ScreenshotGame(Node owner, JsonObject args)
    {
        int? maxSide = null;
        if (args["max_side"] is JsonValue mv && mv.TryGetValue<int>(out var ms)) maxSide = ms;
        var viewport = owner.GetTree().Root;
        return Screenshot.Capture(viewport, maxSide);
    }

    private static JsonNode? GetPerformance()
    {
        return new JsonObject
        {
            ["fps"] = Performance.GetMonitor(Performance.Monitor.TimeFps),
            ["process_ms"] = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0,
            ["physics_ms"] = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0,
            ["nodes"] = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
            ["objects"] = Performance.GetMonitor(Performance.Monitor.ObjectCount),
            ["draw_calls"] = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            ["mem_static_mb"] = Performance.GetMonitor(Performance.Monitor.MemoryStatic) / (1024.0 * 1024.0),
        };
    }

    // ════════════════════════════════════════════════════════════════════
    // MUTATION
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? SetNodeProperty(Node owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        string property = ReqString(args, "property");
        var raw = args["value"] ?? throw new AdapterError("invalid_args", "Missing 'value'.");
        ApplyPropertySet(node, property, raw);
        return new JsonObject { ["set"] = property };
    }

    private static JsonNode? SetNodeProperties(Node owner, JsonObject args)
    {
        var setsNode = args["sets"] as JsonArray
            ?? throw new AdapterError("invalid_args", "Missing array 'sets'.");
        var results = new JsonArray();
        int failed = 0;
        foreach (var item in setsNode)
        {
            if (item is not JsonObject obj) { failed++; results.Add(new JsonObject { ["ok"] = false, ["error"] = "Item is not an object." }); continue; }
            try
            {
                var node = ResolveNode(owner, obj);
                string property = ReqString(obj, "property");
                var raw = obj["value"] ?? throw new AdapterError("invalid_args", "Missing 'value'.");
                ApplyPropertySet(node, property, raw);
                results.Add(new JsonObject { ["ok"] = true, ["path"] = node.GetPath().ToString(), ["property"] = property });
            }
            catch (AdapterError e)
            {
                failed++;
                results.Add(new JsonObject { ["ok"] = false, ["error_code"] = e.Code, ["error"] = e.Message });
            }
        }
        return new JsonObject { ["count"] = results.Count, ["failed"] = failed, ["results"] = results };
    }

    private static JsonNode? CallMethod(Node owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        string method = ReqString(args, "method");
        if (!node.HasMethod(method))
            throw NotFound("Method", method, NodeMethodNames(node));

        var callArgs = new Godot.Collections.Array();
        if (args["args"] is JsonArray arr)
            foreach (var item in arr)
                callArgs.Add(WireJson.FromJson(item));

        var result = node.Callv(method, callArgs);
        return new JsonObject
        {
            ["return"] = WireJson.ToJson(result),
            ["type"] = result.VariantType.ToString(),
        };
    }

    private static JsonNode? InjectAction(JsonObject args)
    {
        string action = ReqString(args, "action");
        string mode = args["mode"]?.GetValue<string>() ?? "tap";
        float strength = 1f;
        if (args["strength"] is JsonValue jv && jv.TryGetValue<double>(out var d)) strength = (float)d;

        if (!InputMap.HasAction(action))
        {
            var known = new List<string>();
            foreach (var a in InputMap.GetActions()) known.Add(a.ToString());
            throw NotFound("Input action", action, known);
        }

        switch (mode)
        {
            case "press": Input.ActionPress(action, strength); break;
            case "release": Input.ActionRelease(action); break;
            case "tap":
            default:
                Input.ActionPress(action, strength);
                Callable.From(() => Input.ActionRelease(action)).CallDeferred();
                break;
        }
        return new JsonObject { ["action"] = action, ["mode"] = mode };
    }

    private static JsonNode? SetPaused(Node owner, JsonObject args)
    {
        bool paused = args["paused"]?.GetValue<bool>()
            ?? throw new AdapterError("invalid_args", "Missing 'paused' bool.");
        owner.GetTree().Paused = paused;
        return new JsonObject { ["paused"] = paused };
    }

    // ════════════════════════════════════════════════════════════════════
    // EXPERIMENTATION
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? SnapshotState(Node owner, JsonObject args)
    {
        string? rootPath = args["path"]?.GetValue<string>();
        var root = string.IsNullOrEmpty(rootPath)
            ? owner.GetTree().Root
            : ResolveNode(owner, args);

        var nodes = new JsonArray();
        SnapshotWalk(root, root, nodes);

        string id = Guid.NewGuid().ToString("N");
        var snap = new JsonObject
        {
            ["root_path"] = root.GetPath().ToString(),
            ["taken_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["nodes"] = nodes,
        };
        _snapshots[id] = snap;
        return new JsonObject
        {
            ["snapshot_id"] = id,
            ["root_path"] = root.GetPath().ToString(),
            ["node_count"] = nodes.Count,
        };
    }

    private static void SnapshotWalk(Node root, Node node, JsonArray sink)
    {
        var props = new JsonObject();
        foreach (var d in node.GetPropertyList())
        {
            var usage = (long)d["usage"];
            if ((usage & (long)PropertyUsageFlags.Storage) == 0) continue;
            string pname = d["name"].AsString();
            // Don't snapshot heavy resource references; they restore poorly.
            var type = (Variant.Type)(long)d["type"];
            if (type == Variant.Type.Object) continue;
            try { props[pname] = WireJson.ToJson(node.Get(pname)); }
            catch { }
        }
        sink.Add(new JsonObject
        {
            ["rel_path"] = root.GetPathTo(node).ToString(),
            ["id"] = InstanceIdString(node),
            ["properties"] = props,
        });
        foreach (var child in node.GetChildren())
            SnapshotWalk(root, child, sink);
    }

    private static JsonNode? RestoreState(Node owner, JsonObject args)
    {
        string id = ReqString(args, "snapshot_id");
        if (!_snapshots.TryGetValue(id, out var snap))
            throw new AdapterError("node_not_found", $"Snapshot '{id}' not found. Snapshots are kept only for this session and only on this adapter.");

        string rootPath = snap["root_path"]!.GetValue<string>();
        var root = owner.GetTree().Root.GetNodeOrNull(rootPath);
        if (root is null)
            throw new AdapterError("node_not_found", $"Snapshot's root '{rootPath}' is no longer in the SceneTree.");

        var nodes = snap["nodes"] as JsonArray
            ?? throw new AdapterError("internal_error", "Snapshot payload missing 'nodes'.");

        int restored = 0, skipped = 0;
        foreach (var nv in nodes)
        {
            if (nv is not JsonObject n) continue;
            string rel = n["rel_path"]!.GetValue<string>();
            var target = rel == "." ? root : root.GetNodeOrNull(rel);
            if (target is null) { skipped++; continue; }
            if (n["properties"] is not JsonObject props) { skipped++; continue; }
            foreach (var kv in props)
            {
                if (kv.Value is null) continue;
                var hint = PropertyHint(target, kv.Key);
                try { target.Set(kv.Key, WireJson.FromJson(kv.Value, hint)); restored++; }
                catch { skipped++; }
            }
        }
        return new JsonObject
        {
            ["snapshot_id"] = id,
            ["properties_restored"] = restored,
            ["properties_skipped"] = skipped,
        };
    }

    // ════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════

    private static Node ResolveNode(Node owner, JsonObject args)
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
        if (node is not null) return node;

        var existing = new List<string>();
        CollectPaths(root, existing, 500);
        throw new AdapterError("node_not_found", $"No live node at '{path}'.")
            .WithHint("did_you_mean", Suggestions.Nearest(path, existing));
    }

    private static void CollectPaths(Node node, List<string> sink, int limit)
    {
        if (sink.Count >= limit) return;
        sink.Add(node.GetPath().ToString());
        foreach (var c in node.GetChildren()) CollectPaths(c, sink, limit);
    }

    private static void ApplyPropertySet(Node node, string property, JsonNode value)
    {
        if (!NodeHasProperty(node, property))
            throw NotFound("Property", property, NodePropertyNames(node));
        var hint = PropertyHint(node, property);
        node.Set(property, WireJson.FromJson(value, hint));
    }

    private static bool NodeHasProperty(Node node, string property)
    {
        foreach (var d in node.GetPropertyList())
            if (d["name"].AsString() == property) return true;
        return false;
    }

    private static IEnumerable<string> NodePropertyNames(Node node)
    {
        foreach (var d in node.GetPropertyList())
        {
            var usage = (long)d["usage"];
            if ((usage & (long)PropertyUsageFlags.Storage) == 0 && (usage & (long)PropertyUsageFlags.Editor) == 0) continue;
            yield return d["name"].AsString();
        }
    }

    private static IEnumerable<string> NodeMethodNames(Node node)
    {
        foreach (var m in node.GetMethodList())
            yield return m["name"].AsString();
    }

    private static Variant.Type PropertyHint(Node node, string property)
    {
        foreach (var d in node.GetPropertyList())
        {
            if (d["name"].AsString() == property) return (Variant.Type)(long)d["type"];
        }
        return Variant.Type.Nil;
    }

    private static string ReqString(JsonObject args, string name)
    {
        var v = args[name]?.GetValue<string>();
        if (string.IsNullOrEmpty(v)) throw new AdapterError("invalid_args", $"Missing '{name}'.");
        return v;
    }

    private static ulong ReqInstanceId(JsonObject args)
    {
        var v = args["instance_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(v)) throw new AdapterError("invalid_args", "Missing 'instance_id'.");
        if (!ulong.TryParse(v, out var id)) throw new AdapterError("invalid_args", $"'instance_id' must be a numeric string, got '{v}'.");
        return id;
    }

    private static AdapterError NotFound(string what, string requested, IEnumerable<string> existing) =>
        new AdapterError("node_not_found", $"{what} '{requested}' not found.")
            .WithHint("did_you_mean", Suggestions.Nearest(requested, existing));

    private static string InstanceIdString(GodotObject obj) =>
        obj.GetInstanceId().ToString(CultureInfo.InvariantCulture);
}

