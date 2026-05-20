using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace GodotMcp.Shared;

/// Handler that runs on the main thread. May throw AdapterError for typed failures.
public delegate JsonNode? AdapterHandler(JsonObject args);

public sealed class AdapterError : Exception
{
    public string Code { get; }
    public AdapterError(string code, string message) : base(message) { Code = code; }
}

/// Common adapter logic: connect to 127.0.0.1:port, send hello, loop on requests,
/// route each through the main-thread dispatcher. Subclasses register handlers
/// and supply identity (surface, project path).
public abstract partial class AdapterBase : Node
{
    private readonly Dictionary<string, AdapterHandler> _handlers = new(StringComparer.Ordinal);
    private MainThreadDispatcher _dispatcher = null!;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private readonly object _socketLock = new();
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    protected abstract string Surface { get; }
    protected abstract string ProjectPath { get; }

    public int Port { get; set; } = 4936;
    public string Host { get; set; } = "127.0.0.1";

    public override void _EnterTree()
    {
        _dispatcher = new MainThreadDispatcher { Name = "MainThreadDispatcher" };
        AddChild(_dispatcher);
        RegisterHandlers(_handlers);

        var envPort = OS.GetEnvironment("GODOT_MCP_PORT");
        if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out var p) && p > 0)
            Port = p;

        _runCts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_runCts.Token));
    }

    public override void _ExitTree()
    {
        _runCts?.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { lock (_socketLock) _stream?.Dispose(); } catch { }
        _runCts?.Dispose();
        _runCts = null;
        _writeLock.Dispose();
    }

    protected abstract void RegisterHandlers(Dictionary<string, AdapterHandler> handlers);

    private async Task RunAsync(CancellationToken ct)
    {
        int backoffMs = 500;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(Host, Port, ct).ConfigureAwait(false);
                var stream = client.GetStream();
                lock (_socketLock) _stream = stream;

                await SendHelloAsync(stream, ct).ConfigureAwait(false);
                // Wait for hello_ack.
                string? ack = await FrameProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false)
                    ?? throw new IOException("Server closed before hello_ack.");
                var ackMsg = JsonNode.Parse(ack);
                if ((string?)ackMsg?["type"] != "hello_ack")
                    throw new IOException($"Expected hello_ack, got {ack}");

                GD.Print($"[godot_mcp/{Surface}] Connected to MCP server at {Host}:{Port}.");
                backoffMs = 500;

                while (!ct.IsCancellationRequested)
                {
                    string? frame = await FrameProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                    if (frame is null) break;
                    _ = HandleFrameAsync(stream, frame, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                GD.Print($"[godot_mcp/{Surface}] Connection lost: {ex.Message}");
            }
            finally
            {
                lock (_socketLock) _stream = null;
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); } catch { break; }
            backoffMs = Math.Min(backoffMs * 2, 5000);
        }
    }

    private async Task SendHelloAsync(NetworkStream stream, CancellationToken ct)
    {
        var hello = new JsonObject
        {
            ["type"] = "hello",
            ["surface"] = Surface,
            ["godot_version"] = Engine.GetVersionInfo()["string"].AsString(),
            ["project_path"] = ProjectPath,
        };
        await FrameProtocol.WriteFrameAsync(stream, hello.ToJsonString(), ct).ConfigureAwait(false);
    }

    private async Task HandleFrameAsync(NetworkStream stream, string frame, CancellationToken ct)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(frame); }
        catch (JsonException) { return; }
        if (msg is not JsonObject req || (string?)req["type"] != "request") return;

        string id = (string?)req["id"] ?? "";
        string action = (string?)req["action"] ?? "";
        var args = (req["args"] as JsonObject) ?? new JsonObject();

        JsonNode response;
        try
        {
            if (!_handlers.TryGetValue(action, out var handler))
                throw new AdapterError("action_not_found", $"Adapter '{Surface}' does not implement action '{action}'.");

            JsonNode? result = await _dispatcher.RunAsync<JsonNode?>(() => handler(args), ct).ConfigureAwait(false);
            response = new JsonObject
            {
                ["type"] = "response",
                ["id"] = id,
                ["result"] = result?.DeepClone() ?? new JsonObject(),
                ["error"] = null,
            };
        }
        catch (AdapterError err)
        {
            response = ErrorResponse(id, err.Code, err.Message);
        }
        catch (Exception ex)
        {
            response = ErrorResponse(id, "internal_error", $"{ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try { await FrameProtocol.WriteFrameAsync(stream, response.ToJsonString(), ct).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }
        catch { /* socket likely closed */ }
    }

    private static JsonObject ErrorResponse(string id, string code, string message)
    {
        return new JsonObject
        {
            ["type"] = "response",
            ["id"] = id,
            ["result"] = null,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
    }
}
