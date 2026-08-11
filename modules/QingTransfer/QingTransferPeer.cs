using System.Net;

namespace QingToolbox.Modules.QingTransfer;

public sealed record QingTransferPeer(
    string ServiceName,
    string DisplayName,
    string Platform,
    string ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<IPAddress> Addresses,
    int Port,
    bool Online,
    DateTimeOffset? LastSeen = null);

internal sealed class QingTransferPeerTable
{
    private readonly Dictionary<string, QingTransferPeer> _peers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<QingTransferPeer> Snapshot() =>
        _peers.Values
            .OrderBy(peer => peer.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool Upsert(QingTransferPeer peer)
    {
        if (_peers.TryGetValue(peer.ServiceName, out var existing) && existing == peer) return false;
        _peers[peer.ServiceName] = peer;
        return true;
    }

    public bool Remove(string serviceName) => _peers.Remove(serviceName);

    public void Clear() => _peers.Clear();
}
