using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Godot;

namespace GodotMcp.Shared;

/// Best-effort conversion between JSON values (System.Text.Json.Nodes) and Godot Variants.
/// Covers the common types AI tools will exchange: primitives, vectors, colors, arrays, dicts.
/// Object refs are stringified to "Object#<id>" with class info; the client cannot re-hydrate them.
internal static class WireJson
{
    public static JsonNode? ToJson(Variant v)
    {
        switch (v.VariantType)
        {
            case Variant.Type.Nil: return null;
            case Variant.Type.Bool: return v.AsBool();
            case Variant.Type.Int: return v.AsInt64();
            case Variant.Type.Float: return v.AsDouble();
            case Variant.Type.String: return v.AsString();
            case Variant.Type.StringName: return v.AsStringName().ToString();
            case Variant.Type.NodePath: return v.AsNodePath().ToString();

            case Variant.Type.Vector2: { var x = v.AsVector2(); return new JsonObject { ["x"] = x.X, ["y"] = x.Y }; }
            case Variant.Type.Vector2I: { var x = v.AsVector2I(); return new JsonObject { ["x"] = x.X, ["y"] = x.Y }; }
            case Variant.Type.Vector3: { var x = v.AsVector3(); return new JsonObject { ["x"] = x.X, ["y"] = x.Y, ["z"] = x.Z }; }
            case Variant.Type.Vector3I: { var x = v.AsVector3I(); return new JsonObject { ["x"] = x.X, ["y"] = x.Y, ["z"] = x.Z }; }
            case Variant.Type.Vector4: { var x = v.AsVector4(); return new JsonObject { ["x"] = x.X, ["y"] = x.Y, ["z"] = x.Z, ["w"] = x.W }; }
            case Variant.Type.Vector4I: { var x = v.AsVector4I(); return new JsonObject { ["x"] = x.X, ["y"] = x.Y, ["z"] = x.Z, ["w"] = x.W }; }
            case Variant.Type.Rect2: { var r = v.AsRect2(); return new JsonObject { ["x"] = r.Position.X, ["y"] = r.Position.Y, ["w"] = r.Size.X, ["h"] = r.Size.Y }; }
            case Variant.Type.Color: { var c = v.AsColor(); return new JsonObject { ["r"] = c.R, ["g"] = c.G, ["b"] = c.B, ["a"] = c.A }; }

            case Variant.Type.Array:
            {
                var arr = v.AsGodotArray();
                var json = new JsonArray();
                foreach (var item in arr) json.Add(ToJson(item));
                return json;
            }
            case Variant.Type.Dictionary:
            {
                var dict = v.AsGodotDictionary();
                var json = new JsonObject();
                foreach (var key in dict.Keys)
                    json[key.AsString()] = ToJson(dict[key]);
                return json;
            }
            case Variant.Type.PackedByteArray:
            {
                var bytes = v.AsByteArray();
                return Convert.ToBase64String(bytes);
            }
            case Variant.Type.PackedStringArray:
            {
                var arr = v.AsStringArray();
                var json = new JsonArray();
                foreach (var s in arr) json.Add(s);
                return json;
            }
            case Variant.Type.PackedInt32Array:
            {
                var arr = v.AsInt32Array();
                var json = new JsonArray();
                foreach (var i in arr) json.Add(i);
                return json;
            }
            case Variant.Type.PackedInt64Array:
            {
                var arr = v.AsInt64Array();
                var json = new JsonArray();
                foreach (var i in arr) json.Add(i);
                return json;
            }
            case Variant.Type.PackedFloat32Array:
            {
                var arr = v.AsFloat32Array();
                var json = new JsonArray();
                foreach (var f in arr) json.Add(f);
                return json;
            }
            case Variant.Type.PackedFloat64Array:
            {
                var arr = v.AsFloat64Array();
                var json = new JsonArray();
                foreach (var f in arr) json.Add(f);
                return json;
            }
            case Variant.Type.Object:
            {
                var obj = v.AsGodotObject();
                if (obj is null) return null;
                return new JsonObject
                {
                    ["__type"] = "Object",
                    ["class"] = obj.GetClass(),
                    ["id"] = obj.GetInstanceId().ToString(CultureInfo.InvariantCulture),
                };
            }
            default:
                return v.ToString();
        }
    }

    public static Variant FromJson(JsonNode? node, Variant.Type hint = Variant.Type.Nil)
    {
        if (node is null) return new Variant();

        // Hint-driven conversion lets us produce a Vector2 / Color / etc. when we know the target.
        switch (hint)
        {
            case Variant.Type.Vector2: { var v = VecFromJson(node, 2); return new Vector2(v.X, v.Y); }
            case Variant.Type.Vector2I: { var v = VecFromJson(node, 2); return new Vector2I((int)v.X, (int)v.Y); }
            case Variant.Type.Vector3: { var v = VecFromJson(node, 3); return new Vector3(v.X, v.Y, v.Z); }
            case Variant.Type.Vector3I: { var v = VecFromJson(node, 3); return new Vector3I((int)v.X, (int)v.Y, (int)v.Z); }
            case Variant.Type.Vector4: return VecFromJson(node, 4);
            case Variant.Type.Color: return ColorFromJson(node);
            case Variant.Type.NodePath: return new NodePath(node.GetValue<string>());
            case Variant.Type.StringName: return new StringName(node.GetValue<string>());
        }

        switch (node.GetValueKind())
        {
            case System.Text.Json.JsonValueKind.True: return true;
            case System.Text.Json.JsonValueKind.False: return false;
            case System.Text.Json.JsonValueKind.Null: return new Variant();
            case System.Text.Json.JsonValueKind.Number:
            {
                if (node.AsValue().TryGetValue<long>(out var l)) return l;
                if (node.AsValue().TryGetValue<double>(out var d)) return d;
                return 0;
            }
            case System.Text.Json.JsonValueKind.String: return node.GetValue<string>();
            case System.Text.Json.JsonValueKind.Array:
            {
                var arr = (JsonArray)node;
                var g = new Godot.Collections.Array();
                foreach (var item in arr) g.Add(FromJson(item));
                return g;
            }
            case System.Text.Json.JsonValueKind.Object:
            {
                var obj = (JsonObject)node;
                // Heuristic: {x,y} -> Vector2; {x,y,z} -> Vector3; {r,g,b[,a]} -> Color.
                if (obj.ContainsKey("x") && obj.ContainsKey("y") && !obj.ContainsKey("r"))
                {
                    if (obj.ContainsKey("w")) return VecFromJson(obj, 4);
                    if (obj.ContainsKey("z")) { var v3 = VecFromJson(obj, 3); return new Vector3(v3.X, v3.Y, v3.Z); }
                    var v2 = VecFromJson(obj, 2); return new Vector2(v2.X, v2.Y);
                }
                if (obj.ContainsKey("r") && obj.ContainsKey("g") && obj.ContainsKey("b"))
                    return ColorFromJson(obj);

                var g = new Godot.Collections.Dictionary();
                foreach (var kv in obj) g[kv.Key] = FromJson(kv.Value);
                return g;
            }
        }
        return new Variant();
    }

    private static Vector4 VecFromJson(JsonNode node, int dims)
    {
        if (node is JsonObject o)
        {
            float x = AsFloat(o["x"]), y = AsFloat(o["y"]);
            float z = dims >= 3 ? AsFloat(o["z"]) : 0f;
            float w = dims >= 4 ? AsFloat(o["w"]) : 0f;
            return new Vector4(x, y, z, w);
        }
        if (node is JsonArray a && a.Count >= dims)
        {
            float x = AsFloat(a[0]), y = a.Count > 1 ? AsFloat(a[1]) : 0f;
            float z = a.Count > 2 ? AsFloat(a[2]) : 0f;
            float w = a.Count > 3 ? AsFloat(a[3]) : 0f;
            return new Vector4(x, y, z, w);
        }
        return Vector4.Zero;
    }

    private static Color ColorFromJson(JsonNode node)
    {
        if (node is JsonObject o)
        {
            float r = AsFloat(o["r"]), g = AsFloat(o["g"]), b = AsFloat(o["b"]);
            float a = o.ContainsKey("a") ? AsFloat(o["a"]) : 1f;
            return new Color(r, g, b, a);
        }
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
            return new Color(s);
        return Colors.White;
    }

    private static float AsFloat(JsonNode? n)
    {
        if (n is null) return 0f;
        if (n.AsValue().TryGetValue<double>(out var d)) return (float)d;
        if (n.AsValue().TryGetValue<long>(out var l)) return l;
        if (n.AsValue().TryGetValue<string>(out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
        return 0f;
    }
}
