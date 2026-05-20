#if TOOLS
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Shared;

namespace GodotMcp.Editor;

internal static class EditorHandlers
{
    public static void Register(Dictionary<string, AdapterHandler> h)
    {
        h["editor_get_state"] = GetState;
        h["editor_get_scene_tree"] = GetSceneTree;
        h["editor_get_class_reference"] = GetClassReference;
        h["editor_add_node"] = AddNode;
        h["editor_remove_node"] = RemoveNode;
        h["editor_set_node_property"] = SetNodeProperty;
        h["editor_get_node_property"] = GetNodeProperty;
        h["editor_save_scene"] = SaveScene;
    }

    private static EditorInterface EI => EditorInterface.Singleton;

    // ─── state / introspection ────────────────────────────────────────────

    private static JsonNode? GetState(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot();
        var openScenes = new JsonArray();
        foreach (var p in EI.GetOpenScenes()) openScenes.Add(p);

        var selected = new JsonArray();
        foreach (var n in EI.GetSelection().GetSelectedNodes())
        {
            if (root is not null) selected.Add(root.GetPathTo(n).ToString());
            else selected.Add(n.GetPath().ToString());
        }

        return new JsonObject
        {
            ["godot_version"] = Engine.GetVersionInfo()["string"].AsString(),
            ["project_path"] = ProjectSettings.GlobalizePath("res://"),
            ["edited_scene_path"] = root?.SceneFilePath ?? "",
            ["edited_scene_root_type"] = root?.GetClass() ?? "",
            ["open_scene_paths"] = openScenes,
            ["selected_paths"] = selected,
        };
    }

    private static JsonNode? GetSceneTree(JsonObject args)
    {
        bool withProps = args["include_properties"]?.GetValue<bool>() ?? false;
        string? scenePath = args["scene_path"]?.GetValue<string>();

        // Switching the active edited scene from script isn't reliably exposed in 4.x;
        // if the caller wants a non-active scene, they need to open it in the editor first.
        if (!string.IsNullOrEmpty(scenePath))
        {
            bool found = false;
            foreach (var s in EI.GetOpenScenes()) { if (s == scenePath) { found = true; break; } }
            if (!found)
                throw new AdapterError("node_not_found", $"Scene '{scenePath}' is not open in the editor.");
        }

        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");

        return BuildTree(root, root, withProps);
    }

    private static JsonObject BuildTree(Node root, Node node, bool withProps)
    {
        var obj = new JsonObject
        {
            ["path"] = root.GetPathTo(node).ToString(),
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
                var dict = entry.AsGodotDictionary();
                var usage = (long)dict["usage"];
                if ((usage & (long)PropertyUsageFlags.Storage) == 0) continue;
                string pname = dict["name"].AsString();
                try { props[pname] = WireJson.ToJson(node.Get(pname)); }
                catch { /* skip unreadable */ }
            }
            obj["properties"] = props;
        }

        var children = new JsonArray();
        foreach (var child in node.GetChildren())
            children.Add(BuildTree(root, child, withProps));
        obj["children"] = children;
        return obj;
    }

    private static JsonNode? GetClassReference(JsonObject args)
    {
        string className = args["class_name"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'class_name'.");

        if (!ClassDB.ClassExists(className))
            throw new AdapterError("node_not_found", $"Class '{className}' is not registered.");

        var sn = new StringName(className);

        var methods = new JsonArray();
        foreach (var m in ClassDB.ClassGetMethodList(sn, noInheritance: false))
        {
            var d = m.AsGodotDictionary();
            var argsArr = new JsonArray();
            foreach (var p in d["args"].AsGodotArray())
            {
                var pd = p.AsGodotDictionary();
                argsArr.Add(new JsonObject
                {
                    ["name"] = pd["name"].AsString(),
                    ["type"] = ((Variant.Type)(long)pd["type"]).ToString(),
                });
            }
            methods.Add(new JsonObject
            {
                ["name"] = d["name"].AsString(),
                ["return_type"] = d.ContainsKey("return") ? ((Variant.Type)(long)d["return"].AsGodotDictionary()["type"]).ToString() : "Nil",
                ["args"] = argsArr,
            });
        }

        var properties = new JsonArray();
        foreach (var p in ClassDB.ClassGetPropertyList(sn, noInheritance: false))
        {
            var d = p.AsGodotDictionary();
            properties.Add(new JsonObject
            {
                ["name"] = d["name"].AsString(),
                ["type"] = ((Variant.Type)(long)d["type"]).ToString(),
            });
        }

        var signals = new JsonArray();
        foreach (var s in ClassDB.ClassGetSignalList(sn, noInheritance: false))
        {
            var d = s.AsGodotDictionary();
            signals.Add(new JsonObject { ["name"] = d["name"].AsString() });
        }

        return new JsonObject
        {
            ["class"] = className,
            ["parent"] = ClassDB.GetParentClass(sn).ToString(),
            ["instantiable"] = ClassDB.CanInstantiate(sn),
            ["methods"] = methods,
            ["properties"] = properties,
            ["signals"] = signals,
        };
    }

    // ─── mutation ─────────────────────────────────────────────────────────

    private static JsonNode? AddNode(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");

        string parentPath = args["parent_path"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'parent_path'.");
        string type = args["type"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'type'.");
        string? name = args["name"]?.GetValue<string>();

        Node parent = ResolveNode(root, parentPath);

        if (!ClassDB.ClassExists(type) || !ClassDB.CanInstantiate(type))
            throw new AdapterError("type_not_instantiable", $"Cannot instantiate '{type}'.");

        var inst = ClassDB.Instantiate(type);
        if (inst.AsGodotObject() is not Node node)
            throw new AdapterError("type_not_instantiable", $"'{type}' is not a Node.");

        node.Name = name ?? type;
        parent.AddChild(node);
        node.Owner = root;

        MarkSceneDirty();

        return new JsonObject
        {
            ["path"] = root.GetPathTo(node).ToString(),
            ["name"] = node.Name.ToString(),
            ["type"] = node.GetClass(),
        };
    }

    private static JsonNode? RemoveNode(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");
        string path = args["path"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'path'.");

        var node = ResolveNode(root, path);
        if (node == root)
            throw new AdapterError("invalid_args", "Cannot remove the scene root.");

        var parent = node.GetParent();
        parent?.RemoveChild(node);
        node.Free();
        MarkSceneDirty();
        return new JsonObject { ["removed"] = path };
    }

    private static JsonNode? SetNodeProperty(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");
        string path = args["path"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'path'.");
        string property = args["property"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'property'.");
        var raw = args["value"]
            ?? throw new AdapterError("invalid_args", "Missing 'value'.");

        var node = ResolveNode(root, path);
        var hint = PropertyHint(node, property);
        var value = WireJson.FromJson(raw, hint);
        node.Set(property, value);
        MarkSceneDirty();
        return new JsonObject { ["set"] = property };
    }

    private static JsonNode? GetNodeProperty(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");
        string path = args["path"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'path'.");
        string property = args["property"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Missing 'property'.");

        var node = ResolveNode(root, path);
        var value = node.Get(property);
        return new JsonObject
        {
            ["property"] = property,
            ["type"] = value.VariantType.ToString(),
            ["value"] = WireJson.ToJson(value),
        };
    }

    private static JsonNode? SaveScene(JsonObject args)
    {
        string? path = args["scene_path"]?.GetValue<string>();
        Error err = string.IsNullOrEmpty(path) ? EI.SaveScene() : EI.SaveSceneAs(path);
        if (err != Error.Ok)
            throw new AdapterError("internal_error", $"Save failed: {err}");
        return new JsonObject { ["saved"] = string.IsNullOrEmpty(path) ? (EI.GetEditedSceneRoot()?.SceneFilePath ?? "") : path };
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private static Node ResolveNode(Node root, string path)
    {
        if (path == "." || string.IsNullOrEmpty(path))
            return root;
        // At edit time the SceneTree's /root is not the scene root, so we treat
        // leading slashes as relative-from-scene-root.
        var trimmed = path.StartsWith("/", StringComparison.Ordinal) ? path.TrimStart('/') : path;
        var node = root.GetNodeOrNull(trimmed);
        if (node is null)
            throw new AdapterError("node_not_found", $"No node at '{path}' under scene root.");
        return node;
    }

    private static Variant.Type PropertyHint(Node node, string property)
    {
        foreach (var entry in node.GetPropertyList())
        {
            var d = entry.AsGodotDictionary();
            if (d["name"].AsString() == property)
                return (Variant.Type)(long)d["type"];
        }
        return Variant.Type.Nil;
    }

    private static void MarkSceneDirty()
    {
        // Editing a node from a tool script normally already marks the scene modified.
        // EditorInterface doesn't expose a clean MarkDirty, so we rely on Set/AddChild
        // having that side effect. If users find changes aren't picked up, calling
        // EI.GetEditorMainScreen().QueueRedraw() can be added here.
    }
}
#endif
