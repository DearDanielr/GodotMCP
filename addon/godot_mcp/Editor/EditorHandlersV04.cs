#if TOOLS
// TileMap is intentionally still supported alongside TileMapLayer; suppress
// the obsolete warning that comes with mentioning it.
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Godot;
using GodotMcp.Shared;

namespace GodotMcp.Editor;

/// Editor handlers added in v0.4: tree editing beyond add/remove, file-system
/// operations, autoload management, resource dependency graph, editor selection
/// and camera focus, TileMap queries/edits. UndoRedo wrapping for mutations
/// lives next to the originals (see EditorHandlers.cs).
internal static class EditorHandlersV04
{
    public static void Register(Dictionary<string, AdapterHandler> h)
    {
        // ── tree editing ───────────────────────────────────────────────────
        h["editor_reparent_node"] = ReparentNode;
        h["editor_move_node"] = MoveNode;
        h["editor_duplicate_node"] = DuplicateNode;

        // ── filesystem ─────────────────────────────────────────────────────
        h["editor_create_folder"] = CreateFolder;
        h["editor_delete_file"] = DeleteFile;
        h["editor_rename_file"] = RenameFile;
        h["editor_move_file"] = MoveFile;

        // ── autoload ───────────────────────────────────────────────────────
        h["editor_list_autoloads"] = ListAutoloads;
        h["editor_add_autoload"] = AddAutoload;
        h["editor_remove_autoload"] = RemoveAutoload;

        // ── resource graph ─────────────────────────────────────────────────
        h["editor_get_resource_dependencies"] = GetResourceDependencies;
        h["editor_find_resource_users"] = FindResourceUsers;

        // ── selection / focus ──────────────────────────────────────────────
        h["editor_select_node"] = SelectNode;
        h["editor_focus_node"] = FocusNode;

        // ── tilemap ────────────────────────────────────────────────────────
        h["editor_tilemap_get_cell"] = TileMapGetCell;
        h["editor_tilemap_set_cell"] = TileMapSetCell;
        h["editor_tilemap_paint_rect"] = TileMapPaintRect;
        h["editor_tilemap_get_used_cells"] = TileMapGetUsedCells;
        h["editor_tilemap_clear"] = TileMapClear;
    }

    private static EditorInterface EI => EditorInterface.Singleton;

    // ════════════════════════════════════════════════════════════════════
    // TREE EDITING
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? ReparentNode(JsonObject args)
    {
        var root = RequireRoot();
        var node = ResolveNode(args);
        if (node == root)
            throw new AdapterError("invalid_args", "Cannot reparent the scene root.");

        string newParentPath = ReqString(args, "new_parent_path");
        var newParent = ResolveNodeUnder(root, newParentPath);
        if (newParent == node)
            throw new AdapterError("invalid_args", "Cannot reparent a node under itself.");
        if (IsAncestor(node, newParent))
            throw new AdapterError("invalid_args", "Cannot reparent a node under one of its descendants.");

        bool keepGlobal = args["keep_global_transform"]?.GetValue<bool>() ?? true;
        var oldParent = node.GetParent();
        if (oldParent is null)
            throw new AdapterError("invalid_args", "Node has no parent.");
        int oldIndex = node.GetIndex();

        var undo = EI.GetEditorUndoRedo();
        undo.CreateAction($"MCP reparent {node.Name}", customContext: root);
        // Use Node.Reparent which handles add+remove and (optionally) preserves transforms.
        undo.AddDoMethod(node, Node.MethodName.Reparent, newParent, keepGlobal);
        undo.AddDoProperty(node, Node.PropertyName.Owner, root);
        undo.AddUndoMethod(node, Node.MethodName.Reparent, oldParent, keepGlobal);
        undo.AddUndoMethod(oldParent, Node.MethodName.MoveChild, node, oldIndex);
        undo.AddUndoProperty(node, Node.PropertyName.Owner, root);
        undo.CommitAction();

        return new JsonObject
        {
            ["path"] = root.GetPathTo(node).ToString(),
            ["old_parent"] = root.GetPathTo(oldParent).ToString(),
            ["new_parent"] = root.GetPathTo(newParent).ToString(),
            ["id"] = InstanceIdString(node),
        };
    }

    private static JsonNode? MoveNode(JsonObject args)
    {
        var root = RequireRoot();
        var node = ResolveNode(args);
        if (node == root)
            throw new AdapterError("invalid_args", "Cannot move the scene root within its parent (no parent).");
        var parent = node.GetParent()
            ?? throw new AdapterError("invalid_args", "Node has no parent.");

        int childCount = parent.GetChildCount();
        int oldIndex = node.GetIndex();
        int newIndex = args["new_index"]?.GetValue<int>()
            ?? throw new AdapterError("invalid_args", "Missing 'new_index'.");
        if (newIndex < 0) newIndex = Math.Max(0, childCount + newIndex);
        if (newIndex >= childCount) newIndex = childCount - 1;

        var undo = EI.GetEditorUndoRedo();
        undo.CreateAction($"MCP move {node.Name} to index {newIndex}", customContext: root);
        undo.AddDoMethod(parent, Node.MethodName.MoveChild, node, newIndex);
        undo.AddUndoMethod(parent, Node.MethodName.MoveChild, node, oldIndex);
        undo.CommitAction();

        return new JsonObject
        {
            ["path"] = root.GetPathTo(node).ToString(),
            ["old_index"] = oldIndex,
            ["new_index"] = newIndex,
            ["id"] = InstanceIdString(node),
        };
    }

    private static JsonNode? DuplicateNode(JsonObject args)
    {
        var root = RequireRoot();
        var node = ResolveNode(args);
        if (node == root)
            throw new AdapterError("invalid_args", "Cannot duplicate the scene root in-place; use editor_create_scene to extract instead.");
        var parent = node.GetParent()
            ?? throw new AdapterError("invalid_args", "Node has no parent.");

        // Duplicate with scripts + groups + signals so the copy is a true clone.
        var flags = (int)(Node.DuplicateFlags.Scripts | Node.DuplicateFlags.Groups | Node.DuplicateFlags.Signals);
        var dup = node.Duplicate(flags)
            ?? throw new AdapterError("internal_error", "Duplicate returned null.");

        string? newName = args["new_name"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(newName)) dup.Name = newName;

        var undo = EI.GetEditorUndoRedo();
        undo.CreateAction($"MCP duplicate {node.Name}", customContext: root);
        undo.AddDoMethod(parent, Node.MethodName.AddChild, dup);
        undo.AddDoProperty(dup, Node.PropertyName.Owner, root);
        undo.AddDoReference(dup);
        undo.AddUndoMethod(parent, Node.MethodName.RemoveChild, dup);
        undo.CommitAction();

        return new JsonObject
        {
            ["path"] = root.GetPathTo(dup).ToString(),
            ["source_path"] = root.GetPathTo(node).ToString(),
            ["name"] = dup.Name.ToString(),
            ["type"] = dup.GetClass(),
            ["id"] = InstanceIdString(dup),
        };
    }

    // ════════════════════════════════════════════════════════════════════
    // FILE SYSTEM
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? CreateFolder(JsonObject args)
    {
        string path = ReqString(args, "path");
        EnsureResPath(path);
        string global = ProjectSettings.GlobalizePath(path);
        if (Directory.Exists(global))
            return new JsonObject { ["path"] = path, ["created"] = false, ["reason"] = "already exists" };
        try { Directory.CreateDirectory(global); }
        catch (Exception ex) { throw new AdapterError("internal_error", $"CreateDirectory failed: {ex.Message}"); }
        EI.GetResourceFilesystem().Scan();
        return new JsonObject { ["path"] = path, ["created"] = true };
    }

    private static JsonNode? DeleteFile(JsonObject args)
    {
        string path = ReqString(args, "path");
        EnsureResPath(path);
        // Refuse paths that look like the project root or other catastrophic targets.
        string trimmed = path.Substring("res://".Length).TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed))
            throw new AdapterError("invalid_args", "Refusing to delete project root.");

        string global = ProjectSettings.GlobalizePath(path);
        bool wasDir = Directory.Exists(global);
        bool wasFile = File.Exists(global);
        if (!wasDir && !wasFile)
            throw new AdapterError("node_not_found", $"'{path}' does not exist.");

        bool recursive = args["recursive"]?.GetValue<bool>() ?? false;
        try
        {
            if (wasFile) File.Delete(global);
            else Directory.Delete(global, recursive);
        }
        catch (IOException ex) when (wasDir && !recursive)
        {
            throw new AdapterError("invalid_args", $"Directory '{path}' is not empty. Set recursive:true to delete contents. ({ex.Message})");
        }
        catch (Exception ex) { throw new AdapterError("internal_error", $"Delete failed: {ex.Message}"); }
        EI.GetResourceFilesystem().Scan();
        return new JsonObject { ["path"] = path, ["deleted"] = true, ["kind"] = wasDir ? "directory" : "file" };
    }

    private static JsonNode? RenameFile(JsonObject args)
    {
        string path = ReqString(args, "path");
        string newName = ReqString(args, "new_name");
        EnsureResPath(path);
        if (newName.Contains('/') || newName.Contains('\\'))
            throw new AdapterError("invalid_args", "'new_name' is a basename, not a path. Use editor_move_file to relocate.");
        string global = ProjectSettings.GlobalizePath(path);
        if (!File.Exists(global) && !Directory.Exists(global))
            throw new AdapterError("node_not_found", $"'{path}' does not exist.");

        string parent = Path.GetDirectoryName(global) ?? "";
        string target = Path.Combine(parent, newName);
        try
        {
            if (File.Exists(global)) File.Move(global, target);
            else Directory.Move(global, target);
        }
        catch (Exception ex) { throw new AdapterError("internal_error", $"Rename failed: {ex.Message}"); }

        string projectRoot = ProjectSettings.GlobalizePath("res://");
        string newPath = "res://" + Path.GetRelativePath(projectRoot, target).Replace('\\', '/');
        EI.GetResourceFilesystem().Scan();
        return new JsonObject { ["old_path"] = path, ["new_path"] = newPath };
    }

    private static JsonNode? MoveFile(JsonObject args)
    {
        string path = ReqString(args, "path");
        string newPath = ReqString(args, "new_path");
        EnsureResPath(path);
        EnsureResPath(newPath);

        string srcGlobal = ProjectSettings.GlobalizePath(path);
        string dstGlobal = ProjectSettings.GlobalizePath(newPath);
        if (!File.Exists(srcGlobal) && !Directory.Exists(srcGlobal))
            throw new AdapterError("node_not_found", $"Source '{path}' does not exist.");

        string? dstDir = Path.GetDirectoryName(dstGlobal);
        if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
        try
        {
            if (File.Exists(srcGlobal)) File.Move(srcGlobal, dstGlobal);
            else Directory.Move(srcGlobal, dstGlobal);
        }
        catch (Exception ex) { throw new AdapterError("internal_error", $"Move failed: {ex.Message}"); }
        EI.GetResourceFilesystem().Scan();
        return new JsonObject { ["old_path"] = path, ["new_path"] = newPath };
    }

    // ════════════════════════════════════════════════════════════════════
    // AUTOLOAD
    // ════════════════════════════════════════════════════════════════════
    // Autoloads are stored in project.godot under "autoload/<Name>", with the
    // value "[*]res://path" where the leading '*' marks the autoload as a
    // singleton (accessible globally). Mutating these takes effect after the
    // editor restarts; we still update the in-memory ProjectSettings so the
    // change is visible immediately to tools.

    private static JsonNode? ListAutoloads(JsonObject args)
    {
        var arr = new JsonArray();
        var singleton = Engine.GetSingleton("ProjectSettings")
            ?? throw new AdapterError("internal_error", "Could not access ProjectSettings singleton.");
        foreach (var d in singleton.GetPropertyList())
        {
            string name = d["name"].AsString();
            if (!name.StartsWith("autoload/", StringComparison.Ordinal)) continue;
            string shortName = name.Substring("autoload/".Length);
            var raw = ProjectSettings.GetSetting(name).AsString();
            bool isSingleton = raw.StartsWith("*", StringComparison.Ordinal);
            string path = isSingleton ? raw.Substring(1) : raw;
            arr.Add(new JsonObject
            {
                ["name"] = shortName,
                ["path"] = path,
                ["singleton"] = isSingleton,
            });
        }
        return new JsonObject { ["count"] = arr.Count, ["autoloads"] = arr };
    }

    private static JsonNode? AddAutoload(JsonObject args)
    {
        string name = ReqString(args, "name");
        string path = ReqString(args, "path");
        bool singleton = args["singleton"]?.GetValue<bool>() ?? true;
        if (!path.StartsWith("res://", StringComparison.Ordinal))
            throw new AdapterError("invalid_args", "'path' must start with 'res://'.");
        if (!ResourceLoader.Exists(path))
            throw new AdapterError("node_not_found", $"Autoload target '{path}' does not exist.");
        if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains(' '))
            throw new AdapterError("invalid_args", "Autoload 'name' must be a single identifier with no slashes/spaces.");

        string key = $"autoload/{name}";
        if (ProjectSettings.HasSetting(key))
            throw new AdapterError("invalid_args", $"Autoload '{name}' already exists. Remove it first to overwrite.");

        string value = singleton ? "*" + path : path;
        ProjectSettings.SetSetting(key, value);
        var err = ProjectSettings.Save();
        if (err != Error.Ok) throw new AdapterError("internal_error", $"ProjectSettings.Save returned {err}.");
        return new JsonObject
        {
            ["name"] = name,
            ["path"] = path,
            ["singleton"] = singleton,
            ["note"] = "Autoload changes typically take effect after restarting the editor.",
        };
    }

    private static JsonNode? RemoveAutoload(JsonObject args)
    {
        string name = ReqString(args, "name");
        string key = $"autoload/{name}";
        if (!ProjectSettings.HasSetting(key))
            throw new AdapterError("node_not_found", $"Autoload '{name}' is not defined.");
        ProjectSettings.Clear(key);
        var err = ProjectSettings.Save();
        if (err != Error.Ok) throw new AdapterError("internal_error", $"ProjectSettings.Save returned {err}.");
        return new JsonObject { ["removed"] = name };
    }

    // ════════════════════════════════════════════════════════════════════
    // RESOURCE GRAPH
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? GetResourceDependencies(JsonObject args)
    {
        string path = ReqString(args, "path");
        if (!path.StartsWith("res://", StringComparison.Ordinal))
            throw new AdapterError("invalid_args", "'path' must start with 'res://'.");
        if (!ResourceLoader.Exists(path))
            throw new AdapterError("node_not_found", $"Resource '{path}' does not exist.");

        // ResourceLoader.GetDependencies returns lines shaped like
        // "<remap_uid>::<remap_type>::<remap_path>". We surface only the path.
        var deps = ResourceLoader.GetDependencies(path);
        var arr = new JsonArray();
        foreach (var entry in deps)
        {
            string s = entry;
            int last = s.LastIndexOf("::", StringComparison.Ordinal);
            string dep = last >= 0 ? s.Substring(last + 2) : s;
            if (!string.IsNullOrEmpty(dep)) arr.Add(dep);
        }
        return new JsonObject { ["path"] = path, ["count"] = arr.Count, ["dependencies"] = arr };
    }

    private static JsonNode? FindResourceUsers(JsonObject args)
    {
        string needle = ReqString(args, "path");
        if (!needle.StartsWith("res://", StringComparison.Ordinal))
            throw new AdapterError("invalid_args", "'path' must start with 'res://'.");
        int limit = args["limit"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 200;

        string projectRoot = ProjectSettings.GlobalizePath("res://");
        var exts = new[] { ".tscn", ".tres", ".gd", ".cs", ".gdshader" };
        var hits = new JsonArray();

        foreach (var dir in EnumerateDirs(projectRoot))
        {
            if (hits.Count >= limit) break;
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { continue; }
            foreach (var f in files)
            {
                if (hits.Count >= limit) break;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                bool extOk = false;
                foreach (var e in exts) if (e == ext) { extOk = true; break; }
                if (!extOk) continue;
                string content;
                try { content = File.ReadAllText(f); }
                catch { continue; }
                if (!content.Contains(needle, StringComparison.Ordinal)) continue;

                string rel = "res://" + Path.GetRelativePath(projectRoot, f).Replace('\\', '/');
                // Find first matching line for context.
                int line = 1;
                int idx = content.IndexOf(needle, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    for (int i = 0; i < idx; i++) if (content[i] == '\n') line++;
                }
                hits.Add(new JsonObject
                {
                    ["file"] = rel,
                    ["line"] = line,
                });
            }
        }
        return new JsonObject
        {
            ["path"] = needle,
            ["count"] = hits.Count,
            ["truncated"] = hits.Count >= limit,
            ["users"] = hits,
        };
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
    // SELECTION / FOCUS
    // ════════════════════════════════════════════════════════════════════

    private static JsonNode? SelectNode(JsonObject args)
    {
        var root = RequireRoot();
        var selection = EI.GetSelection();
        bool extend = args["extend"]?.GetValue<bool>() ?? false;
        if (!extend) selection.Clear();

        if (args["paths"] is JsonArray pa)
        {
            var picked = new JsonArray();
            foreach (var pv in pa)
            {
                string? p = pv?.GetValue<string>();
                if (string.IsNullOrEmpty(p)) continue;
                var n = ResolveNodeUnder(root, p);
                selection.AddNode(n);
                picked.Add(root.GetPathTo(n).ToString());
            }
            return new JsonObject { ["selected"] = picked };
        }
        else
        {
            var node = ResolveNode(args);
            selection.AddNode(node);
            return new JsonObject { ["selected"] = new JsonArray { root.GetPathTo(node).ToString() } };
        }
    }

    private static JsonNode? FocusNode(JsonObject args)
    {
        var root = RequireRoot();
        var node = ResolveNode(args);
        // EditNode selects the node, inspects it, and centers the viewport on it.
        EI.EditNode(node);
        return new JsonObject { ["focused"] = root.GetPathTo(node).ToString(), ["id"] = InstanceIdString(node) };
    }

    // ════════════════════════════════════════════════════════════════════
    // TILEMAP
    // ════════════════════════════════════════════════════════════════════
    // Supports both TileMap (deprecated in 4.3+) and TileMapLayer (preferred).
    // For TileMapLayer the 'layer' arg is ignored.

    private static JsonNode? TileMapGetCell(JsonObject args)
    {
        var node = ResolveNode(args);
        var coords = AsVector2I(args["coords"] ?? throw new AdapterError("invalid_args", "Missing 'coords'."));
        int layer = args["layer"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 0;

        int sourceId;
        Vector2I atlas;
        int alt;
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

    private static JsonNode? TileMapSetCell(JsonObject args)
    {
        var node = ResolveNode(args);
        var coords = AsVector2I(args["coords"] ?? throw new AdapterError("invalid_args", "Missing 'coords'."));
        int sourceId = args["source_id"] is JsonValue sv && sv.TryGetValue<int>(out var s) ? s : -1;
        var atlas = args["atlas_coords"] is JsonNode ac ? AsVector2I(ac) : Vector2I.Zero;
        int alt = args["alternative_tile"] is JsonValue av && av.TryGetValue<int>(out var a) ? a : 0;
        int layer = args["layer"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 0;

        var root = RequireRoot();
        var undo = EI.GetEditorUndoRedo();
        if (node is TileMapLayer tml)
        {
            int oldSource = tml.GetCellSourceId(coords);
            Vector2I oldAtlas = tml.GetCellAtlasCoords(coords);
            int oldAlt = tml.GetCellAlternativeTile(coords);

            undo.CreateAction($"MCP tilemap set_cell {coords}", customContext: root);
            undo.AddDoMethod(tml, TileMapLayer.MethodName.SetCell, coords, sourceId, atlas, alt);
            undo.AddUndoMethod(tml, TileMapLayer.MethodName.SetCell, coords, oldSource, oldAtlas, oldAlt);
            undo.CommitAction();
        }
        else if (node is TileMap tm)
        {
            int oldSource = tm.GetCellSourceId(layer, coords);
            Vector2I oldAtlas = tm.GetCellAtlasCoords(layer, coords);
            int oldAlt = tm.GetCellAlternativeTile(layer, coords);

            undo.CreateAction($"MCP tilemap set_cell L{layer} {coords}", customContext: root);
            undo.AddDoMethod(tm, TileMap.MethodName.SetCell, layer, coords, sourceId, atlas, alt);
            undo.AddUndoMethod(tm, TileMap.MethodName.SetCell, layer, coords, oldSource, oldAtlas, oldAlt);
            undo.CommitAction();
        }
        else throw NotTileMap(node);

        return new JsonObject
        {
            ["coords"] = WireJson.ToJson(coords),
            ["source_id"] = sourceId,
            ["atlas_coords"] = WireJson.ToJson(atlas),
            ["alternative_tile"] = alt,
            ["cleared"] = sourceId < 0,
        };
    }

    private static JsonNode? TileMapPaintRect(JsonObject args)
    {
        var node = ResolveNode(args);
        var from = AsVector2I(args["from"] ?? throw new AdapterError("invalid_args", "Missing 'from'."));
        var to = AsVector2I(args["to"] ?? throw new AdapterError("invalid_args", "Missing 'to'."));
        int sourceId = args["source_id"] is JsonValue sv && sv.TryGetValue<int>(out var s) ? s : -1;
        var atlas = args["atlas_coords"] is JsonNode ac ? AsVector2I(ac) : Vector2I.Zero;
        int alt = args["alternative_tile"] is JsonValue av && av.TryGetValue<int>(out var a) ? a : 0;
        int layer = args["layer"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 0;

        int x0 = Math.Min(from.X, to.X), x1 = Math.Max(from.X, to.X);
        int y0 = Math.Min(from.Y, to.Y), y1 = Math.Max(from.Y, to.Y);
        int painted = 0;

        var root = RequireRoot();
        var undo = EI.GetEditorUndoRedo();
        undo.CreateAction($"MCP tilemap paint_rect {from}..{to}", customContext: root);

        if (node is TileMapLayer tml)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var coords = new Vector2I(x, y);
                    int oldSource = tml.GetCellSourceId(coords);
                    Vector2I oldAtlas = tml.GetCellAtlasCoords(coords);
                    int oldAlt = tml.GetCellAlternativeTile(coords);
                    undo.AddDoMethod(tml, TileMapLayer.MethodName.SetCell, coords, sourceId, atlas, alt);
                    undo.AddUndoMethod(tml, TileMapLayer.MethodName.SetCell, coords, oldSource, oldAtlas, oldAlt);
                    painted++;
                }
        }
        else if (node is TileMap tm)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var coords = new Vector2I(x, y);
                    int oldSource = tm.GetCellSourceId(layer, coords);
                    Vector2I oldAtlas = tm.GetCellAtlasCoords(layer, coords);
                    int oldAlt = tm.GetCellAlternativeTile(layer, coords);
                    undo.AddDoMethod(tm, TileMap.MethodName.SetCell, layer, coords, sourceId, atlas, alt);
                    undo.AddUndoMethod(tm, TileMap.MethodName.SetCell, layer, coords, oldSource, oldAtlas, oldAlt);
                    painted++;
                }
        }
        else
        {
            undo.CommitAction();
            throw NotTileMap(node);
        }

        undo.CommitAction();
        return new JsonObject
        {
            ["painted"] = painted,
            ["from"] = WireJson.ToJson(new Vector2I(x0, y0)),
            ["to"] = WireJson.ToJson(new Vector2I(x1, y1)),
        };
    }

    private static JsonNode? TileMapGetUsedCells(JsonObject args)
    {
        var node = ResolveNode(args);
        int layer = args["layer"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 0;
        Godot.Collections.Array<Vector2I> cells;
        if (node is TileMapLayer tml) cells = tml.GetUsedCells();
        else if (node is TileMap tm) cells = tm.GetUsedCells(layer);
        else throw NotTileMap(node);

        var arr = new JsonArray();
        foreach (var c in cells) arr.Add(WireJson.ToJson(c));
        return new JsonObject { ["count"] = arr.Count, ["cells"] = arr };
    }

    private static JsonNode? TileMapClear(JsonObject args)
    {
        var node = ResolveNode(args);
        var root = RequireRoot();
        var undo = EI.GetEditorUndoRedo();
        undo.CreateAction($"MCP tilemap clear", customContext: root);

        if (node is TileMapLayer tml)
        {
            // Record current cells for undo.
            foreach (var c in tml.GetUsedCells())
            {
                int oldSource = tml.GetCellSourceId(c);
                Vector2I oldAtlas = tml.GetCellAtlasCoords(c);
                int oldAlt = tml.GetCellAlternativeTile(c);
                undo.AddUndoMethod(tml, TileMapLayer.MethodName.SetCell, c, oldSource, oldAtlas, oldAlt);
            }
            undo.AddDoMethod(tml, TileMapLayer.MethodName.Clear);
        }
        else if (node is TileMap tm)
        {
            int layer = args["layer"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : -1;
            if (layer < 0)
            {
                // Clear all layers.
                for (int li = 0; li < tm.GetLayersCount(); li++)
                {
                    foreach (var c in tm.GetUsedCells(li))
                    {
                        int oldSource = tm.GetCellSourceId(li, c);
                        Vector2I oldAtlas = tm.GetCellAtlasCoords(li, c);
                        int oldAlt = tm.GetCellAlternativeTile(li, c);
                        undo.AddUndoMethod(tm, TileMap.MethodName.SetCell, li, c, oldSource, oldAtlas, oldAlt);
                    }
                }
                undo.AddDoMethod(tm, TileMap.MethodName.Clear);
            }
            else
            {
                foreach (var c in tm.GetUsedCells(layer))
                {
                    int oldSource = tm.GetCellSourceId(layer, c);
                    Vector2I oldAtlas = tm.GetCellAtlasCoords(layer, c);
                    int oldAlt = tm.GetCellAlternativeTile(layer, c);
                    undo.AddUndoMethod(tm, TileMap.MethodName.SetCell, layer, c, oldSource, oldAtlas, oldAlt);
                }
                undo.AddDoMethod(tm, TileMap.MethodName.ClearLayer, layer);
            }
        }
        else
        {
            undo.CommitAction();
            throw NotTileMap(node);
        }

        undo.CommitAction();
        return new JsonObject { ["cleared"] = true };
    }

    private static AdapterError NotTileMap(Node node) =>
        new AdapterError("invalid_args", $"Node {node.GetPath()} is not a TileMap or TileMapLayer (class={node.GetClass()}).");

    // ════════════════════════════════════════════════════════════════════
    // SHARED HELPERS
    // ════════════════════════════════════════════════════════════════════

    private static Node RequireRoot() =>
        EI.GetEditedSceneRoot()
            ?? throw new AdapterError("node_not_found", "No scene is currently being edited.");

    private static bool IsAncestor(Node maybeAncestor, Node node)
    {
        for (var p = node.GetParent(); p is not null; p = p.GetParent())
            if (p == maybeAncestor) return true;
        return false;
    }

    private static Node ResolveNode(JsonObject args)
    {
        var root = RequireRoot();
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
        var node = root.GetNodeOrNull(trimmed)
            ?? throw new AdapterError("node_not_found", $"No node at '{path}' under scene root.");
        return node;
    }

    private static Vector2I AsVector2I(JsonNode n)
    {
        if (n is JsonObject o) return new Vector2I(AsInt(o["x"]), AsInt(o["y"]));
        if (n is JsonArray a && a.Count >= 2) return new Vector2I(AsInt(a[0]), AsInt(a[1]));
        throw new AdapterError("invalid_args", "Expected {x,y} or [x,y] integer coordinates.");
    }

    private static int AsInt(JsonNode? n)
    {
        if (n is null) return 0;
        if (n.AsValue().TryGetValue<long>(out var l)) return (int)l;
        if (n.AsValue().TryGetValue<double>(out var d)) return (int)d;
        return 0;
    }

    private static void EnsureResPath(string path)
    {
        if (!path.StartsWith("res://", StringComparison.Ordinal))
            throw new AdapterError("invalid_args", "Path must start with 'res://'.");
    }

    private static string ReqString(JsonObject args, string name)
    {
        var v = args[name]?.GetValue<string>();
        if (string.IsNullOrEmpty(v)) throw new AdapterError("invalid_args", $"Missing '{name}'.");
        return v;
    }

    private static string InstanceIdString(GodotObject obj) =>
        obj.GetInstanceId().ToString(CultureInfo.InvariantCulture);
}
#endif
