using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.IO;
using QingToolbox.Modules.QingTransfer;

namespace QingToolbox.Modules.QingTransfer;

public static class QingTransferProtocolTest
{
    public static async Task RunAsync()
    {
        var hello = new QingTransferProtocol.HelloMessage("windows", "Smoke");
        var frame = QingTransferProtocol.EncodeFrame(hello);
        Require(QingTransferProtocol.TryDecodeFrame(frame, out var decoded) && decoded is QingTransferProtocol.HelloMessage, "Hello frame roundtrip failed.");
        var oversized = new byte[sizeof(uint) + QingTransferProtocol.MaxFrameBytes + 1];
        BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(0, sizeof(uint)), (uint)(oversized.Length - sizeof(uint)));
        Require(!QingTransferProtocol.TryDecodeFrame(oversized, out _), "Oversized frame was accepted.");
        var malformed = frame.ToArray(); malformed[^1] = (byte)'!';
        Require(!QingTransferProtocol.TryDecodeFrame(malformed, out _), "Malformed JSON was accepted.");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var accept = listener.AcceptTcpClientAsync();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await accept;
        await QingTransferProtocol.WriteMessageAsync(client.GetStream(), hello, CancellationToken.None);
        Require(await QingTransferProtocol.ReadMessageAsync(server.GetStream(), CancellationToken.None) is QingTransferProtocol.HelloMessage, "Stream frame failed.");
        Require(QingTransferProtocol.TryDecodeFrame(QingTransferProtocol.EncodeFrame(new QingTransferProtocol.FileOfferMessage("x.txt", 0)), out var offer) && offer is QingTransferProtocol.FileOfferMessage { Size: 0 }, "Zero-byte offer failed.");
        Require(QingTransferProtocol.TryDecodeFrame(QingTransferProtocol.EncodeFrame(new QingTransferProtocol.FileEndMessage(new string('a', 64))), out var end) && end is QingTransferProtocol.FileEndMessage, "File end failed.");
        Require(QingTransferProtocol.TryDecodeFrame(QingTransferProtocol.EncodeFrame(new QingTransferProtocol.FileResultMessage(true)), out var result) && result is QingTransferProtocol.FileResultMessage { Ok: true }, "File result failed.");
        Require(!QingTransferProtocol.TryDecodeFrame(QingTransferProtocol.EncodeFrame(new QingTransferProtocol.FileOfferMessage("../unsafe", 1)), out _), "Unsafe offer name was accepted.");
        await RunStreamingWireAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 });
        await RunStreamingWireAsync(Array.Empty<byte>());
    }

    private static async Task RunStreamingWireAsync(byte[] payload)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var accept = listener.AcceptTcpClientAsync();
        using var sender = new TcpClient(); await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var receiver = await accept;
        var sendStream = sender.GetStream(); var receiveStream = receiver.GetStream();
        await QingTransferProtocol.WriteMessageAsync(sendStream, new QingTransferProtocol.FileOfferMessage("wire.bin", payload.Length), CancellationToken.None);
        Require(await QingTransferProtocol.ReadMessageAsync(receiveStream, CancellationToken.None) is QingTransferProtocol.FileOfferMessage { Name: "wire.bin" }, "Streaming offer was not read.");
        await QingTransferProtocol.WriteMessageAsync(receiveStream, new QingTransferProtocol.FileAcceptMessage(), CancellationToken.None);
        Require(await QingTransferProtocol.ReadMessageAsync(sendStream, CancellationToken.None) is QingTransferProtocol.FileAcceptMessage, "Streaming accept was not read.");
        await sendStream.WriteAsync(payload); await sendStream.FlushAsync();
        var actual = new byte[payload.Length]; await ReadExactlyAsync(receiveStream, actual);
        Require(actual.AsSpan().SequenceEqual(payload), "Streaming bytes changed.");
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        await QingTransferProtocol.WriteMessageAsync(sendStream, new QingTransferProtocol.FileEndMessage(hash), CancellationToken.None);
        Require(await QingTransferProtocol.ReadMessageAsync(receiveStream, CancellationToken.None) is QingTransferProtocol.FileEndMessage { Sha256: var receivedHash } && receivedHash == hash, "Streaming file end/hash failed.");
        await QingTransferProtocol.WriteMessageAsync(receiveStream, new QingTransferProtocol.FileResultMessage(true), CancellationToken.None);
        Require(await QingTransferProtocol.ReadMessageAsync(sendStream, CancellationToken.None) is QingTransferProtocol.FileResultMessage { Ok: true }, "Streaming result was not read.");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
