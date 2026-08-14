using System.Net;
using System.Net.Sockets;
using System.IO;

namespace QingToolbox.Modules.QingTransfer;

/// <summary>
/// Performs a bounded, silent QingTransfer compatibility check for one resolved
/// DNS-SD endpoint. It sends only a nonce-bound probe frame and never starts a
/// user connection or transfer session.
/// </summary>
internal static class QingTransferEndpointProbe
{
    internal static string EndpointKey(QingTransferPeer peer) =>
        string.Join(
            "|",
            peer.ServiceName.Trim().TrimEnd('.').ToLowerInvariant(),
            peer.Port,
            string.Join(",", peer.Addresses
                .Distinct()
                .OrderBy(address => address.ToString(), StringComparer.OrdinalIgnoreCase)
                .Select(address => address.ToString())));

    internal static async Task<bool> ConfirmAsync(
        QingTransferPeer peer,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (peer.Port is < 1 or > ushort.MaxValue || peer.Addresses.Count == 0) return false;
        foreach (var address in peer.Addresses.Distinct())
        {
            using var client = new TcpClient(address.AddressFamily);
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeTimeout.CancelAfter(timeout);
            try
            {
                await client.ConnectAsync(address, peer.Port, probeTimeout.Token).ConfigureAwait(false);
                var nonce = QingTransferProtocol.CreateProbeNonce();
                await QingTransferProtocol.WriteMessageAsync(client.GetStream(), new QingTransferProtocol.ProbeMessage(nonce), probeTimeout.Token).ConfigureAwait(false);
                var response = await QingTransferProtocol.ReadMessageAsync(client.GetStream(), probeTimeout.Token).ConfigureAwait(false);
                if (response is QingTransferProtocol.ProbeAckMessage { Nonce: var ackNonce } &&
                    string.Equals(ackNonce, nonce, StringComparison.Ordinal)) return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
        return false;
    }
}
