using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.IO;

namespace QingToolbox.Modules.QingTransfer;

public enum QingTransferSessionState
{
    Idle,
    Connecting,
    WaitingApproval,
    Connected,
}

public sealed record QingTransferFileOffer(string Name, long Size);
public sealed record QingTransferProgress(string Name, long Completed, long Total, bool Receiving);

/// <summary>One D1 connection and its single bounded D2 transfer.</summary>
public sealed class QingTransferSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly QingTransferDiscoveryService _discovery;
    private readonly string _friendlyName;
    private readonly string _platform;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _diagnosticDirectory;
    private long _transferSequence;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _receiveTask;
    private QingTransferSessionState _state = QingTransferSessionState.Idle;
    private bool _disposed;
    private bool _transferActive;
    private QingTransferFileOffer? _pendingOffer;
    private TaskCompletionSource<FileDecision>? _incomingDecision;
    private TaskCompletionSource<QingTransferProtocol.Message>? _offerResponse;
    private TaskCompletionSource<bool>? _resultResponse;
    private TaskCompletionSource<bool>? _incomingCompletion;

    private sealed record FileDecision(bool Accepted, string? Destination);

    public QingTransferSession(QingTransferDiscoveryService discovery, string friendlyName, string platform = "windows")
    {
        _discovery = discovery;
        _diagnosticDirectory = discovery.DiagnosticDirectory;
        discovery.IncomingClientHandler = HandleIncomingAsync;
        _friendlyName = friendlyName;
        _platform = platform;
    }

    public event EventHandler<QingTransferSessionState>? StateChanged;
    public event EventHandler<QingTransferProtocol.HelloMessage>? IncomingRequest;
    public event EventHandler<string>? Error;
    public event EventHandler<QingTransferFileOffer>? IncomingFileOffer;
    public event EventHandler<QingTransferProgress>? TransferProgress;
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
            _discovery.ForgetPeer(peer.ServiceName);
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

    public async Task SendFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (State != QingTransferSessionState.Connected || _stream is null) throw new InvalidOperationException("Not connected.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("File not found.", path);
        BeginTransfer();
        var correlation = Interlocked.Increment(ref _transferSequence).ToString("x");
        WriteTransferDiagnostic(correlation, $"offer-send nameLength={info.Name.Length} size={info.Length}");
        try
        {
            var offerResponse = NewMessageCompletion();
            var resultResponse = NewResultCompletion();
            await QingTransferProtocol.WriteMessageAsync(_stream, new QingTransferProtocol.FileOfferMessage(info.Name, info.Length), cancellationToken).ConfigureAwait(false);
            WriteTransferDiagnostic(correlation, "offer-sent");
            var response = await offerResponse.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            WriteTransferDiagnostic(correlation, $"offer-response accepted={response is QingTransferProtocol.FileAcceptMessage}");
            if (response is not QingTransferProtocol.FileAcceptMessage) throw new InvalidOperationException("The peer rejected this file.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024]; long completed = 0; int read;
            WriteTransferDiagnostic(correlation, $"raw-receive-begin declared={info.Length}");
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await _stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                completed += read; TransferProgress?.Invoke(this, new QingTransferProgress(info.Name, completed, info.Length, false));
            }
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            WriteTransferDiagnostic(correlation, $"raw-send-end bytes={completed} declared={info.Length}");
            await QingTransferProtocol.WriteMessageAsync(_stream, new QingTransferProtocol.FileEndMessage(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()), cancellationToken).ConfigureAwait(false);
            WriteTransferDiagnostic(correlation, "file-end-sent");
            if (!await resultResponse.Task.WaitAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("The peer reported a file error.");
            WriteTransferDiagnostic(correlation, "result-received ok=true");
        }
        catch (Exception ex) { WriteTransferDiagnostic(correlation, $"exception type={ex.GetType().Name} hresult=0x{ex.HResult:X8}"); throw; }
        finally { EndTransfer(); }
    }

    public Task AcceptIncomingFileAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_pendingOffer is null || _incomingDecision is null) throw new InvalidOperationException("No incoming file offer.");
            // The destination is chosen by the recipient's SaveFileDialog and may
            // intentionally use a different local filename.  The offered name is
            // still used as the dialog default, while the selected full path is
            // the sole destination controlled by the recipient.
            if (string.IsNullOrWhiteSpace(destinationPath) || string.IsNullOrWhiteSpace(Path.GetFileName(destinationPath)))
                throw new InvalidDataException("A destination file is required.");
            _incomingCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            _incomingDecision.TrySetResult(new FileDecision(true, destinationPath));
            return _incomingCompletion.Task.WaitAsync(cancellationToken);
        }
    }

    public void RejectIncomingFile()
    {
        lock (_gate) _incomingDecision?.TrySetResult(new FileDecision(false, null));
    }

    public async Task DisconnectAsync()
    {
        TcpClient? client; Task? receive;
        lock (_gate)
        {
            client = _client; _client = null; _stream = null; receive = _receiveTask; _receiveTask = null;
            Peer = null; _state = QingTransferSessionState.Idle; _pendingOffer = null;
            _incomingDecision?.TrySetCanceled(); _offerResponse?.TrySetCanceled(); _resultResponse?.TrySetCanceled(); _incomingCompletion?.TrySetCanceled();
        }
        try { client?.Close(); } catch { }
        if (receive is not null) try { await receive.ConfigureAwait(false); } catch { }
        RaiseStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); await DisconnectAsync().ConfigureAwait(false); _lifetime.Dispose();
    }

    internal async Task HandleIncomingAsync(TcpClient client, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state != QingTransferSessionState.Idle) { _ = RejectAndCloseAsync(client, cancellationToken); return; }
            _client = client;
        }
        try { await AttachAndHandshakeAsync(client, outgoing: false, cancellationToken).ConfigureAwait(false); }
        catch { await DisconnectAsync().ConfigureAwait(false); }
    }

    private async Task AttachAndHandshakeAsync(TcpClient client, bool outgoing, CancellationToken cancellationToken)
    {
        var stream = client.GetStream(); lock (_gate) { _client = client; _stream = stream; }
        if (outgoing)
        {
            await QingTransferProtocol.WriteMessageAsync(stream, new QingTransferProtocol.HelloMessage(_platform, _friendlyName), cancellationToken).ConfigureAwait(false);
            var reply = await QingTransferProtocol.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            if (reply is QingTransferProtocol.RejectMessage) throw new InvalidOperationException("The peer rejected this request.");
            if (reply is not QingTransferProtocol.AcceptMessage) throw new InvalidDataException("The peer sent an invalid response.");
            lock (_gate) _state = QingTransferSessionState.Connected; RaiseStateChanged();
        }
        else
        {
            var hello = await QingTransferProtocol.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            if (hello is not QingTransferProtocol.HelloMessage incoming) throw new InvalidDataException("The peer sent an invalid request.");
            lock (_gate) { _state = QingTransferSessionState.WaitingApproval; Peer = new QingTransferPeer("incoming", incoming.Name, incoming.Platform, "1", ["file"], [], 0, true); }
            RaiseStateChanged(); IncomingRequest?.Invoke(this, incoming);
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
                switch (message)
                {
                    case QingTransferProtocol.FileAcceptMessage or QingTransferProtocol.FileRejectMessage:
                        _offerResponse?.TrySetResult(message); break;
                    case QingTransferProtocol.FileResultMessage result:
                        _resultResponse?.TrySetResult(result.Ok); break;
                    case QingTransferProtocol.FileOfferMessage offer:
                        await HandleOfferAsync(stream, offer, cancellationToken).ConfigureAwait(false); break;
                    case QingTransferProtocol.FileEndMessage:
                        throw new InvalidDataException("Unexpected file end.");
                }
            }
        }
        catch
        {
            if (_disposed) return;
            TcpClient? client; lock (_gate) { client = _client; _client = null; _stream = null; _receiveTask = null; Peer = null; _state = QingTransferSessionState.Idle; }
            try { client?.Dispose(); } catch { }
            RaiseStateChanged();
        }
    }

    private async Task HandleOfferAsync(NetworkStream stream, QingTransferProtocol.FileOfferMessage offer, CancellationToken cancellationToken)
    {
        var correlation = Interlocked.Increment(ref _transferSequence).ToString("x");
        WriteTransferDiagnostic(correlation, $"offer-parsed nameLength={offer.Name.Length} size={offer.Size}");
        lock (_gate)
        {
            if (_transferActive || _pendingOffer is not null) { _ = QingTransferProtocol.WriteMessageAsync(stream, new QingTransferProtocol.FileRejectMessage(), cancellationToken); return; }
            _transferActive = true; _pendingOffer = new QingTransferFileOffer(offer.Name, offer.Size); _incomingDecision = new(TaskCreationOptions.RunContinuationsAsynchronously); _incomingCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        IncomingFileOffer?.Invoke(this, _pendingOffer);
        var decision = await _incomingDecision.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        WriteTransferDiagnostic(correlation, $"ui-decision accepted={decision.Accepted} hasDestination={decision.Destination is not null}");
        if (!decision.Accepted || decision.Destination is null) { await QingTransferProtocol.WriteMessageAsync(stream, new QingTransferProtocol.FileRejectMessage(), cancellationToken).ConfigureAwait(false); WriteTransferDiagnostic(correlation, "reject-sent"); EndTransfer(); return; }
        await QingTransferProtocol.WriteMessageAsync(stream, new QingTransferProtocol.FileAcceptMessage(), cancellationToken).ConfigureAwait(false);
        WriteTransferDiagnostic(correlation, "accept-sent");
        var ok = false; string? temp = null;
        try
        {
            var destination = Path.GetFullPath(decision.Destination); var directory = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(directory); if (File.Exists(destination)) throw new IOException("The destination already exists.");
            temp = Path.Combine(directory, "." + offer.Name + ".qingtransfer.part"); if (File.Exists(temp)) File.Delete(temp);
            using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[128 * 1024]; long remaining = offer.Size, completed = 0;
            WriteTransferDiagnostic(correlation, $"raw-receive-begin declared={offer.Size}");
            while (remaining > 0) { var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException(); hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false); remaining -= read; completed += read; TransferProgress?.Invoke(this, new QingTransferProgress(offer.Name, completed, offer.Size, true)); }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false); var end = await QingTransferProtocol.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false); var expected = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            WriteTransferDiagnostic(correlation, $"raw-receive-end bytes={completed} declared={offer.Size} file-end={end is QingTransferProtocol.FileEndMessage}");
            if (end is not QingTransferProtocol.FileEndMessage fileEnd || !string.Equals(expected, fileEnd.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("File hash mismatch.");
            File.Move(temp, destination, false); temp = null; ok = true;
        }
        catch (Exception ex) { WriteTransferDiagnostic(correlation, $"exception type={ex.GetType().Name} hresult=0x{ex.HResult:X8}"); throw; }
        finally { if (temp is not null) try { File.Delete(temp); } catch { } try { await QingTransferProtocol.WriteMessageAsync(stream, new QingTransferProtocol.FileResultMessage(ok), cancellationToken).ConfigureAwait(false); WriteTransferDiagnostic(correlation, $"result-sent ok={ok}"); } catch (Exception ex) { WriteTransferDiagnostic(correlation, $"result-send-exception type={ex.GetType().Name} hresult=0x{ex.HResult:X8}"); throw; } _incomingCompletion?.TrySetResult(ok); EndTransfer(); }
    }

    private TaskCompletionSource<QingTransferProtocol.Message> NewMessageCompletion() { lock (_gate) { return _offerResponse = new(TaskCreationOptions.RunContinuationsAsynchronously); } }
    private TaskCompletionSource<bool> NewResultCompletion() { lock (_gate) { return _resultResponse = new(TaskCreationOptions.RunContinuationsAsynchronously); } }
    private void BeginTransfer() { lock (_gate) { if (_transferActive) throw new InvalidOperationException("A transfer is already active."); _transferActive = true; } }
    private void EndTransfer() { lock (_gate) { _transferActive = false; _pendingOffer = null; _incomingDecision = null; _incomingCompletion = null; _offerResponse = null; _resultResponse = null; } }
    private static async Task RejectAndCloseAsync(TcpClient client, CancellationToken cancellationToken) { try { await using var stream = client.GetStream(); await QingTransferProtocol.WriteMessageAsync(stream, new QingTransferProtocol.RejectMessage(), cancellationToken).ConfigureAwait(false); } catch { } client.Dispose(); }
    private void RaiseStateChanged() => StateChanged?.Invoke(this, State);
    private void WriteTransferDiagnostic(string correlation, string message)
    {
        if (_diagnosticDirectory is null) return;
        try { Directory.CreateDirectory(_diagnosticDirectory); File.AppendAllText(Path.Combine(_diagnosticDirectory, "transfer-debug.log"), $"{DateTimeOffset.UtcNow:O} id={correlation} {message}{Environment.NewLine}"); } catch { }
    }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(QingTransferSession)); }
}
