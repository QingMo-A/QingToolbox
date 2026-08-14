using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
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
        var nonce = QingTransferProtocol.CreateProbeNonce();
        Require(nonce.Length == QingTransferProtocol.ProbeNonceLength &&
                QingTransferProtocol.TryDecodeFrame(QingTransferProtocol.EncodeFrame(new QingTransferProtocol.ProbeMessage(nonce)), out var probe) &&
                probe is QingTransferProtocol.ProbeMessage { Nonce: var decodedNonce } && decodedNonce == nonce,
            "Probe nonce frame roundtrip failed.");
        Require(QingTransferProtocol.TryDecodeFrame(QingTransferProtocol.EncodeFrame(new QingTransferProtocol.ProbeAckMessage(nonce)), out var probeAck) &&
                probeAck is QingTransferProtocol.ProbeAckMessage { Nonce: var decodedAck } && decodedAck == nonce,
            "Probe acknowledgement frame roundtrip failed.");
        await RunProbeWireAsync(validAck: false);
        await RunProbeWireAsync(validAck: true);
        await RunSilentProbeSessionAsync();
        await RunStreamingWireAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 });
        await RunStreamingWireAsync(Array.Empty<byte>());
        RunMoveAfterDisposeRegression();
    }

    private static async Task RunProbeWireAsync(bool validAck)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            var message = await QingTransferProtocol.ReadMessageAsync(accepted.GetStream(), CancellationToken.None);
            Require(message is QingTransferProtocol.ProbeMessage, "Probe endpoint did not receive a probe frame.");
            var nonce = ((QingTransferProtocol.ProbeMessage)message!).Nonce;
            var responseNonce = validAck ? nonce : QingTransferProtocol.CreateProbeNonce();
            await QingTransferProtocol.WriteMessageAsync(accepted.GetStream(), new QingTransferProtocol.ProbeAckMessage(responseNonce), CancellationToken.None);
        });
        var peer = new QingTransferPeer("probe._qingtransfer._tcp.local", "Probe", "windows", "1", ["file"], [IPAddress.Loopback], port, true);
        Require(await QingTransferEndpointProbe.ConfirmAsync(peer, TimeSpan.FromSeconds(1)) == validAck,
            validAck ? "Valid probe acknowledgement was rejected." : "Wrong probe nonce was accepted.");
        await server;
    }

    private static async Task RunSilentProbeSessionAsync()
    {
        await using var discovery = new QingTransferDiscoveryService("ProbeSessionSmoke");
        await using var session = new QingTransferSession(discovery, "ProbeSessionSmoke");
        var incomingRaised = false;
        session.IncomingRequest += (_, _) => incomingRaised = true;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var acceptedTask = listener.AcceptTcpClientAsync();
        using var probeClient = new TcpClient();
        await probeClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await acceptedTask;
        var handle = session.HandleIncomingAsync(accepted, CancellationToken.None);
        var nonce = QingTransferProtocol.CreateProbeNonce();
        await QingTransferProtocol.WriteMessageAsync(probeClient.GetStream(), new QingTransferProtocol.ProbeMessage(nonce), CancellationToken.None);
        Require(await QingTransferProtocol.ReadMessageAsync(probeClient.GetStream(), CancellationToken.None) is QingTransferProtocol.ProbeAckMessage { Nonce: var ack } && ack == nonce,
            "Session did not silently acknowledge a discovery probe.");
        await handle;
        Require(!incomingRaised && session.State == QingTransferSessionState.Idle, "Discovery probe entered the user connection flow.");
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

    private static void RunMoveAfterDisposeRegression()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qingtransfer-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, ".wire.bin.qingtransfer.part");
        var destination = Path.Combine(directory, "wire.bin");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.WriteByte(0x42); output.Flush();
            }
            File.Move(temp, destination, false);
            Require(File.ReadAllBytes(destination).SequenceEqual(new byte[] { 0x42 }), "Disposed temp stream could not be committed.");
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
