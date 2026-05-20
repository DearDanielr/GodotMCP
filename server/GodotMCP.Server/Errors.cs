namespace GodotMCP.Server;

/// Typed error codes returned to the MCP client.
/// Matches the codes the adapters emit on the wire.
public static class ErrorCodes
{
    public const string AdapterNotConnected = "adapter_not_connected";
    public const string AdapterTimeout = "adapter_timeout";
    public const string ActionNotFound = "action_not_found";
    public const string InvalidArgs = "invalid_args";
    public const string NodeNotFound = "node_not_found";
    public const string PropertyNotFound = "property_not_found";
    public const string MethodNotFound = "method_not_found";
    public const string TypeNotInstantiable = "type_not_instantiable";
    public const string BuildFailed = "build_failed";
    public const string WrongThread = "wrong_thread";
    public const string ReadOnly = "read_only";
    public const string UnsafeDisabled = "unsafe_disabled";
    public const string Internal = "internal_error";
    public const string Unsupported = "unsupported";
}

public sealed class AdapterException : Exception
{
    public string Code { get; }
    /// Structured hints attached by the adapter (e.g. {"did_you_mean": [...]}).
    public System.Text.Json.Nodes.JsonNode? Hints { get; }
    public AdapterException(string code, string message, System.Text.Json.Nodes.JsonNode? hints = null) : base(message)
    {
        Code = code;
        Hints = hints;
    }
}
