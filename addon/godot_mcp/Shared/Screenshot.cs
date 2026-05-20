using System;
using System.Text.Json.Nodes;
using Godot;

namespace GodotMcp.Shared;

internal static class Screenshot
{
    /// Capture a viewport's current contents as a PNG, packaged for the MCP server's
    /// image-content magic shape (server detects `_mcp_image` and emits an image block).
    public static JsonObject Capture(Viewport viewport, int? maxSide = null)
    {
        var tex = viewport.GetTexture();
        if (tex is null)
            throw new AdapterError("internal_error", "Viewport has no texture; the viewport may not have rendered yet.");

        Image img = tex.GetImage();
        if (img is null)
            throw new AdapterError("internal_error", "Could not retrieve image from viewport texture.");

        // Optional downscale so big screenshots don't blow context budgets.
        if (maxSide.HasValue)
        {
            int max = maxSide.Value;
            int w = img.GetWidth(), h = img.GetHeight();
            if (w > max || h > max)
            {
                float scale = (float)max / Math.Max(w, h);
                img.Resize((int)(w * scale), (int)(h * scale), Image.Interpolation.Bilinear);
            }
        }

        byte[] png = img.SavePngToBuffer();
        return new JsonObject
        {
            ["_mcp_image"] = new JsonObject
            {
                ["data"] = Convert.ToBase64String(png),
                ["mime_type"] = "image/png",
            },
            ["width"] = img.GetWidth(),
            ["height"] = img.GetHeight(),
            ["bytes"] = png.Length,
        };
    }
}
