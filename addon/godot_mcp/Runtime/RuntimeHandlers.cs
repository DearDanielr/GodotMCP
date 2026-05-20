using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Shared;

namespace GodotMcp.Runtime;

internal static class RuntimeHandlers
{
    public static void Register(Node owner, Dictionary<string, AdapterHandler> h)
    {
        h["runtime_get_tree"] = args => GetTree(owner, args);
        h["runtime_get_node_property"] = args => GetNodeProperty(owner, args);
        h["runtime_set_node_property"] = args => SetNodeProperty(owner, args);
        h["runtime_call_method"] = args => CallMethod(owner, args);
        h["runtime_get_performance"] = args => GetPerformance();
        h["runtime_inject_action"] = args => InjectAction(args);
    }

    private static JsonNode? GetTree(Node owner, JsonObject args)
    {
        bool withProps = args["include_properties"]?.GetValue<bool>() ?? false;
        var sceneTree = owner.GetTree();
        var root = sceneTree.Root;
        return BuildTree(root, withProps);
    }

    private static JsonObject BuildTree(Node node, bool withProps)
    {
        var obj = new JsonObject
        {
            ["path"] = node.GetPath().ToString(),
            ["name"] = node.Name.ToString(),
            ["type"] = node.GetClass(),
        };

        var script = node.GetScript();
        if (script.AsGodotObject() is Script s)
            obj["script"] = s.ResourcePath;

        if (withProps)
        {
            var props = new JsonObject();
            foreach (var entry in node.GetPropertyList())
            {
                var d = entry.AsGodotDictionary();
                var usage = (long)d["usage"];
                if ((usage & (long)PropertyUsageFlags.Storage) == 0) continue;
                string pname = d["name"].AsString();
                try { props[pname] = WireJson.ToJson(node.Get(pname)); }
                catch { /* skip unreadable */ }
            }
            obj["properties"] = props;
        }

        var children = new JsonArray();
        foreach (var child in node.GetChildren())
            children.Add(BuildTree(child, withProps));
        obj["children"] = children;
        return obj;
    }

    private static JsonNode? GetNodeProperty(Node owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        string property = args["property"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'property'.");
        var value = node.Get(property);
        return new JsonObject
        {
            ["property"] = property,
            ["type"] = value.VariantType.ToString(),
            ["value"] = WireJson.ToJson(value),
        };
    }

    private static JsonNode? SetNodeProperty(Node owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        string property = args["property"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'property'.");
        var raw = args["value"] ?? throw new AdapterError("invalid_args", "Missing 'value'.");

        Variant.Type hint = Variant.Type.Nil;
        foreach (var entry in node.GetPropertyList())
        {
            var d = entry.AsGodotDictionary();
            if (d["name"].AsString() == property) { hint = (Variant.Type)(long)d["type"]; break; }
        }

        node.Set(property, WireJson.FromJson(raw, hint));
        return new JsonObject { ["set"] = property };
    }

    private static JsonNode? CallMethod(Node owner, JsonObject args)
    {
        var node = ResolveNode(owner, args);
        string method = args["method"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'method'.");

        if (!node.HasMethod(method))
            throw new AdapterError("method_not_found", $"Node has no method '{method}'.");

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

    private static JsonNode? InjectAction(JsonObject args)
    {
        string action = args["action"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'action'.");
        string mode = args["mode"]?.GetValue<string>() ?? "tap";
        float strength = 1f;
        if (args["strength"] is JsonValue jv && jv.TryGetValue<double>(out var d))
            strength = (float)d;

        if (!InputMap.HasAction(action))
            throw new AdapterError("invalid_args", $"Input action '{action}' is not defined in the project's InputMap.");

        switch (mode)
        {
            case "press":
                Input.ActionPress(action, strength);
                break;
            case "release":
                Input.ActionRelease(action);
                break;
            case "tap":
            default:
                Input.ActionPress(action, strength);
                Callable.From(() => Input.ActionRelease(action)).CallDeferred();
                break;
        }
        return new JsonObject { ["action"] = action, ["mode"] = mode };
    }

    private static Node ResolveNode(Node owner, JsonObject args)
    {
        string path = args["path"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'path'.");
        var node = owner.GetTree().Root.GetNodeOrNull(path);
        // GetNodeOrNull from /root expects path without the leading "/root" segment when
        // called on the root itself. Try both forms for robustness.
        if (node is null && path.StartsWith("/root/", System.StringComparison.Ordinal))
            node = owner.GetTree().Root.GetNodeOrNull(path.Substring("/root/".Length));
        if (node is null)
            throw new AdapterError("node_not_found", $"No live node at '{path}'.");
        return node;
    }
}
