using System.Net;
using System.Net.Sockets;
using System.IO;

namespace QingToolbox.Modules.QingTransfer;

public enum QingTransferSessionState
{
    Idle,
    Connecting,
    WaitingApproval,
    Connected,
}

public sealed class QingTransferSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _friendlyName;
    private readonly string _platform;
    private readonly CancellationTokenSource _lifetime = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _receiveTask;
    private QingTransferSessionState _state = QingTransferSessionState.Idle;
    private bool _disposed;

    public QingTransferSession(QingTransferDiscoveryService discovery, string friendlyName, string platform = "windows")
    {
        discovery.IncomingClientHandler = HandleIncomingAsync;
        _friendlyName = friendlyName;
        _platform = platform;
    }

    public event EventHandler<QingTransferSessionState>? StateChanged;
    public event EventHandler<QingTransferProtocol.HelloMessage>? IncomingRequest;
    public event EventHandler<string>? Error;
    public QingTransferSessionState State { get { lock (_gate) return _state; } }
    public QingTransferPeer? Peer { get; private set; }

    public async Task ConnectAsync(QingTransferPeer peer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            if (_state != QingTransferSessionState.Idle) throw new InvalidOperationException("A QingTransfer session is already active.");
            _state = QingTransferSessionState.Connecting;
            Peer = peer;
        }
        RaiseStateChanged();
        try
        {
            foreach (var address in peer.Addresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var client = new TcpClient(address.AddressFamily);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(QingTransferProtocol.HandshakeTimeoutSeconds));
                    await client.ConnectAsync(address, peer.Port, timeout.Token).ConfigureAwait(false);
                    await AttachAndHandshakeAsync(client, outgoing: true, timeout.Token).ConfigureAwait(false);
                    return;
                }
                catch { client.Dispose(); }
            }
            throw new IOException("The device could not be reached.");
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    public async Task AcceptIncomingAsync(CancellationToken cancellationToken = default)
    {
        TcpClient? client;
        lock (_gate)
        {
            if (_state != QingTransferSessionState.WaitingApproval) return;
            client = _client;
            _state = QingTransferSessionState.Connected;
        }
        if (client is null) return;
        try
        {
            await QingTransferProtocol.WriteMessageAsync(_stream!, new QingTransferProtocol.AcceptMessage(), cancellationToken).ConfigureAwait(false);
            RaiseStateChanged();
        }
        catch { await DisconnectAsync().ConfigureAwait(false); }
    }

    public async Task RejectIncomingAsync(CancellationToken cancellationToken = default)
    {
        try { if (_stream is not null) await QingTransferProtocol.WriteMessageAsync(_stream, new QingTransferProtocol.RejectMessage(), cancellationToken).ConfigureAwait(false); }
        catch { }
        await DisconnectAsync().ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        TcpClient? client;
        Task? receive;
        lock (_gate)
        {
            client = _client; _client = null;
            _stream = null; receive = _receiveTask; _receiveTask = null;
            Peer = null;
            _state = QingTransferSessionState.Idle;
        }
        try { client?.Close(); } catch { }
        if (receive is not null) try { await receive.ConfigureAwait(false); } catch { }
        RaiseStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        await DisconnectAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    internal async Task HandleIncomingAsync(TcpClient client, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state != QingTransferSessionState.Idle)
            {
                _ = RejectAndCloseAsync(client, cancellationToken);
                return;
            }
            _client = client;
        }
        try { await AttachAndHandshakeAsync(client, outgoing: false, cancellationToken).ConfigureAwait(false); }
        catch { await DisconnectAsync().ConfigureAwait(false); }
    }

    private async Task AttachAndHandshakeAsync(TcpClient client, bool outgoing, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        lock (_gate) { _client = client; _stream = stream; }
        if (outgoing)
        {
            await QingTransferProtocol.WriteMessageAsync(stream, new QingTransferProtocol.HelloMessage(_platform, _friendlyName), cancellationToken).ConfigureAwait(false);
            var reply = await QingTransferProtocol.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            if (reply is QingTransferProtocol.RejectMessage) throw new InvalidOperationException("The peer rejected this request.");
            if (reply is not QingTransferProtocol.AcceptMessage) throw new InvalidDataException("The peer sent an invalid response.");
            lock (_gate) _state = QingTransferSessionState.Connected;
            RaiseStateChanged();
        }
        else
        {
            var hello = await QingTransferProtocol.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            if (hello is not QingTransferProtocol.HelloMessage incoming) throw new InvalidDataException("The peer sent an invalid request.");
            lock (_gate) { _state = QingTransferSessionState.WaitingApproval; Peer = new QingTransferPeer("incoming", incoming.Name, incoming.Platform, "1", ["file"], [], 0, true); }
            RaiseStateChanged();
            IncomingRequest?.Invoke(this, incoming);
        }
        _receiveTask = ReceiveUntilClosedAsync(stream, _lifetime.Token);
    }

    private async Task ReceiveUntilClosedAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await QingTransferProtocol.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
                if (message is null) throw new InvalidDataException("The peer sent a malformed message.");
            }
        }
        catch
        {
            if (_disposed) return;
            TcpClient? client;
            lock (_gate)
            {
                client = _client;
                _client = null;
                _stream = null;
                _receiveTask = null;
                Peer = null;
                _state = QingTransferSessionState.Idle;
            }
            try { client?.Dispose(); } catch { }
            RaiseStateChanged();
        }
    }

    private static async Task RejectAndCloseAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try { await using var stream = client.GetStream(); await QingTransferProtocol.WriteMessageAsync(stream, new QingTransferProtocol.RejectMessage(), cancellationToken).ConfigureAwait(false); } catch { }
        client.Dispose();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, State);
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(QingTransferSession)); }
}
