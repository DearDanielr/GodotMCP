#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using GodotMcp.Shared;

namespace GodotMcp.Editor;

internal static class EditorHandlers
{
    public static void Register(Dictionary<string, AdapterHandler> h)
    {
        // ── state / introspection ──────────────────────────────────────────
        h["editor_get_state"] = GetState;
        h["editor_get_scene_tree"] = GetSceneTree;
        h["editor_get_class_reference"] = GetClassReference;
        h["editor_resolve_id"] = ResolveId;

        // ── listings ───────────────────────────────────────────────────────
        h["editor_list_classes"] = ListClasses;
        h["editor_list_node_methods"] = ListNodeMethods;
        h["editor_list_node_properties"] = ListNodeProperties;
        h["editor_list_node_signals"] = ListNodeSignals;
        h["editor_list_node_groups"] = ListNodeGroups;
        h["editor_list_input_actions"] = ListInputActions;

        // ── search ─────────────────────────────────────────────────────────
        h["editor_find_nodes"] = FindNodes;
        h["editor_grep_project"] = GrepProject;

        // ── observation ────────────────────────────────────────────────────
        h["editor_get_logs"] = GetLogs;
        h["editor_screenshot_viewport"] = ScreenshotViewport;

        // ── mutation ───────────────────────────────────────────────────────
        h["editor_add_node"] = AddNode;
        h["editor_remove_node"] = RemoveNode;
        h["editor_set_node_property"] = SetNodeProperty;
        h["editor_set_node_properties"] = SetNodeProperties;
        h["editor_get_node_property"] = GetNodeProperty;
        h["editor_save_scene"] = SaveScene;
        h["editor_attach_script"] = AttachScript;

        // ── scripts / build ────────────────────────────────────────────────
        h["editor_read_script"] = ReadScript;
        h["editor_write_script"] = WriteScript;
        h["editor_patch_script"] = PatchScript;
        // editor_build_project is registered via RegisterAsync below — building
        // synchronously blocked the main-thread dispatcher for the full build
        // duration, which made every other tool call queue behind it.

        // ── extras (v0.3): play, settings, input-map, resources, eval ──────
        EditorHandlersExtra.Register(h);
        // ── v0.4: tree edits, filesystem, autoloads, deps, focus, tilemap ──
        EditorHandlersV04.Register(h);
    }

    public static void RegisterAsync(Dictionary<string, AsyncAdapterHandler> h)
    {
        // Long-running handlers go here so they don't pin the main-thread dispatcher.
        h["editor_build_project"] = BuildProjectAsync;
    }

    private static EditorInterface EI => EditorInterface.Singleton;
    private static long _logOffset = 0;

    // ════════════════════════════════════════════════════════════════════
    // STATE / INTROSPECTION
    // ════════════════════════════════════════════════════════════════════

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
            ["edited_scene_root_id"] = root is null ? null : InstanceIdString(root),
            ["open_scene_paths"] = openScenes,
            ["selected_paths"] = selected,
        };
    }

    private static JsonNode? GetSceneTree(JsonObject args)
    {
        bool withProps = args["include_properties"]?.GetValue<bool>() ?? false;
        int maxDepth = args["max_depth"] is JsonValue mdv && mdv.TryGetValue<int>(out var md) ? md : -1;

        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");

        return BuildTree(root, root, withProps, maxDepth, 0);
    }

    private static JsonObject BuildTree(Node root, Node node, bool withProps, int maxDepth, int depth)
    {
        var obj = new JsonObject
        {
            ["path"] = root.GetPathTo(node).ToString(),
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
            foreach (var dict in node.GetPropertyList())
            {
                var usage = (long)dict["usage"];
                if ((usage & (long)PropertyUsageFlags.Storage) == 0) continue;
                string pname = dict["name"].AsString();
                try { props[pname] = WireJson.ToJson(node.Get(pname)); }
                catch { /* skip unreadable */ }
            }
            obj["properties"] = props;
        }

        var children = new JsonArray();
        if (maxDepth < 0 || depth < maxDepth)
        {
            foreach (var child in node.GetChildren())
                children.Add(BuildTree(root, child, withProps, maxDepth, depth + 1));
        }
        else if (node.GetChildCount() > 0)
        {
            obj["children_truncated"] = node.GetChildCount();
        }
        obj["children"] = children;
        return obj;
    }

    private static JsonNode? GetClassReference(JsonObject args)
    {
        string className = ReqString(args, "class_name");
        if (!ClassDB.ClassExists(className))
            throw NotFound("Class", className, AllClassNames());

        var sn = new StringName(className);

        var methods = new JsonArray();
        foreach (var d in ClassDB.ClassGetMethodList(sn, noInheritance: false))
        {
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
        foreach (var d in ClassDB.ClassGetPropertyList(sn, noInheritance: false))
        {
            properties.Add(new JsonObject
            {
                ["name"] = d["name"].AsString(),
                ["type"] = ((Variant.Type)(long)d["type"]).ToString(),
            });
        }

        var signals = new JsonArray();
        foreach (var d in ClassDB.ClassGetSignalList(sn, noInheritance: false))
        {
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

    private static JsonNode? ResolveId(JsonObject args)
    {
        ulong id = ReqInstanceId(args);
        var inst = GodotObject.InstanceFromId(id);
        if (inst is null)
            throw new AdapterError("node_not_found", $"No live object with instance id {id}.");
        if (inst is not Node node)
            return new JsonObject { ["id"] = id.ToString(), ["class"] = inst.GetClass(), ["is_node"] = false };

        var root = EI.GetEditedSceneRoot();
        return new JsonObject
        {
            ["id"] = id.ToString(),
            ["class"] = node.GetClass(),
            ["name"] = node.Name.ToString(),
            ["is_node"] = true,
            ["path"] = root is not null && node.IsInsideTree() ? root.GetPathTo(node).ToString() : node.GetPath().ToString(),
        };
    }

    // ════════════════════════════════════════════════════════════════════
    // LISTINGS
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? ListClasses(JsonObject args)
    {
        string? inheritsFrom = args["inherits_from"]?.GetValue<string>();
        string? nameContains = args["name_contains"]?.GetValue<string>();
        bool concreteOnly = args["concrete_only"]?.GetValue<bool>() ?? true;

        var inheritsSn = string.IsNullOrEmpty(inheritsFrom) ? null : new StringName(inheritsFrom);

        var arr = new JsonArray();
        foreach (var cls in ClassDB.GetClassList())
        {
            string name = cls.ToString();
            if (concreteOnly && !ClassDB.CanInstantiate(cls)) continue;
            if (nameContains is not null && !name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (inheritsSn is not null && !ClassDB.IsParentClass(cls, inheritsSn)) continue;
            arr.Add(name);
        }
        return new JsonObject { ["count"] = arr.Count, ["classes"] = arr };
    }

    private static JsonNode? ListNodeMethods(JsonObject args)
    {
        var node = ResolveNode(args);
        var arr = new JsonArray();
        foreach (var d in node.GetMethodList())
        {
            string name = d["name"].AsString();
            if (name.StartsWith("_", StringComparison.Ordinal) && (args["include_internal"]?.GetValue<bool>() ?? false) == false)
                continue;
            arr.Add(name);
        }
        return new JsonObject { ["path"] = NodePathFor(node), ["count"] = arr.Count, ["methods"] = arr };
    }

    private static JsonNode? ListNodeProperties(JsonObject args)
    {
        var node = ResolveNode(args);
        var arr = new JsonArray();
        foreach (var d in node.GetPropertyList())
        {
            var usage = (long)d["usage"];
            if ((usage & (long)PropertyUsageFlags.Storage) == 0 &&
                (usage & (long)PropertyUsageFlags.Editor) == 0) continue;
            arr.Add(new JsonObject
            {
                ["name"] = d["name"].AsString(),
                ["type"] = ((Variant.Type)(long)d["type"]).ToString(),
            });
        }
        return new JsonObject { ["path"] = NodePathFor(node), ["count"] = arr.Count, ["properties"] = arr };
    }

    private static JsonNode? ListNodeSignals(JsonObject args)
    {
        var node = ResolveNode(args);
        var arr = new JsonArray();
        foreach (var d in node.GetSignalList())
        {
            arr.Add(d["name"].AsString());
        }
        return new JsonObject { ["path"] = NodePathFor(node), ["count"] = arr.Count, ["signals"] = arr };
    }

    private static JsonNode? ListNodeGroups(JsonObject args)
    {
        var node = ResolveNode(args);
        var arr = new JsonArray();
        foreach (var g in node.GetGroups()) arr.Add(g.ToString());
        return new JsonObject { ["path"] = NodePathFor(node), ["groups"] = arr };
    }

    private static JsonNode? ListInputActions(JsonObject args)
    {
        var arr = new JsonArray();
        foreach (var a in InputMap.GetActions())
        {
            var events = new JsonArray();
            foreach (var ev in InputMap.ActionGetEvents(a))
                events.Add(ev?.AsText() ?? "");
            arr.Add(new JsonObject
            {
                ["name"] = a.ToString(),
                ["deadzone"] = InputMap.ActionGetDeadzone(a),
                ["events"] = events,
            });
        }
        return new JsonObject { ["count"] = arr.Count, ["actions"] = arr };
    }

    // ════════════════════════════════════════════════════════════════════
    // SEARCH
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? FindNodes(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");

        string? typeFilter = args["type"]?.GetValue<string>();
        string? nameContains = args["name_contains"]?.GetValue<string>();
        string? inGroup = args["in_group"]?.GetValue<string>();
        int limit = args["limit"] is JsonValue lv && lv.TryGetValue<int>(out var lim) ? lim : 100;

        var matches = new JsonArray();
        WalkAndMatch(root, root, typeFilter, nameContains, inGroup, limit, matches);
        return new JsonObject { ["count"] = matches.Count, ["matches"] = matches };
    }

    private static void WalkAndMatch(Node root, Node node, string? typeFilter, string? nameContains, string? inGroup, int limit, JsonArray matches)
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
                ["path"] = root.GetPathTo(node).ToString(),
                ["name"] = node.Name.ToString(),
                ["type"] = node.GetClass(),
                ["id"] = InstanceIdString(node),
            });
        }

        foreach (var child in node.GetChildren())
        {
            if (matches.Count >= limit) break;
            WalkAndMatch(root, child, typeFilter, nameContains, inGroup, limit, matches);
        }
    }

    private static JsonNode? GrepProject(JsonObject args)
    {
        string pattern = ReqString(args, "pattern");
        string? globsArg = args["extensions"]?.GetValue<string>();
        bool isRegex = args["regex"]?.GetValue<bool>() ?? false;
        int maxHits = args["limit"] is JsonValue lv && lv.TryGetValue<int>(out var lim) ? lim : 200;

        // Default to script + scene/resource files.
        var exts = string.IsNullOrEmpty(globsArg)
            ? new[] { ".gd", ".cs", ".tscn", ".tres", ".gdshader" }
            : globsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Regex? rx = null;
        if (isRegex)
        {
            try { rx = new Regex(pattern, RegexOptions.Multiline); }
            catch (ArgumentException ex) { throw new AdapterError("invalid_args", $"Bad regex: {ex.Message}"); }
        }

        string projectRoot = ProjectSettings.GlobalizePath("res://");
        var hits = new JsonArray();
        foreach (var file in EnumerateProjectFiles(projectRoot, exts))
        {
            if (hits.Count >= maxHits) break;
            string content;
            try { content = File.ReadAllText(file); }
            catch { continue; }

            string[] lines = content.Split('\n');
            for (int i = 0; i < lines.Length && hits.Count < maxHits; i++)
            {
                bool match = rx is not null ? rx.IsMatch(lines[i]) : lines[i].Contains(pattern, StringComparison.Ordinal);
                if (!match) continue;
                hits.Add(new JsonObject
                {
                    ["file"] = "res://" + Path.GetRelativePath(projectRoot, file).Replace('\\', '/'),
                    ["line"] = i + 1,
                    ["text"] = lines[i].TrimEnd('\r'),
                });
            }
        }
        return new JsonObject { ["count"] = hits.Count, ["truncated"] = hits.Count >= maxHits, ["hits"] = hits };
    }

    private static IEnumerable<string> EnumerateProjectFiles(string root, string[] extensions)
    {
        // Manual walk so we can skip .godot/ and addons/ if asked. For v0.2 we
        // skip only .godot/.
        foreach (var dir in EnumerateDirs(root))
        {
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var f in files)
            {
                string ext = Path.GetExtension(f);
                bool ok = false;
                foreach (var e in extensions) if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                if (ok) yield return f;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirs(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string d = stack.Pop();
            yield return d;
            string[] subs;
            try { subs = Directory.GetDirectories(d); } catch { continue; }
            foreach (var sub in subs)
            {
                string name = Path.GetFileName(sub);
                if (name == ".godot" || name == ".import" || name == ".vs" || name == "obj" || name == "bin") continue;
                stack.Push(sub);
            }
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
            foreach (var l in newLines) arr.Add(l);
            return new JsonObject { ["path"] = p, ["lines"] = arr, ["offset"] = newOffset.ToString() };
        }
        else
        {
            var (lines, total, p) = LogTail.ReadTail(path, tail);
            var arr = new JsonArray();
            foreach (var l in lines) arr.Add(l);
            _logOffset = total;
            return new JsonObject { ["path"] = p, ["lines"] = arr, ["total_bytes"] = total };
        }
    }

    private static JsonNode? ScreenshotViewport(JsonObject args)
    {
        int? maxSide = null;
        if (args["max_side"] is JsonValue mv && mv.TryGetValue<int>(out var ms)) maxSide = ms;

        // The edited scene's SubViewport-equivalent in the 3D editor is hard to
        // reach from a tool script in 4.6. We capture the scene root's viewport,
        // which is what the editor renders for 2D scenes; for 3D it's the closest
        // accessible texture.
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");
        var viewport = root.GetViewport()
            ?? throw new AdapterError("internal_error", "Edited scene root has no viewport.");
        return Screenshot.Capture(viewport, maxSide);
    }

    // ════════════════════════════════════════════════════════════════════
    // MUTATION
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? AddNode(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");

        string parentPath = ReqString(args, "parent_path");
        string type = ReqString(args, "type");
        string? name = args["name"]?.GetValue<string>();

        Node parent = ResolveNodeUnder(root, parentPath);

        if (!ClassDB.ClassExists(type) || !ClassDB.CanInstantiate(type))
            throw NotFound("Instantiable class", type, AllInstantiableClassNames());

        var inst = ClassDB.Instantiate(type);
        if (inst.AsGodotObject() is not Node node)
            throw new AdapterError("type_not_instantiable", $"'{type}' is not a Node.");

        node.Name = name ?? type;

        var undo = EI.GetEditorUndoRedo();
        undo.CreateAction($"MCP add {node.Name} ({type})", customContext: root);
        undo.AddDoMethod(parent, Node.MethodName.AddChild, node);
        undo.AddDoProperty(node, Node.PropertyName.Owner, root);
        undo.AddDoReference(node);
        undo.AddUndoMethod(parent, Node.MethodName.RemoveChild, node);
        undo.CommitAction();

        return new JsonObject
        {
            ["path"] = root.GetPathTo(node).ToString(),
            ["name"] = node.Name.ToString(),
            ["type"] = node.GetClass(),
            ["id"] = InstanceIdString(node),
        };
    }

    private static JsonNode? RemoveNode(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");
        var node = ResolveNode(args);
        if (node == root)
            throw new AdapterError("invalid_args", "Cannot remove the scene root.");

        string path = root.GetPathTo(node).ToString();
        var parent = node.GetParent()
            ?? throw new AdapterError("invalid_args", "Node has no parent.");
        int index = node.GetIndex();
        var owner = node.Owner;

        var undo = EI.GetEditorUndoRedo();
        undo.CreateAction($"MCP remove {node.Name}", customContext: root);
        undo.AddDoMethod(parent, Node.MethodName.RemoveChild, node);
        undo.AddUndoMethod(parent, Node.MethodName.AddChild, node);
        undo.AddUndoMethod(parent, Node.MethodName.MoveChild, node, index);
        undo.AddUndoProperty(node, Node.PropertyName.Owner, owner ?? root);
        undo.AddUndoReference(node);
        undo.CommitAction();

        return new JsonObject { ["removed"] = path };
    }

    private static JsonNode? SetNodeProperty(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");
        var node = ResolveNode(args);
        string property = ReqString(args, "property");
        var raw = args["value"] ?? throw new AdapterError("invalid_args", "Missing 'value'.");
        if (!NodeHasProperty(node, property))
            throw NotFound("Property", property, NodePropertyNames(node));

        var hint = PropertyHint(node, property);
        var newValue = WireJson.FromJson(raw, hint);
        var oldValue = node.Get(property);

        var undo = EI.GetEditorUndoRedo();
        undo.CreateAction($"MCP set {property} on {node.Name}", customContext: root);
        undo.AddDoProperty(node, property, newValue);
        undo.AddUndoProperty(node, property, oldValue);
        undo.CommitAction();
        return new JsonObject { ["set"] = property };
    }

    private static JsonNode? SetNodeProperties(JsonObject args)
    {
        var setsNode = args["sets"] as JsonArray
            ?? throw new AdapterError("invalid_args", "Missing array 'sets'.");
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");

        // Pre-resolve each item so we can build one undo action covering the whole batch.
        var planned = new List<(Node node, string property, Variant oldValue, Variant newValue)>();
        var results = new JsonArray();
        int failed = 0;
        foreach (var item in setsNode)
        {
            if (item is not JsonObject obj) { failed++; results.Add(new JsonObject { ["ok"] = false, ["error"] = "Item is not an object." }); continue; }
            try
            {
                var node = ResolveNode(obj);
                string property = ReqString(obj, "property");
                var raw = obj["value"] ?? throw new AdapterError("invalid_args", "Missing 'value'.");
                if (!NodeHasProperty(node, property))
                    throw NotFound("Property", property, NodePropertyNames(node));
                var hint = PropertyHint(node, property);
                var newValue = WireJson.FromJson(raw, hint);
                var oldValue = node.Get(property);
                planned.Add((node, property, oldValue, newValue));
                results.Add(new JsonObject { ["ok"] = true, ["path"] = NodePathFor(node), ["property"] = property });
            }
            catch (AdapterError e)
            {
                failed++;
                results.Add(new JsonObject { ["ok"] = false, ["error_code"] = e.Code, ["error"] = e.Message });
            }
        }

        if (planned.Count > 0)
        {
            var undo = EI.GetEditorUndoRedo();
            undo.CreateAction($"MCP set {planned.Count} properties", customContext: root);
            foreach (var (n, p, oldV, newV) in planned)
            {
                undo.AddDoProperty(n, p, newV);
                undo.AddUndoProperty(n, p, oldV);
            }
            undo.CommitAction();
        }
        return new JsonObject { ["count"] = results.Count, ["failed"] = failed, ["results"] = results };
    }

    private static JsonNode? GetNodeProperty(JsonObject args)
    {
        var node = ResolveNode(args);
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

    private static JsonNode? SaveScene(JsonObject args)
    {
        string? path = args["scene_path"]?.GetValue<string>();
        // EditorInterface.SaveScene / SaveSceneAs return void in 4.6; we rely on
        // a subsequent file check rather than an Error return to detect failure.
        if (string.IsNullOrEmpty(path)) EI.SaveScene();
        else EI.SaveSceneAs(path);
        return new JsonObject { ["saved"] = string.IsNullOrEmpty(path) ? (EI.GetEditedSceneRoot()?.SceneFilePath ?? "") : path };
    }

    private static JsonNode? AttachScript(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");
        var node = ResolveNode(args);
        string scriptPath = ReqString(args, "script_path");
        if (!ResourceLoader.Exists(scriptPath))
            throw new AdapterError("node_not_found", $"Script '{scriptPath}' does not exist.");
        var res = ResourceLoader.Load(scriptPath);
        if (res is not Script script)
            throw new AdapterError("invalid_args", $"Resource at '{scriptPath}' is not a Script.");
        var oldScript = node.GetScript();

        var undo = EI.GetEditorUndoRedo();
        undo.CreateAction($"MCP attach script to {node.Name}", customContext: root);
        undo.AddDoMethod(node, GodotObject.MethodName.SetScript, script);
        undo.AddUndoMethod(node, GodotObject.MethodName.SetScript, oldScript);
        undo.CommitAction();
        return new JsonObject { ["attached"] = scriptPath, ["path"] = NodePathFor(node) };
    }

    // ════════════════════════════════════════════════════════════════════
    // SCRIPTS / BUILD
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? ReadScript(JsonObject args)
    {
        string resPath = ReqString(args, "path");
        EnsureScriptPath(resPath);
        string global = ProjectSettings.GlobalizePath(resPath);
        if (!File.Exists(global))
            throw new AdapterError("node_not_found", $"Script '{resPath}' does not exist on disk.");
        string content = File.ReadAllText(global);
        return new JsonObject
        {
            ["path"] = resPath,
            ["bytes"] = content.Length,
            ["content"] = content,
        };
    }

    private static JsonNode? WriteScript(JsonObject args)
    {
        string resPath = ReqString(args, "path");
        string content = ReqString(args, "content");
        EnsureScriptPath(resPath);

        string global = ProjectSettings.GlobalizePath(resPath);
        var dir = Path.GetDirectoryName(global);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(global, content);
        EI.GetResourceFilesystem().Scan();
        return new JsonObject { ["wrote"] = resPath, ["bytes"] = content.Length };
    }

    private static JsonNode? PatchScript(JsonObject args)
    {
        string resPath = ReqString(args, "path");
        EnsureScriptPath(resPath);
        var edits = args["edits"] as JsonArray
            ?? throw new AdapterError("invalid_args", "Missing array 'edits' of {old, new} pairs.");

        string global = ProjectSettings.GlobalizePath(resPath);
        if (!File.Exists(global))
            throw new AdapterError("node_not_found", $"Script '{resPath}' does not exist on disk.");
        string content = File.ReadAllText(global);

        var applied = new JsonArray();
        int n = 0;
        foreach (var raw in edits)
        {
            if (raw is not JsonObject edit) throw new AdapterError("invalid_args", $"Edit #{n} is not an object.");
            string oldText = edit["old"]?.GetValue<string>() ?? throw new AdapterError("invalid_args", $"Edit #{n} missing 'old'.");
            string newText = edit["new"]?.GetValue<string>() ?? throw new AdapterError("invalid_args", $"Edit #{n} missing 'new'.");
            int idx = content.IndexOf(oldText, StringComparison.Ordinal);
            if (idx < 0) throw new AdapterError("invalid_args", $"Edit #{n}: 'old' anchor not found.");
            int second = content.IndexOf(oldText, idx + 1, StringComparison.Ordinal);
            if (second >= 0) throw new AdapterError("invalid_args", $"Edit #{n}: 'old' anchor matches multiple times — add more surrounding context to make it unique.");
            content = content.Substring(0, idx) + newText + content.Substring(idx + oldText.Length);
            applied.Add(new JsonObject { ["edit"] = n, ["at"] = idx });
            n++;
        }

        File.WriteAllText(global, content);
        EI.GetResourceFilesystem().Scan();
        return new JsonObject { ["patched"] = resPath, ["edits_applied"] = applied, ["bytes"] = content.Length };
    }

    /// Async so the dispatcher's main-thread queue is not blocked for the full
    /// build duration — other tool calls (status, get_state, etc.) keep working
    /// while dotnet is running. The build process itself is spawned out-of-process
    /// from Godot, so its stdio reads stay on background threads.
    ///
    /// Note about hot-reload: when the build writes a new GodotMcpProject.dll
    /// the editor's C# integration will hot-reload the assembly shortly after.
    /// That tears down the current adapter instance (this code!) and a fresh one
    /// is created. Pending tool calls in flight at that exact moment may be
    /// abandoned; the MCP client should poll mcp_status until it reconnects.
    /// We do NOT call EditorInterface.GetResourceFilesystem().Scan() anymore —
    /// the filesystem watcher already triggers a scan on its own, and forcing
    /// a second one was making the reload more disruptive in practice.
    private static async Task<JsonNode?> BuildProjectAsync(JsonObject args)
    {
        string projectRoot = ProjectSettings.GlobalizePath("res://");
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo("dotnet", "build")
        {
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process proc;
        try { proc = Process.Start(psi) ?? throw new AdapterError("build_failed", "dotnet build failed to start."); }
        catch (Exception ex) { throw new AdapterError("build_failed", $"Could not run 'dotnet build': {ex.Message}. Is the .NET SDK on PATH?"); }

        // Bounded wait — same 110s ceiling as before, but async so the dispatcher stays free.
        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(110));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new AdapterError("build_failed", "dotnet build timed out after 110s; the process was killed. If the editor holds the assembly locked, close the editor and build from a terminal, or use the editor's built-in Build button.");
        }

        string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        sw.Stop();

        bool ok = proc.ExitCode == 0;

        // Parse MSBuild diagnostics into structured {file, line, severity, message}
        // entries so the AI doesn't have to grep stdout.
        var diagnostics = EditorHandlersExtra.ParseBuildOutput(stdout + "\n" + stderr, projectRoot);
        var errors = new JsonArray();
        var warnings = new JsonArray();
        foreach (var d in diagnostics)
        {
            if (d is not JsonObject dObj) continue;
            string sev = dObj["severity"]?.GetValue<string>() ?? "";
            if (sev == "error") errors.Add(dObj.DeepClone());
            else if (sev == "warning") warnings.Add(dObj.DeepClone());
        }

        return new JsonObject
        {
            ["ok"] = ok,
            ["exit_code"] = proc.ExitCode,
            ["elapsed_ms"] = sw.ElapsedMilliseconds,
            ["error_count"] = errors.Count,
            ["warning_count"] = warnings.Count,
            ["errors"] = errors,
            ["warnings"] = warnings,
            ["stdout"] = stdout,
            ["stderr"] = stderr,
            // Heads-up so the client knows to poll mcp_status briefly after this.
            ["reload_warning"] = ok
                ? "Build succeeded. Godot's C# hot-reload may briefly disconnect the adapter; poll mcp_status until it reconnects before issuing further calls."
                : null,
        };
    }

    // ════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════

    private static Node ResolveNode(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");

        if (args["instance_id"] is JsonValue iv && iv.TryGetValue<string>(out var idStr) && ulong.TryParse(idStr, out var id))
        {
            var inst = GodotObject.InstanceFromId(id);
            if (inst is Node n) return n;
            throw new AdapterError("node_not_found", $"Instance {id} is not a live Node.");
        }

        string? path = args["path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(path))
            throw new AdapterError("invalid_args", "Provide 'path' or 'instance_id'.");
        return ResolveNodeUnder(root, path);
    }

    private static Node ResolveNodeUnder(Node root, string path)
    {
        if (path == "." || string.IsNullOrEmpty(path)) return root;
        var trimmed = path.StartsWith("/", StringComparison.Ordinal) ? path.TrimStart('/') : path;
        var node = root.GetNodeOrNull(trimmed);
        if (node is not null) return node;

        // Helpful miss: suggest the closest paths that exist.
        var existing = new List<string>();
        CollectPaths(root, root, existing, 500);
        throw new AdapterError("node_not_found", $"No node at '{path}' under scene root.")
            .WithHint("did_you_mean", Suggestions.Nearest(path, existing));
    }

    private static void CollectPaths(Node root, Node node, List<string> sink, int limit)
    {
        if (sink.Count >= limit) return;
        sink.Add(root.GetPathTo(node).ToString());
        foreach (var c in node.GetChildren()) CollectPaths(root, c, sink, limit);
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

    private static Variant.Type PropertyHint(Node node, string property)
    {
        foreach (var d in node.GetPropertyList())
        {
            if (d["name"].AsString() == property) return (Variant.Type)(long)d["type"];
        }
        return Variant.Type.Nil;
    }

    private static IEnumerable<string> AllClassNames()
    {
        foreach (var c in ClassDB.GetClassList()) yield return c.ToString();
    }

    private static IEnumerable<string> AllInstantiableClassNames()
    {
        foreach (var c in ClassDB.GetClassList())
            if (ClassDB.CanInstantiate(c)) yield return c.ToString();
    }

    private static string NodePathFor(Node node)
    {
        var root = EI.GetEditedSceneRoot();
        return root is not null && node.IsInsideTree() ? root.GetPathTo(node).ToString() : node.GetPath().ToString();
    }

    private static string InstanceIdString(GodotObject obj) =>
        obj.GetInstanceId().ToString(System.Globalization.CultureInfo.InvariantCulture);

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

    private static void EnsureScriptPath(string resPath)
    {
        if (!resPath.StartsWith("res://", StringComparison.Ordinal))
            throw new AdapterError("invalid_args", "Path must start with 'res://'.");
        string ext = Path.GetExtension(resPath).ToLowerInvariant();
        if (ext is not (".gd" or ".cs" or ".gdshader"))
            throw new AdapterError("invalid_args", $"Refusing to write '{ext}' file via editor_write_script; only .gd, .cs, .gdshader are allowed.");
    }
}

#endif
