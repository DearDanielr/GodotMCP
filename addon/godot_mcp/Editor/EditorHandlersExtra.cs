#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Godot;
using GodotMcp.Shared;

namespace GodotMcp.Editor;

/// Extra editor handlers added in v0.3+: play/stop scene, structured build errors,
/// project settings R/W, input-map editing, scene/resource CRUD, file listing, eval.
internal static class EditorHandlersExtra
{
    public static void Register(Dictionary<string, AdapterHandler> h)
    {
        // ── play / stop ────────────────────────────────────────────────────
        h["editor_play_scene"] = PlayScene;
        h["editor_stop_play"] = StopPlay;
        h["editor_is_playing"] = IsPlaying;

        // ── scene open ─────────────────────────────────────────────────────
        h["editor_open_scene"] = OpenScene;
        h["editor_create_scene"] = CreateScene;
        h["editor_instantiate_scene"] = InstantiateScene;

        // ── project settings ───────────────────────────────────────────────
        h["editor_get_project_setting"] = GetProjectSetting;
        h["editor_set_project_setting"] = SetProjectSetting;
        h["editor_list_project_settings"] = ListProjectSettings;
        h["editor_save_project_settings"] = SaveProjectSettings;

        // ── input map editing ──────────────────────────────────────────────
        h["editor_add_input_action"] = AddInputAction;
        h["editor_remove_input_action"] = RemoveInputAction;
        h["editor_bind_input_event"] = BindInputEvent;
        h["editor_unbind_input_events"] = UnbindInputEvents;

        // ── resources & files ──────────────────────────────────────────────
        h["editor_read_resource"] = ReadResource;
        h["editor_write_resource"] = WriteResource;
        h["editor_list_files"] = ListFiles;
        h["editor_reload_scripts"] = ReloadScripts;

        // ── eval (unsafe) ──────────────────────────────────────────────────
        h["editor_eval_expression"] = EvalExpression;
    }

    private static EditorInterface EI => EditorInterface.Singleton;

    // ════════════════════════════════════════════════════════════════════
    // PLAY / STOP
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? PlayScene(JsonObject args)
    {
        string? scenePath = args["scene_path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(scenePath))
        {
            // Play the currently edited scene, or main scene if none.
            var root = EI.GetEditedSceneRoot();
            if (root is not null && !string.IsNullOrEmpty(root.SceneFilePath))
            {
                EI.PlayCurrentScene();
                return new JsonObject { ["playing"] = root.SceneFilePath };
            }
            EI.PlayMainScene();
            return new JsonObject { ["playing"] = "main" };
        }
        if (!ResourceLoader.Exists(scenePath))
            throw new AdapterError("node_not_found", $"Scene '{scenePath}' does not exist.");
        EI.PlayCustomScene(scenePath);
        return new JsonObject { ["playing"] = scenePath };
    }

    private static JsonNode? StopPlay(JsonObject args)
    {
        EI.StopPlayingScene();
        return new JsonObject { ["stopped"] = true };
    }

    private static JsonNode? IsPlaying(JsonObject args)
    {
        return new JsonObject { ["playing"] = EI.IsPlayingScene(), ["scene"] = EI.GetPlayingScene() };
    }

    // ════════════════════════════════════════════════════════════════════
    // SCENE OPS
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? OpenScene(JsonObject args)
    {
        string path = ReqString(args, "path");
        if (!ResourceLoader.Exists(path))
            throw new AdapterError("node_not_found", $"Scene '{path}' does not exist.");
        EI.OpenSceneFromPath(path);
        return new JsonObject { ["opened"] = path };
    }

    private static JsonNode? CreateScene(JsonObject args)
    {
        // Pack the currently edited scene root (or 'path' subtree) into a new .tscn.
        string outPath = ReqString(args, "out_path");
        if (!outPath.EndsWith(".tscn", StringComparison.Ordinal))
            throw new AdapterError("invalid_args", "out_path must end in .tscn.");

        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");

        Node target = root;
        string? subPath = args["path"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(subPath) && subPath != ".")
        {
            var trimmed = subPath.StartsWith("/", StringComparison.Ordinal) ? subPath.TrimStart('/') : subPath;
            target = root.GetNodeOrNull(trimmed)
                ?? throw new AdapterError("node_not_found", $"No node at '{subPath}'.");
        }

        var packed = new PackedScene();
        var err = packed.Pack(target);
        if (err != Error.Ok)
            throw new AdapterError("internal_error", $"Pack failed: {err}");

        string global = ProjectSettings.GlobalizePath(outPath);
        var dir = Path.GetDirectoryName(global);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        err = ResourceSaver.Save(packed, outPath);
        if (err != Error.Ok)
            throw new AdapterError("internal_error", $"Save failed: {err}");
        EI.GetResourceFilesystem().Scan();
        return new JsonObject { ["created"] = outPath, ["from"] = subPath ?? "." };
    }

    private static JsonNode? InstantiateScene(JsonObject args)
    {
        var root = EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");
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

        Node parent;
        if (parentPath == "." || parentPath == "") parent = root;
        else parent = root.GetNodeOrNull(parentPath.TrimStart('/'))
            ?? throw new AdapterError("node_not_found", $"No parent at '{parentPath}'.");

        if (!string.IsNullOrEmpty(name)) inst.Name = name;

        var undo = EI.GetEditorUndoRedo();
        undo.CreateAction($"MCP instantiate {scenePath}", customContext: root);
        undo.AddDoMethod(parent, Node.MethodName.AddChild, inst);
        undo.AddDoProperty(inst, Node.PropertyName.Owner, root);
        undo.AddDoReference(inst);
        undo.AddUndoMethod(parent, Node.MethodName.RemoveChild, inst);
        undo.CommitAction();
        return new JsonObject
        {
            ["path"] = root.GetPathTo(inst).ToString(),
            ["name"] = inst.Name.ToString(),
            ["type"] = inst.GetClass(),
            ["id"] = inst.GetInstanceId().ToString(CultureInfo.InvariantCulture),
            ["from"] = scenePath,
        };
    }

    // ════════════════════════════════════════════════════════════════════
    // PROJECT SETTINGS
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? GetProjectSetting(JsonObject args)
    {
        string key = ReqString(args, "key");
        if (!ProjectSettings.HasSetting(key))
            throw new AdapterError("node_not_found", $"Project setting '{key}' is not defined.");
        var v = ProjectSettings.GetSetting(key);
        return new JsonObject
        {
            ["key"] = key,
            ["type"] = v.VariantType.ToString(),
            ["value"] = WireJson.ToJson(v),
        };
    }

    private static JsonNode? SetProjectSetting(JsonObject args)
    {
        string key = ReqString(args, "key");
        var raw = args["value"] ?? throw new AdapterError("invalid_args", "Missing 'value'.");
        bool persist = args["persist"]?.GetValue<bool>() ?? false;
        ProjectSettings.SetSetting(key, WireJson.FromJson(raw));
        if (persist)
        {
            var err = ProjectSettings.Save();
            if (err != Error.Ok) throw new AdapterError("internal_error", $"ProjectSettings.Save returned {err}.");
        }
        return new JsonObject { ["set"] = key, ["persisted"] = persist };
    }

    private static JsonNode? ListProjectSettings(JsonObject args)
    {
        string? prefix = args["prefix"]?.GetValue<string>();
        // ProjectSettings is exposed as a static façade in C#; reach the
        // underlying singleton via Engine to enumerate properties.
        var singleton = Engine.GetSingleton("ProjectSettings");
        if (singleton is null)
            throw new AdapterError("internal_error", "Could not access ProjectSettings singleton.");
        var arr = new JsonArray();
        foreach (var d in singleton.GetPropertyList())
        {
            string name = d["name"].AsString();
            if (!string.IsNullOrEmpty(prefix) && !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            arr.Add(new JsonObject
            {
                ["name"] = name,
                ["type"] = ((Variant.Type)(long)d["type"]).ToString(),
            });
        }
        return new JsonObject { ["count"] = arr.Count, ["settings"] = arr };
    }

    private static JsonNode? SaveProjectSettings(JsonObject args)
    {
        var err = ProjectSettings.Save();
        if (err != Error.Ok) throw new AdapterError("internal_error", $"ProjectSettings.Save returned {err}.");
        return new JsonObject { ["saved"] = true };
    }

    // ════════════════════════════════════════════════════════════════════
    // INPUT MAP EDITING
    // ════════════════════════════════════════════════════════════════════
    // We write through ProjectSettings (key "input/<action>") because that's
    // where Godot persists the InputMap. Updates also reflect in the live
    // InputMap so they take effect immediately.

    private static JsonNode? AddInputAction(JsonObject args)
    {
        string action = ReqString(args, "action");
        float deadzone = 0.5f;
        if (args["deadzone"] is JsonValue dv && dv.TryGetValue<double>(out var d)) deadzone = (float)d;

        if (InputMap.HasAction(action))
            throw new AdapterError("invalid_args", $"Action '{action}' already exists.");

        InputMap.AddAction(action, deadzone);
        // Persist into ProjectSettings.
        var dict = new Godot.Collections.Dictionary
        {
            ["deadzone"] = deadzone,
            ["events"] = new Godot.Collections.Array(),
        };
        ProjectSettings.SetSetting($"input/{action}", dict);
        return new JsonObject { ["action"] = action, ["deadzone"] = deadzone };
    }

    private static JsonNode? RemoveInputAction(JsonObject args)
    {
        string action = ReqString(args, "action");
        if (!InputMap.HasAction(action))
            throw new AdapterError("node_not_found", $"Action '{action}' is not defined.");
        InputMap.EraseAction(action);
        ProjectSettings.Clear($"input/{action}");
        return new JsonObject { ["removed"] = action };
    }

    private static JsonNode? BindInputEvent(JsonObject args)
    {
        string action = ReqString(args, "action");
        if (!InputMap.HasAction(action))
            throw new AdapterError("node_not_found", $"Action '{action}' is not defined.");

        var ev = ParseInputEvent(args["event"] as JsonObject
            ?? throw new AdapterError("invalid_args", "Missing 'event' object."));
        InputMap.ActionAddEvent(action, ev);
        SyncActionToSettings(action);
        return new JsonObject { ["action"] = action, ["bound"] = ev.AsText() };
    }

    private static JsonNode? UnbindInputEvents(JsonObject args)
    {
        string action = ReqString(args, "action");
        if (!InputMap.HasAction(action))
            throw new AdapterError("node_not_found", $"Action '{action}' is not defined.");
        InputMap.ActionEraseEvents(action);
        SyncActionToSettings(action);
        return new JsonObject { ["action"] = action, ["events_cleared"] = true };
    }

    private static InputEvent ParseInputEvent(JsonObject ev)
    {
        string kind = ev["kind"]?.GetValue<string>()
            ?? throw new AdapterError("invalid_args", "Event 'kind' is required (one of 'key', 'mouse_button', 'joy_button', 'joy_axis').");

        switch (kind)
        {
            case "key":
            {
                var k = new InputEventKey();
                string? keyName = ev["key"]?.GetValue<string>();
                if (string.IsNullOrEmpty(keyName))
                    throw new AdapterError("invalid_args", "Key event needs 'key' (e.g. 'space', 'a', 'escape').");
                var sym = OS.FindKeycodeFromString(keyName);
                if (sym == Key.None) throw new AdapterError("invalid_args", $"Unknown key '{keyName}'.");
                k.PhysicalKeycode = sym;
                k.Pressed = true;
                return k;
            }
            case "mouse_button":
            {
                var m = new InputEventMouseButton();
                long btn = ev["button"]?.GetValue<long>() ?? (long)MouseButton.Left;
                m.ButtonIndex = (MouseButton)btn;
                m.Pressed = true;
                return m;
            }
            case "joy_button":
            {
                var j = new InputEventJoypadButton();
                j.ButtonIndex = (JoyButton)(ev["button"]?.GetValue<long>() ?? 0);
                j.Pressed = true;
                return j;
            }
            case "joy_axis":
            {
                var j = new InputEventJoypadMotion();
                j.Axis = (JoyAxis)(ev["axis"]?.GetValue<long>() ?? 0);
                j.AxisValue = (float)(ev["value"]?.GetValue<double>() ?? 1.0);
                return j;
            }
            default:
                throw new AdapterError("invalid_args", $"Unknown event kind '{kind}'.");
        }
    }

    private static void SyncActionToSettings(string action)
    {
        var events = new Godot.Collections.Array();
        foreach (var e in InputMap.ActionGetEvents(action)) events.Add(e);
        var dict = new Godot.Collections.Dictionary
        {
            ["deadzone"] = InputMap.ActionGetDeadzone(action),
            ["events"] = events,
        };
        ProjectSettings.SetSetting($"input/{action}", dict);
    }

    // ════════════════════════════════════════════════════════════════════
    // RESOURCES & FILES
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? ReadResource(JsonObject args)
    {
        string path = ReqString(args, "path");
        if (!path.StartsWith("res://", StringComparison.Ordinal))
            throw new AdapterError("invalid_args", "Path must start with 'res://'.");
        if (!ResourceLoader.Exists(path))
            throw new AdapterError("node_not_found", $"Resource '{path}' does not exist.");
        var res = ResourceLoader.Load(path);
        if (res is null)
            throw new AdapterError("internal_error", $"Could not load resource '{path}'.");

        var props = new JsonObject();
        foreach (var d in res.GetPropertyList())
        {
            var usage = (long)d["usage"];
            if ((usage & (long)PropertyUsageFlags.Storage) == 0) continue;
            string pname = d["name"].AsString();
            try { props[pname] = WireJson.ToJson(res.Get(pname)); }
            catch { /* skip unreadable */ }
        }
        return new JsonObject
        {
            ["path"] = path,
            ["class"] = res.GetClass(),
            ["properties"] = props,
        };
    }

    private static JsonNode? WriteResource(JsonObject args)
    {
        string path = ReqString(args, "path");
        if (!path.StartsWith("res://", StringComparison.Ordinal))
            throw new AdapterError("invalid_args", "Path must start with 'res://'.");
        bool create = args["create"]?.GetValue<bool>() ?? false;

        Resource res;
        if (ResourceLoader.Exists(path) && !create)
        {
            res = ResourceLoader.Load(path)
                ?? throw new AdapterError("internal_error", $"Could not load existing resource at '{path}'.");
        }
        else if (create)
        {
            string typeName = ReqString(args, "type");
            if (!ClassDB.ClassExists(typeName) || !ClassDB.CanInstantiate(typeName))
                throw new AdapterError("invalid_args", $"Cannot instantiate resource type '{typeName}'.");
            var inst = ClassDB.Instantiate(typeName);
            if (inst.AsGodotObject() is not Resource r)
                throw new AdapterError("invalid_args", $"'{typeName}' is not a Resource.");
            res = r;
        }
        else
        {
            throw new AdapterError("node_not_found", $"Resource '{path}' does not exist. Set 'create: true' and 'type' to create it.");
        }

        var props = args["properties"] as JsonObject;
        if (props is not null)
        {
            foreach (var kv in props)
            {
                if (kv.Value is null) continue;
                var hint = ResourcePropertyType(res, kv.Key);
                try { res.Set(kv.Key, WireJson.FromJson(kv.Value, hint)); }
                catch (Exception ex) { throw new AdapterError("invalid_args", $"Setting '{kv.Key}': {ex.Message}"); }
            }
        }
        var err = ResourceSaver.Save(res, path);
        if (err != Error.Ok) throw new AdapterError("internal_error", $"Save returned {err}.");
        EI.GetResourceFilesystem().Scan();
        return new JsonObject { ["wrote"] = path, ["class"] = res.GetClass() };
    }

    private static Variant.Type ResourcePropertyType(Resource res, string name)
    {
        foreach (var d in res.GetPropertyList())
        {
            if (d["name"].AsString() == name) return (Variant.Type)(long)d["type"];
        }
        return Variant.Type.Nil;
    }

    private static JsonNode? ListFiles(JsonObject args)
    {
        string dirArg = args["dir"]?.GetValue<string>() ?? "res://";
        if (!dirArg.StartsWith("res://", StringComparison.Ordinal))
            throw new AdapterError("invalid_args", "'dir' must start with 'res://'.");
        bool recursive = args["recursive"]?.GetValue<bool>() ?? false;
        string? extFilter = args["extension"]?.GetValue<string>();
        int limit = args["limit"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 500;

        string global = ProjectSettings.GlobalizePath(dirArg);
        if (!Directory.Exists(global))
            throw new AdapterError("node_not_found", $"Directory '{dirArg}' does not exist.");

        var files = new JsonArray();
        Walk(global, ProjectSettings.GlobalizePath("res://"), recursive, extFilter, limit, files);
        return new JsonObject { ["dir"] = dirArg, ["count"] = files.Count, ["files"] = files };
    }

    private static void Walk(string dir, string projectRoot, bool recursive, string? extFilter, int limit, JsonArray sink)
    {
        string[] files;
        try { files = Directory.GetFiles(dir); } catch { return; }
        foreach (var f in files)
        {
            if (sink.Count >= limit) return;
            if (extFilter is not null && !string.Equals(Path.GetExtension(f), extFilter, StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(f);
            sink.Add(new JsonObject
            {
                ["path"] = "res://" + Path.GetRelativePath(projectRoot, f).Replace('\\', '/'),
                ["size"] = info.Length,
            });
        }
        if (!recursive) return;
        string[] subs;
        try { subs = Directory.GetDirectories(dir); } catch { return; }
        foreach (var sub in subs)
        {
            string name = Path.GetFileName(sub);
            if (name == ".godot" || name == ".import" || name == ".vs" || name == "obj" || name == "bin") continue;
            Walk(sub, projectRoot, recursive, extFilter, limit, sink);
        }
    }

    private static JsonNode? ReloadScripts(JsonObject args)
    {
        EI.GetResourceFilesystem().Scan();
        return new JsonObject { ["rescanned"] = true };
    }

    // ════════════════════════════════════════════════════════════════════
    // EVAL (UNSAFE)
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? EvalExpression(JsonObject args)
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

        var expr = new Expression();
        var parseErr = expr.Parse(source, inputNames.ToArray());
        if (parseErr != Error.Ok)
            throw new AdapterError("invalid_args", $"Parse failed: {expr.GetErrorText()}");
        var valuesArray = new Godot.Collections.Array();
        foreach (var v in inputValues) valuesArray.Add(v);
        var result = expr.Execute(valuesArray);
        if (expr.HasExecuteFailed())
            throw new AdapterError("internal_error", $"Execute failed: {expr.GetErrorText()}");
        return new JsonObject
        {
            ["type"] = result.VariantType.ToString(),
            ["value"] = WireJson.ToJson(result),
        };
    }

    // ════════════════════════════════════════════════════════════════════
    // BUILD ERROR PARSING (used by editor_build_project rework)
    // ════════════════════════════════════════════════════════════════════

    // MSBuild line shapes we handle:
    //  /abs/path/File.cs(12,34): error CS1234: message [.../Project.csproj]
    //  /abs/path/File.cs(12,34): warning CS1234: message
    //  Project.csproj : error NU1100: message
    private static readonly Regex BuildErrorRegex = new(
        @"^(?<file>[^()\r\n]+?)(?:\((?<line>\d+)(?:,(?<col>\d+))?\))?\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Z0-9]+)\s*:\s*(?<msg>.+?)(?:\s\[[^\]]+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static JsonArray ParseBuildOutput(string combinedOutput, string projectRoot)
    {
        var arr = new JsonArray();
        foreach (Match m in BuildErrorRegex.Matches(combinedOutput))
        {
            string file = m.Groups["file"].Value.Trim();
            // Convert absolute → res:// when inside project.
            string fileNorm = file;
            try
            {
                string fp = Path.GetFullPath(file);
                string rp = Path.GetFullPath(projectRoot);
                if (fp.StartsWith(rp, StringComparison.OrdinalIgnoreCase))
                    fileNorm = "res://" + Path.GetRelativePath(rp, fp).Replace('\\', '/');
            }
            catch { }

            var entry = new JsonObject
            {
                ["file"] = fileNorm,
                ["severity"] = m.Groups["sev"].Value,
                ["code"] = m.Groups["code"].Value,
                ["message"] = m.Groups["msg"].Value.Trim(),
            };
            if (m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out var ln)) entry["line"] = ln;
            if (m.Groups["col"].Success && int.TryParse(m.Groups["col"].Value, out var col)) entry["column"] = col;
            arr.Add(entry);
        }
        return arr;
    }

    private static string ReqString(JsonObject args, string name)
    {
        var v = args[name]?.GetValue<string>();
        if (string.IsNullOrEmpty(v)) throw new AdapterError("invalid_args", $"Missing '{name}'.");
        return v;
    }
}
#endif
