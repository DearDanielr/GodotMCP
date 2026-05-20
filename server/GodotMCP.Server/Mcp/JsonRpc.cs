using System.Text.Json.Nodes;

namespace GodotMCP.Server.Mcp;

/// JSON-RPC 2.0 error codes from the spec, plus an MCP-specific
/// "tool execution error" we reserve for adapter failures.
public static class JsonRpcCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

public sealed class JsonRpcException : Exception
{
    public int Code { get; }
    public JsonNode? Data { get; }
    public JsonRpcException(int code, string message, JsonNode? data = null) : base(message)
    {
        Code = code;
        Data = data;
    }
}

public static class JsonRpcMessages
{
    public static JsonObject Result(JsonNode? id, JsonNode? result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result ?? new JsonObject(),
        };
    }

    public static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var err = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };
        if (data is not null) err["data"] = data.DeepClone();
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = err,
        };
    }
}
