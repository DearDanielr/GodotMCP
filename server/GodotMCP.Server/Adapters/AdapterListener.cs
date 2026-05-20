using System.Net;
using System.Net.Sockets;

namespace GodotMCP.Server.Adapters;

/// Listens on a local TCP port and hands off accepted connections to the registry
/// after the hello handshake completes. Stays bound to loopback only.
public sealed class AdapterListener : IAsyncDisposable
{
    private readonly AdapterRegistry _registry;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private readonly Action<string> _log;

    public int Port { get; private set; }

    public AdapterListener(AdapterRegistry registry, int port, Action<string> log)
    {
        _registry = registry;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _log = log;
    }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log($"Listening for Godot adapters on 127.0.0.1:{Port}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log($"Accept failed: {ex.Message}"); continue; }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    handshakeCts.CancelAfter(TimeSpan.FromSeconds(10));
                    var conn = await AdapterConnection.HandshakeAsync(client, handshakeCts.Token).ConfigureAwait(false);
                    _log($"Adapter connected: {conn.Surface.ToWire()} (godot {conn.GodotVersion}) from {conn.PeerEndpoint}");
                    conn.OnClosed += (c, ex) => _log($"Adapter disconnected: {c.Surface.ToWire()}" + (ex is null ? "" : $" ({ex.Message})"));
                    _registry.Register(conn);
                }
                catch (Exception ex)
                {
                    _log($"Adapter handshake failed: {ex.Message}");
                    client.Dispose();
                }
            }, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { if (_acceptTask is not null) await _acceptTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
