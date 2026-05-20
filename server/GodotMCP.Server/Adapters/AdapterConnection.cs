using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMCP.Server.Transport;

namespace GodotMCP.Server.Adapters;

/// One physical adapter (editor or runtime) connected over TCP.
/// Owns the read loop; correlates responses with outstanding requests by id.
public sealed class AdapterConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _readTask;

    public Surface Surface { get; }
    public string GodotVersion { get; }
    public string ProjectPath { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public string PeerEndpoint { get; }

    public event Action<AdapterConnection, string, JsonNode?>? OnEvent;
    public event Action<AdapterConnection, Exception?>? OnClosed;

    private AdapterConnection(TcpClient client, Surface surface, string godotVersion, string projectPath)
    {
        _client = client;
        _stream = client.GetStream();
        Surface = surface;
        GodotVersion = godotVersion;
        ProjectPath = projectPath;
        PeerEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    /// Performs the hello handshake on a freshly-accepted socket and returns a wired connection.
    /// The caller assumes ownership.
    public static async Task<AdapterConnection> HandshakeAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        string? frame = await FrameProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false)
            ?? throw new IOException("Adapter closed before hello.");

        JsonNode hello = JsonNode.Parse(frame)
            ?? throw new InvalidDataException("Hello frame was not valid JSON.");

        if ((string?)hello["type"] != "hello")
            throw new InvalidDataException($"Expected hello, got '{(string?)hello["type"]}'.");

        if (!SurfaceParser.TryParse((string?)hello["surface"], out var surface))
            throw new InvalidDataException($"Unknown surface '{(string?)hello["surface"]}'.");

        string version = (string?)hello["godot_version"] ?? "unknown";
        string project = (string?)hello["project_path"] ?? "";

        var conn = new AdapterConnection(client, surface, version, project);

        // Send hello_ack so the adapter knows it's wired up.
        var ack = new JsonObject
        {
            ["type"] = "hello_ack",
            ["server_version"] = ThisAssembly.Version,
        };
        await FrameProtocol.WriteFrameAsync(stream, ack.ToJsonString(), ct).ConfigureAwait(false);

        conn._readTask = Task.Run(() => conn.ReadLoopAsync(conn._cts.Token));
        return conn;
    }

    /// Sends a request, awaits the matching response. Throws AdapterException for typed errors.
    public async Task<JsonNode?> CallAsync(string action, JsonNode? args, TimeSpan timeout, CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var req = new JsonObject
        {
            ["type"] = "request",
            ["id"] = id,
            ["action"] = action,
            ["args"] = args?.DeepClone() ?? new JsonObject(),
        };

        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await FrameProtocol.WriteFrameAsync(_stream, req.ToJsonString(), ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(timeout);
            using (linked.Token.Register(() =>
            {
                if (_pending.TryRemove(id, out var t))
                    t.TrySetException(new AdapterException(ErrorCodes.AdapterTimeout,
                        $"Adapter '{Surface.ToWire()}' did not respond to '{action}' within {timeout.TotalSeconds:0.#}s."));
            }))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? closeReason = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? frame = await FrameProtocol.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                if (frame is null) break;
                DispatchFrame(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            closeReason = ex;
        }
        finally
        {
            foreach (var (_, t) in _pending)
                t.TrySetException(new AdapterException(ErrorCodes.AdapterNotConnected,
                    $"Adapter '{Surface.ToWire()}' disconnected."));
            _pending.Clear();
            OnClosed?.Invoke(this, closeReason);
        }
    }

    private void DispatchFrame(string frame)
    {
        try
        {
            JsonNode? msg = JsonNode.Parse(frame);
            if (msg is null) return;

            string? type = JsonString(msg, "type");
            if (type == "response")
            {
                string? id = JsonString(msg, "id");
                if (id is null || !_pending.TryRemove(id, out var tcs)) return;

                var err = msg["error"];
                if (err is not null && err.GetValueKind() != JsonValueKind.Null)
                {
                    string code = JsonString(err, "code") ?? ErrorCodes.Internal;
                    string message = JsonString(err, "message") ?? "Adapter error.";
                    tcs.TrySetException(new AdapterException(code, message));
                }
                else
                {
                    tcs.TrySetResult(msg["result"]);
                }
            }
            else if (type == "event")
            {
                string name = JsonString(msg, "name") ?? "";
                OnEvent?.Invoke(this, name, msg["payload"]);
            }
        }
        catch (Exception) { /* ignore malformed frame */ }
    }

    private static string? JsonString(JsonNode node, string key)
    {
        var v = node[key];
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_readTask is not null) await _readTask.ConfigureAwait(false); } catch { }
        _stream.Dispose();
        _client.Dispose();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}

internal static class ThisAssembly
{
    public const string Version = "0.1.0";
}
