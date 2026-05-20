namespace GodotMCP.Server.Adapters;

public enum Surface
{
    Editor,
    Runtime,
}

public static class SurfaceParser
{
    public static bool TryParse(string? s, out Surface surface)
    {
        switch (s)
        {
            case "editor": surface = Surface.Editor; return true;
            case "runtime": surface = Surface.Runtime; return true;
            default: surface = default; return false;
        }
    }

    public static string ToWire(this Surface s) => s == Surface.Editor ? "editor" : "runtime";
}
