using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GodotMcp.Shared;

/// Mirror of the server's framing: 4-byte big-endian length + UTF-8 JSON payload.
internal static class FrameProtocol
{
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream stream, string payload, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(payload);
        if (body.Length > MaxFrameBytes)
            throw new InvalidOperationException($"Frame exceeds {MaxFrameBytes} byte limit ({body.Length}).");

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, 0, 4, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxFrameBytes)
            throw new InvalidDataException($"Frame length {length} out of range.");
        if (length == 0)
            return string.Empty;

        byte[] body = new byte[length];
        if (!await ReadExactAsync(stream, body, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Stream closed mid-frame.");
        return Encoding.UTF8.GetString(body);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer, offset, buffer.Length - offset, ct).ConfigureAwait(false);
            if (n == 0)
                return offset == 0 ? false : throw new EndOfStreamException("Unexpected EOF.");
            offset += n;
        }
        return true;
    }
}
