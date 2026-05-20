using GodotMCP.Server;
using GodotMCP.Server.Adapters;
using GodotMCP.Server.Mcp;
using GodotMCP.Server.Tools;

var options = ServerOptions.FromArgs(args);

void Log(string msg) => Console.Error.WriteLine($"[godot-mcp {DateTimeOffset.Now:HH:mm:ss.fff}] {msg}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await using var adapters = new AdapterRegistry();
await using var listener = new AdapterListener(adapters, options.AdapterPort, Log);
listener.Start();

var tools = new ToolRegistry();
BuiltinTools.Register(tools, adapters);
EditorTools.Register(tools, adapters);
RuntimeTools.Register(tools, adapters);
Log($"Registered {tools.Count} tools.");

if (options.ReadOnly)
    Log("Read-only mode is enabled (mutation tools will be enforced at the adapter).");

var stdin = Console.In;
// Use Console.Out directly; on .NET stdio is binary-safe enough for newline-delimited JSON.
var stdout = Console.Out;

var mcp = new McpServer(tools, stdin, stdout, Log, options.ReadOnly);
await mcp.RunAsync(cts.Token);
