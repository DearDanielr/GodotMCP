using GodotMCP.Server.Adapters;

namespace GodotMCP.Server.Tools;

/// Forwards a tool call to the named adapter as a wire action. The adapter is
/// the source of truth for what each action does.
internal static class SurfaceTools
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(120);

    public static ToolHandler ForwardAction(AdapterRegistry adapters, Surface surface, string action, TimeSpan? timeout = null)
    {
        var to = timeout ?? DefaultTimeout;
        return async (args, ct) =>
        {
            var conn = adapters.Require(surface);
            return await conn.CallAsync(action, args, to, ct).ConfigureAwait(false);
        };
    }
}
