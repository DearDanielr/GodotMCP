using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMCP.Server.Adapters;
using GodotMCP.Server.Tools;

namespace GodotMCP.Server.Mcp;

/// MCP JSON-RPC server speaking newline-delimited JSON on stdin/stdout.
/// Handles initialize, tools/list, tools/call, ping.
public sealed class McpServer
{
    private readonly ToolRegistry _tools;
    private readonly TextReader _stdin;
    private readonly TextWriter _stdout;
    private readonly Action<string> _log;
    private readonly bool _readOnly;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private bool _initialized;
    private const string ProtocolVersion = "2024-11-05";

    public McpServer(ToolRegistry tools, TextReader stdin, TextWriter stdout, Action<string> log, bool readOnly = false)
    {
        _tools = tools;
        _stdin = stdin;
        _stdout = stdout;
        _log = log;
        _readOnly = readOnly;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log("MCP server starting on stdio.");
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await _stdin.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (line is null) break;
            if (line.Length == 0) continue;

            _ = Task.Run(() => HandleLineAsync(line, ct), ct);
        }
        _log("MCP server stdin closed; exiting.");
    }

    private async Task HandleLineAsync(string line, CancellationToken ct)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(line); }
        catch (JsonException ex)
        {
            await WriteAsync(JsonRpcMessages.Error(null, JsonRpcCodes.ParseError, ex.Message), ct);
            return;
        }
        if (msg is not JsonObject req)
        {
            await WriteAsync(JsonRpcMessages.Error(null, JsonRpcCodes.InvalidRequest, "Message was not an object."), ct);
            return;
        }

        JsonNode? id = req["id"];
        string? method = (string?)req["method"];
        bool isNotification = id is null;

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => HandleInitialize(req),
                "initialized" or "notifications/initialized" => HandleInitializedNotification(),
                "tools/list" => HandleToolsList(),
                "tools/call" => await HandleToolsCallAsync(req, ct).ConfigureAwait(false),
                "ping" => new JsonObject(),
                "notifications/cancelled" => null,
                null => throw new JsonRpcException(JsonRpcCodes.InvalidRequest, "Missing 'method'."),
                _ => throw new JsonRpcException(JsonRpcCodes.MethodNotFound, $"Method '{method}' not found."),
            };

            if (!isNotification)
                await WriteAsync(JsonRpcMessages.Result(id, result), ct);
        }
        catch (JsonRpcException ex)
        {
            if (!isNotification)
                await WriteAsync(JsonRpcMessages.Error(id, ex.Code, ex.Message, ex.Data), ct);
        }
        catch (Exception ex)
        {
            _log($"Unhandled error in '{method}': {ex}");
            if (!isNotification)
                await WriteAsync(JsonRpcMessages.Error(id, JsonRpcCodes.InternalError, ex.Message), ct);
        }
    }

    private JsonNode HandleInitialize(JsonObject req)
    {
        _initialized = true;
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "godot-mcp",
                ["version"] = ThisAssembly.Version,
            },
        };
    }

    private JsonNode? HandleInitializedNotification()
    {
        // Notifications don't get a response. Return null so the dispatcher
        // does not send one (id is also null in notifications).
        return null;
    }

    private JsonNode HandleToolsList()
    {
        RequireInitialized();
        return new JsonObject { ["tools"] = _tools.ListWire() };
    }

    private async Task<JsonNode> HandleToolsCallAsync(JsonObject req, CancellationToken ct)
    {
        RequireInitialized();
        var p = req["params"] as JsonObject
            ?? throw new JsonRpcException(JsonRpcCodes.InvalidParams, "Missing params.");
        string name = (string?)p["name"]
            ?? throw new JsonRpcException(JsonRpcCodes.InvalidParams, "Missing tool name.");
        var args = (p["arguments"] as JsonObject) ?? new JsonObject();

        if (!_tools.TryGet(name, out var tool))
            throw new JsonRpcException(JsonRpcCodes.MethodNotFound, $"Tool '{name}' not found.");

        if (_readOnly && tool.Mutates)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = $"[{ErrorCodes.ReadOnly}] Server is in read-only mode; '{name}' would mutate state." }
                },
                ["isError"] = true,
                ["structuredContent"] = new JsonObject
                {
                    ["code"] = ErrorCodes.ReadOnly,
                    ["message"] = $"Server is in read-only mode; '{name}' would mutate state.",
                },
            };
        }

        try
        {
            var result = await tool.Handler(args, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = ToolResultText(result),
                    }
                },
                ["isError"] = false,
                ["structuredContent"] = result ?? new JsonObject(),
            };
        }
        catch (AdapterException ex)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = $"[{ex.Code}] {ex.Message}" }
                },
                ["isError"] = true,
                ["structuredContent"] = new JsonObject
                {
                    ["code"] = ex.Code,
                    ["message"] = ex.Message,
                },
            };
        }
    }

    private static string ToolResultText(JsonNode? node)
    {
        if (node is null) return "ok";
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private void RequireInitialized()
    {
        if (!_initialized)
            throw new JsonRpcException(JsonRpcCodes.InvalidRequest, "Server has not been initialized.");
    }

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        string line = message.ToJsonString();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stdout.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _stdout.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
