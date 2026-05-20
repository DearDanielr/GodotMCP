using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using FileAccess = System.IO.FileAccess;

namespace GodotMcp.Shared;

/// Reads Godot's own log file. Captures GD.Print, errors, engine messages —
/// everything the engine itself writes. We don't try to intercept print calls
/// inside the process because Godot 4 doesn't expose a stable C# Logger hook;
/// tailing the file is simpler and catches more.
internal static class LogTail
{
    public static string DefaultLogPath()
    {
        // Honor the project's logging path setting if set; otherwise the Godot default.
        var setting = ProjectSettings.GetSetting("debug/file_logging/log_path");
        string configured = setting.VariantType == Variant.Type.String ? setting.AsString() : "";
        string path = string.IsNullOrEmpty(configured) ? "user://logs/godot.log" : configured;
        return ProjectSettings.GlobalizePath(path);
    }

    /// Read the last `tailLines` lines from the log. Returns lines + total size,
    /// so callers can show "showing X of Y bytes". Safe to call on a file being
    /// actively written to (opens with FileShare.ReadWrite).
    public static (List<string> Lines, long TotalBytes, string Path) ReadTail(string path, int tailLines)
    {
        var lines = new List<string>();
        if (!File.Exists(path)) return (lines, 0, path);

        long totalBytes;
        // Read the whole file — Godot logs are small (KB to low MB) and tailing
        // is mostly used after errors, where you want everything around it.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
        {
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                lines.Add(line);
                if (lines.Count > tailLines * 4)
                    lines.RemoveRange(0, lines.Count - tailLines * 2); // keep memory bounded
            }
            totalBytes = fs.Length;
        }

        if (lines.Count > tailLines)
            lines.RemoveRange(0, lines.Count - tailLines);
        return (lines, totalBytes, path);
    }

    /// Read only what's been appended since `lastOffset`. Returns the new bytes
    /// as lines plus the new offset. Lets callers do "give me what's new since
    /// I last asked" without re-scanning.
    public static (List<string> NewLines, long NewOffset, string Path) ReadSince(string path, long lastOffset)
    {
        var lines = new List<string>();
        if (!File.Exists(path)) return (lines, lastOffset, path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (lastOffset > fs.Length)
        {
            // File rotated/truncated — start fresh.
            lastOffset = 0;
        }
        fs.Seek(lastOffset, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) is not null)
            lines.Add(line);
        return (lines, fs.Position, path);
    }
}
