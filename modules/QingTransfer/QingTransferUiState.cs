namespace QingToolbox.Modules.QingTransfer;

internal static class QingTransferUiState
{
    public static bool CanConnect(QingTransferSessionState state, QingTransferPeer peer) =>
        state == QingTransferSessionState.Idle && peer.Online && peer.Port is > 0 and <= ushort.MaxValue;

    public static bool CanDisconnect(QingTransferSessionState state, QingTransferPeer rowPeer, QingTransferPeer? activePeer) =>
        state == QingTransferSessionState.Connected && activePeer is not null &&
        Canonical(rowPeer.ServiceName) == Canonical(activePeer.ServiceName);

    private static string Canonical(string value) => value.Trim().TrimEnd('.').ToLowerInvariant();
}
