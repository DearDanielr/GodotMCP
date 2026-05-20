using System.Text.Json.Nodes;

namespace GodotMCP.Server.Adapters;

/// Holds at most one connected adapter per surface. A new connection on a surface
/// replaces (and disposes) the previous one — this mirrors how a user closing
/// and reopening Godot should "just work" without manual cleanup.
public sealed class AdapterRegistry : IAsyncDisposable
{
    private readonly object _lock = new();
    private AdapterConnection? _editor;
    private AdapterConnection? _runtime;

    public AdapterConnection? Editor { get { lock (_lock) return _editor; } }
    public AdapterConnection? Runtime { get { lock (_lock) return _runtime; } }

    public event Action<Surface, AdapterConnection?>? OnChanged;

    public void Register(AdapterConnection conn)
    {
        AdapterConnection? displaced = null;
        lock (_lock)
        {
            if (conn.Surface == Surface.Editor) { displaced = _editor; _editor = conn; }
            else { displaced = _runtime; _runtime = conn; }
        }
        conn.OnClosed += HandleClosed;
        OnChanged?.Invoke(conn.Surface, conn);

        if (displaced is not null)
            _ = displaced.DisposeAsync();
    }

    private void HandleClosed(AdapterConnection conn, Exception? _)
    {
        bool changed = false;
        lock (_lock)
        {
            if (conn.Surface == Surface.Editor && ReferenceEquals(_editor, conn)) { _editor = null; changed = true; }
            if (conn.Surface == Surface.Runtime && ReferenceEquals(_runtime, conn)) { _runtime = null; changed = true; }
        }
        if (changed) OnChanged?.Invoke(conn.Surface, null);
    }

    public AdapterConnection Require(Surface s)
    {
        var conn = s == Surface.Editor ? Editor : Runtime;
        if (conn is null)
            throw new AdapterException(ErrorCodes.AdapterNotConnected,
                $"No {s.ToWire()} adapter is connected. Open the Godot editor with the godot_mcp plugin enabled" +
                (s == Surface.Runtime ? " and run the game." : "."));
        return conn;
    }

    public JsonObject Snapshot()
    {
        AdapterConnection? e, r;
        lock (_lock) { e = _editor; r = _runtime; }
        return new JsonObject
        {
            ["editor"] = e is null ? null : new JsonObject
            {
                ["godot_version"] = e.GodotVersion,
                ["project_path"] = e.ProjectPath,
                ["peer"] = e.PeerEndpoint,
                ["connected_at"] = e.ConnectedAt.ToString("O"),
            },
            ["runtime"] = r is null ? null : new JsonObject
            {
                ["godot_version"] = r.GodotVersion,
                ["project_path"] = r.ProjectPath,
                ["peer"] = r.PeerEndpoint,
                ["connected_at"] = r.ConnectedAt.ToString("O"),
            },
        };
    }

    public async ValueTask DisposeAsync()
    {
        AdapterConnection? e, r;
        lock (_lock) { e = _editor; r = _runtime; _editor = null; _runtime = null; }
        if (e is not null) await e.DisposeAsync();
        if (r is not null) await r.DisposeAsync();
    }
}
