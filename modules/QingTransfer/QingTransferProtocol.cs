using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace QingToolbox.Modules.QingTransfer;

public static class QingTransferProtocol
{
    internal const int MaxFrameBytes = 4096;
    internal const int MaxFieldLength = 128;
    internal const int HandshakeTimeoutSeconds = 5;

    public abstract record Message(string Type);
    public sealed record HelloMessage(string Platform, string Name) : Message("hello");
    public sealed record AcceptMessage() : Message("accept");
    public sealed record RejectMessage() : Message("reject");

    internal static byte[] EncodeFrame(Message message)
    {
        object contract = message switch
        {
            HelloMessage hello => new { type = "hello", v = 1, pf = hello.Platform, name = hello.Name },
            AcceptMessage => new { type = "accept", v = 1 },
            RejectMessage => new { type = "reject", v = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(message)),
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(contract);
        if (payload.Length is <= 0 or > MaxFrameBytes) throw new InvalidDataException("Protocol frame is too large.");
        var frame = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, sizeof(uint)), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(uint)));
        return frame;
    }

    internal static bool TryDecodeFrame(ReadOnlySpan<byte> frame, out Message? message)
    {
        message = null;
        if (frame.Length is <= sizeof(uint) or > sizeof(uint) + MaxFrameBytes) return false;
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(frame[..sizeof(uint)]);
        if (payloadLength is 0 or > MaxFrameBytes || payloadLength != frame.Length - sizeof(uint)) return false;
        return TryParsePayload(frame[sizeof(uint)..], out message);
    }

    internal static async Task<Message?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = ArrayPool<byte>.Shared.Rent(sizeof(uint));
        try
        {
            await ReadExactlyAsync(stream, header.AsMemory(0, sizeof(uint)), cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, sizeof(uint)));
            if (length is 0 or > MaxFrameBytes) throw new InvalidDataException("Protocol frame is too large.");
            var payload = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                await ReadExactlyAsync(stream, payload.AsMemory(0, (int)length), cancellationToken).ConfigureAwait(false);
                return TryParsePayload(payload.AsSpan(0, (int)length), out var message) ? message : null;
            }
            finally { ArrayPool<byte>.Shared.Return(payload); }
        }
        finally { ArrayPool<byte>.Shared.Return(header); }
    }

    internal static async Task WriteMessageAsync(Stream stream, Message message, CancellationToken cancellationToken)
    {
        var frame = EncodeFrame(message);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParsePayload(ReadOnlySpan<byte> payload, out Message? message)
    {
        message = null;
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement) ||
                !root.TryGetProperty("v", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number ||
                versionElement.GetInt32() != 1 || typeElement.ValueKind != JsonValueKind.String) return false;
            var type = typeElement.GetString();
            if (type == "accept" && root.EnumerateObject().Count() == 2) { message = new AcceptMessage(); return true; }
            if (type == "reject" && root.EnumerateObject().Count() == 2) { message = new RejectMessage(); return true; }
            if (type != "hello" || !root.TryGetProperty("pf", out var platformElement) ||
                !root.TryGetProperty("name", out var nameElement) || platformElement.ValueKind != JsonValueKind.String ||
                nameElement.ValueKind != JsonValueKind.String) return false;
            var platform = platformElement.GetString() ?? string.Empty;
            var name = nameElement.GetString() ?? string.Empty;
            if (!IsSafeField(platform) || platform is not ("windows" or "android") || !IsSafeField(name)) return false;
            message = new HelloMessage(platform, name);
            return root.EnumerateObject().Count() == 4;
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool IsSafeField(string value) =>
        value.Length is > 0 and <= MaxFieldLength && !value.Any(char.IsControl);

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The peer closed the protocol stream.");
            read += count;
        }
    }
}
