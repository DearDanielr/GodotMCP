using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace GodotMcp.Shared;

/// Marshals work from arbitrary threads onto Godot's main thread.
/// Backed by a node that drains a queue on every _Process tick.
/// CallDeferred would also work, but draining in _Process keeps timing predictable
/// (always between idle frames, never mid-physics) and gives a single place to bound work per frame.
public partial class MainThreadDispatcher : Node
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private const int MaxItemsPerFrame = 64;

    public override void _Ready()
    {
        // Keep draining even when the game is paused, so inspection tools work during a pause.
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Process(double delta)
    {
        int processed = 0;
        while (processed < MaxItemsPerFrame && _queue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { GD.PrintErr($"[godot_mcp] main-thread action threw: {ex}"); }
            processed++;
        }
    }

    public Task<T> RunAsync<T>(Func<T> fn, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reg = ct.Register(() => tcs.TrySetCanceled());
        _queue.Enqueue(() =>
        {
            reg.Dispose();
            if (tcs.Task.IsCompleted) return;
            try { tcs.TrySetResult(fn()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task RunAsync(Action fn, CancellationToken ct)
    {
        return RunAsync<bool>(() => { fn(); return true; }, ct);
    }
}
