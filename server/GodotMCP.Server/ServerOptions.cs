namespace GodotMCP.Server;

public sealed class ServerOptions
{
    public int AdapterPort { get; set; } = 4936;
    public bool ReadOnly { get; set; } = false;

    public static ServerOptions FromArgs(string[] args)
    {
        var o = new ServerOptions();
        // Env vars first (easier to set from MCP client launch config).
        if (int.TryParse(Environment.GetEnvironmentVariable("GODOT_MCP_PORT"), out var envPort) && envPort > 0)
            o.AdapterPort = envPort;
        if (Environment.GetEnvironmentVariable("GODOT_MCP_READ_ONLY") is { } ro && (ro == "1" || ro.Equals("true", StringComparison.OrdinalIgnoreCase)))
            o.ReadOnly = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[++i], out var p) && p > 0:
                    o.AdapterPort = p; break;
                case "--read-only":
                    o.ReadOnly = true; break;
                case "--help" or "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }
        return o;
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine("godot-mcp-server");
        Console.Error.WriteLine("  --port <n>     TCP port for Godot adapters to connect to (default 4936).");
        Console.Error.WriteLine("  --read-only    Reject any tool call that mutates editor/runtime state.");
        Console.Error.WriteLine("  --help         Show this help.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Environment:");
        Console.Error.WriteLine("  GODOT_MCP_PORT, GODOT_MCP_READ_ONLY (1/true)");
    }
}
