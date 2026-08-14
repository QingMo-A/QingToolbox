using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QingToolbox.Modules.QingTransfer;

public static class QingTransferProtocol
{
    internal const int MaxFrameBytes = 4096;
    internal const int MaxFieldLength = 128;
    internal const int HandshakeTimeoutSeconds = 5;
    internal const int ProbeNonceLength = 32;

    public abstract record Message(string Type);
    public sealed record HelloMessage(string Platform, string Name) : Message("hello");
    public sealed record ProbeMessage(string Nonce) : Message("probe");
    public sealed record ProbeAckMessage(string Nonce) : Message("probe_ack");
    public sealed record AcceptMessage() : Message("accept");
    public sealed record RejectMessage() : Message("reject");
    public sealed record FileOfferMessage(string Name, long Size) : Message("file_offer");
    public sealed record FileAcceptMessage() : Message("file_accept");
    public sealed record FileRejectMessage() : Message("file_reject");
    public sealed record FileEndMessage(string Sha256) : Message("file_end");
    public sealed record FileResultMessage(bool Ok) : Message("file_result");

    internal static string CreateProbeNonce()
    {
        Span<byte> bytes = stackalloc byte[ProbeNonceLength / 2];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }


    internal static byte[] EncodeFrame(Message message)
    {
        object contract = message switch
        {
            HelloMessage hello => new { type = "hello", v = 1, pf = hello.Platform, name = hello.Name },
            ProbeMessage probe => new { type = "probe", v = 1, nonce = probe.Nonce },
            ProbeAckMessage ack => new { type = "probe_ack", v = 1, nonce = ack.Nonce },
            AcceptMessage => new { type = "accept", v = 1 },
            RejectMessage => new { type = "reject", v = 1 },
            FileOfferMessage offer => new { type = "file_offer", v = 1, name = offer.Name, size = offer.Size },
            FileAcceptMessage => new { type = "file_accept", v = 1 },
            FileRejectMessage => new { type = "file_reject", v = 1 },
            FileEndMessage end => new { type = "file_end", v = 1, sha256 = end.Sha256 },
            FileResultMessage result => new { type = "file_result", v = 1, ok = result.Ok },
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
            if (type is ("probe" or "probe_ack") && root.EnumerateObject().Count() == 3 &&
                root.TryGetProperty("nonce", out var nonce) && nonce.ValueKind == JsonValueKind.String &&
                IsProbeNonce(nonce.GetString() ?? string.Empty))
            {
                message = type == "probe" ? new ProbeMessage(nonce.GetString()!) : new ProbeAckMessage(nonce.GetString()!);
                return true;
            }
            if (type == "file_accept" && root.EnumerateObject().Count() == 2) { message = new FileAcceptMessage(); return true; }
            if (type == "file_reject" && root.EnumerateObject().Count() == 2) { message = new FileRejectMessage(); return true; }
            if (type == "file_result" && root.EnumerateObject().Count() == 3 && root.TryGetProperty("ok", out var ok) && (ok.ValueKind is JsonValueKind.True or JsonValueKind.False)) { message = new FileResultMessage(ok.GetBoolean()); return true; }
            if (type == "file_end" && root.EnumerateObject().Count() == 3 && root.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String && IsSha256(sha.GetString() ?? string.Empty)) { message = new FileEndMessage(sha.GetString()!); return true; }
            if (type == "file_offer" && root.EnumerateObject().Count() == 4 && root.TryGetProperty("name", out var fileName) && root.TryGetProperty("size", out var fileSize) && fileName.ValueKind == JsonValueKind.String && fileSize.ValueKind == JsonValueKind.Number && fileSize.TryGetInt64(out var length) && length >= 0 && IsSafeFileName(fileName.GetString() ?? string.Empty)) { message = new FileOfferMessage(fileName.GetString()!, length); return true; }
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

    private static bool IsProbeNonce(string value) =>
        value.Length == ProbeNonceLength && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

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

    private static bool IsSafeFileName(string value) => value.Length is > 0 and <= 255 && !value.Any(char.IsControl) && !value.Contains('/') && !value.Contains('\\') && value is not "." and not "..";
    private static bool IsSha256(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
