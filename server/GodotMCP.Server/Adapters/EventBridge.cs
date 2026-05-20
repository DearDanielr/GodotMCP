using System.Text.Json.Nodes;
using GodotMCP.Server.Mcp;

namespace GodotMCP.Server.Adapters;

/// Subscribes to adapter events and forwards them to the MCP client as
/// JSON-RPC notifications under "notifications/message". Lets adapters
/// push state (property watches, log lines, build errors) without the
/// client polling. The payload shape is:
///   { "level": "info", "data": { "kind": "godot.event", "surface": "...", "name": "...", "payload": ... } }
public sealed class EventBridge : IDisposable
{
    private readonly AdapterRegistry _adapters;
    private readonly McpServer _mcp;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();

    public EventBridge(AdapterRegistry adapters, McpServer mcp, Action<string> log)
    {
        _adapters = adapters;
        _mcp = mcp;
        _log = log;
        _adapters.OnChanged += HandleChanged;
        // Wire up any already-connected adapters.
        if (_adapters.Editor is { } e) e.OnEvent += HandleEvent;
        if (_adapters.Runtime is { } r) r.OnEvent += HandleEvent;
    }

    private void HandleChanged(Surface surface, AdapterConnection? conn)
    {
        if (conn is null) return;
        conn.OnEvent += HandleEvent;
    }

    private void HandleEvent(AdapterConnection conn, string name, JsonNode? payload)
    {
        var note = new JsonObject
        {
            ["level"] = "info",
            ["logger"] = "godot-mcp",
            ["data"] = new JsonObject
            {
                ["kind"] = "godot.event",
                ["surface"] = conn.Surface.ToWire(),
                ["name"] = name,
                ["payload"] = payload?.DeepClone(),
            },
        };
        _ = ForwardAsync(note);
    }

    private async Task ForwardAsync(JsonObject note)
    {
        try
        {
            await _mcp.SendNotificationAsync("notifications/message", note, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"Event forward failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _adapters.OnChanged -= HandleChanged;
        if (_adapters.Editor is { } e) e.OnEvent -= HandleEvent;
        if (_adapters.Runtime is { } r) r.OnEvent -= HandleEvent;
        _cts.Dispose();
    }
}
