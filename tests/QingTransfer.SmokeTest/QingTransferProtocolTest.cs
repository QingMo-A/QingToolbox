using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
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
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
