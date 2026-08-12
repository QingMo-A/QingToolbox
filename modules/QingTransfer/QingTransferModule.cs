using System.Text.Json;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.QingTransfer;

/// <summary>
/// Web-module backend for QingTransfer.  The Web UI only sends named commands;
/// native file/folder dialogs and all protocol/session work remain here.
/// </summary>
public sealed class QingTransferModule : IWebToolModule
{
    private readonly object _gate = new();
    private QingTransferDiscoveryService? _discovery;
    private QingTransferSession? _session;
    private QingTransferReceiveSettingsStore? _receiveSettings;
    private QingTransferReceiveSettings _receiveSettingsSnapshot = new();
    private ModuleContext? _context;
    private QingTransferProtocol.HelloMessage? _incomingRequest;
    private QingTransferFileOffer? _incomingOffer;
    private QingTransferProgress? _progress;
    private string? _lastError;
    private DateTimeOffset _lastProgressPublish = DateTimeOffset.MinValue;
    private CancellationTokenSource? _progressThrottle;
    private bool _disposed;

    public string Id => "qing.qingtransfer";
    public string Name => "QingTransfer";
    public string Description => "Transfer files between QingToolbox devices on the local network.";
    public event EventHandler<ModuleWebEventArgs>? WebEvent;

    public Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        if (_context is not null) return Task.CompletedTask;
        _context = context;
        _receiveSettings = new QingTransferReceiveSettingsStore(context.DataDirectory);
        _receiveSettingsSnapshot = _receiveSettings.Load();
        _discovery = new QingTransferDiscoveryService(Environment.MachineName, context.DataDirectory);
        _session = new QingTransferSession(_discovery, Environment.MachineName, receiveSettings: _receiveSettings);
        _discovery.PeersChanged += OnPeersChanged;
        _session.StateChanged += OnSessionStateChanged;
        _session.IncomingRequest += OnIncomingRequest;
        _session.IncomingFileOffer += OnIncomingFileOffer;
        _session.TransferProgress += OnTransferProgress;
        _session.TransferEnded += OnTransferEnded;
        _session.Error += OnSessionError;
        return Task.CompletedTask;
    }

    public async Task OnActivateAsync(CancellationToken cancellationToken = default)
    {
        await (_discovery?.StartAsync(cancellationToken) ?? Task.CompletedTask).ConfigureAwait(false);
        PublishState();
    }

    public async Task OnDeactivateAsync(CancellationToken cancellationToken = default)
    {
        if (_discovery is not null) await _discovery.StopAsync().ConfigureAwait(false);
        PublishState();
    }

    public async Task<JsonElement?> HandleWebRequestAsync(string method, JsonElement? payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QingTransferSession session = _session ?? throw new InvalidOperationException("Module is not loaded.");
        QingTransferDiscoveryService discovery = _discovery ?? throw new InvalidOperationException("Module is not loaded.");
        switch (method)
        {
            case "getState": return Snapshot();
            case "refresh":
                await session.DisconnectAsync().ConfigureAwait(false);
                await discovery.RestartAsync(cancellationToken).ConfigureAwait(false);
                PublishState();
                return Snapshot();
            case "connect":
                var serviceName = RequiredString(payload, "serviceName");
                var peer = discovery.Peers.FirstOrDefault(x =>
                    string.Equals(x.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
                if (peer is null) throw new InvalidOperationException("The selected device is no longer online.");
                await session.ConnectAsync(peer, cancellationToken).ConfigureAwait(false);
                return Snapshot();
            case "disconnect":
                await session.DisconnectAsync().ConfigureAwait(false);
                PublishState();
                return Snapshot();
            case "chooseAndSendFile":
                await ChooseAndSendFileAsync(session, cancellationToken).ConfigureAwait(false);
                return Snapshot();
            case "chooseReceiveDirectory":
                ChooseReceiveDirectory();
                return Snapshot();
            case "clearReceiveDirectory":
                SaveReceiveSettings(new QingTransferReceiveSettings());
                return Snapshot();
            case "setReceivePreferences":
                var settings = CurrentSettings();
                SaveReceiveSettings(settings with
                {
                    UseDefaultDirectory = OptionalBool(payload, "useDefaultDirectory", settings.UseDefaultDirectory),
                    AutoAccept = OptionalBool(payload, "autoAccept", settings.AutoAccept),
                });
                return Snapshot();
            case "acceptIncomingConnection":
                await session.AcceptIncomingAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate) _incomingRequest = null;
                PublishState();
                return Snapshot();
            case "rejectIncomingConnection":
                await session.RejectIncomingAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate) _incomingRequest = null;
                PublishState();
                return Snapshot();
            case "acceptIncomingFile":
                await AcceptIncomingFileWithDialogAsync(session, cancellationToken).ConfigureAwait(false);
                return Snapshot();
            case "rejectIncomingFile":
                session.RejectIncomingFile();
                lock (_gate) _incomingOffer = null;
                PublishState();
                return Snapshot();
            case "dismissError":
                lock (_gate) _lastError = null;
                PublishState();
                return Snapshot();
            default: throw new InvalidOperationException("Unknown QingTransfer method.");
        }
    }

    public async Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        if (_context is null) return;
        if (_discovery is not null) _discovery.PeersChanged -= OnPeersChanged;
        if (_session is not null)
        {
            _session.StateChanged -= OnSessionStateChanged;
            _session.IncomingRequest -= OnIncomingRequest;
            _session.IncomingFileOffer -= OnIncomingFileOffer;
            _session.TransferProgress -= OnTransferProgress;
            _session.TransferEnded -= OnTransferEnded;
            _session.Error -= OnSessionError;
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        if (_discovery is not null) await _discovery.DisposeAsync().ConfigureAwait(false);
        _progressThrottle?.Cancel();
        _progressThrottle?.Dispose();
        _progressThrottle = null;
        _session = null; _discovery = null; _receiveSettings = null; _context = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await OnUnloadAsync().ConfigureAwait(false);
    }

    private async Task ChooseAndSendFileAsync(QingTransferSession session, CancellationToken cancellationToken)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;
        await session.SendFileAsync(dialog.FileName, cancellationToken).ConfigureAwait(false);
        PublishState();
    }

    private void ChooseReceiveDirectory()
    {
        var current = CurrentSettings();
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = current.DefaultDirectory ?? string.Empty,
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        SaveReceiveSettings(current with { DefaultDirectory = dialog.SelectedPath });
    }

    private async Task AcceptIncomingFileWithDialogAsync(QingTransferSession session, CancellationToken cancellationToken)
    {
        QingTransferFileOffer offer;
        lock (_gate) offer = _incomingOffer ?? throw new InvalidOperationException("No incoming file offer.");
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = offer.Name, AddExtension = false, OverwritePrompt = true };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            session.RejectIncomingFile();
            lock (_gate) _incomingOffer = null;
            PublishState();
            return;
        }
        // The Save dialog is the user's decision boundary.  Clear the offer
        // before awaiting the potentially long receive operation so the Web UI
        // immediately switches from the decision modal to transfer progress.
        lock (_gate) _incomingOffer = null;
        PublishState();
        await session.AcceptIncomingFileAsync(dialog.FileName, cancellationToken).ConfigureAwait(false);
    }

    private QingTransferReceiveSettings CurrentSettings()
    {
        lock (_gate) return _receiveSettingsSnapshot;
    }

    private void SaveReceiveSettings(QingTransferReceiveSettings value)
    {
        _receiveSettings?.Save(value);
        lock (_gate) _receiveSettingsSnapshot = value;
        PublishState();
    }

    private void OnPeersChanged(object? sender, IReadOnlyList<QingTransferPeer> peers) => PublishState();
    private void OnSessionStateChanged(object? sender, QingTransferSessionState state)
    {
        if (state == QingTransferSessionState.Idle)
        {
            lock (_gate) { _incomingRequest = null; _incomingOffer = null; _progress = null; }
        }
        PublishState();
    }
    private void OnTransferProgress(object? sender, QingTransferProgress progress)
    {
        var publishNow = false;
        CancellationTokenSource? scheduled = null;
        lock (_gate)
        {
            _progress = progress;
            var now = DateTimeOffset.UtcNow;
            if (now - _lastProgressPublish >= TimeSpan.FromMilliseconds(150))
            {
                _lastProgressPublish = now;
                publishNow = true;
            }
            else if (_progressThrottle is null)
            {
                _progressThrottle = new CancellationTokenSource();
                scheduled = _progressThrottle;
            }
        }
        if (publishNow) PublishState();
        if (scheduled is not null) _ = PublishProgressAfterDelayAsync(scheduled);
    }

    private async Task PublishProgressAfterDelayAsync(CancellationTokenSource scheduled)
    {
        try
        {
            await Task.Delay(150, scheduled.Token).ConfigureAwait(false);
            lock (_gate) _lastProgressPublish = DateTimeOffset.UtcNow;
            PublishState();
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_progressThrottle, scheduled))
                {
                    _progressThrottle.Dispose();
                    _progressThrottle = null;
                }
            }
        }
    }

    private void OnTransferEnded(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _progress = null;
            _lastProgressPublish = DateTimeOffset.MinValue;
        }
        _progressThrottle?.Cancel();
        PublishState();
    }
    private void OnSessionError(object? sender, string error) { lock (_gate) _lastError = error; PublishState(); }
    private void OnIncomingRequest(object? sender, QingTransferProtocol.HelloMessage request) { lock (_gate) _incomingRequest = request; PublishState(); }
    private void OnIncomingFileOffer(object? sender, QingTransferFileOffer offer) { lock (_gate) _incomingOffer = offer; PublishState(); }

    private void PublishState()
    {
        var payload = Snapshot();
        WebEvent?.Invoke(this, new ModuleWebEventArgs("stateChanged", payload));
    }

    private JsonElement Snapshot()
    {
        var discovery = _discovery;
        var session = _session;
        QingTransferProtocol.HelloMessage? request; QingTransferFileOffer? offer; QingTransferProgress? progress; string? error;
        lock (_gate) { request = _incomingRequest; offer = _incomingOffer; progress = _progress; error = _lastError; }
        var settings = CurrentSettings();
        var peers = (discovery?.Peers ?? []).Select(peer => new
        {
            serviceName = peer.ServiceName,
            displayName = peer.DisplayName,
            platform = peer.Platform,
            protocolVersion = peer.ProtocolVersion,
            capabilities = peer.Capabilities,
            addresses = peer.Addresses.Select(address => address.ToString()).ToArray(),
            port = peer.Port,
            online = peer.Online,
            lastSeen = peer.LastSeen,
        }).ToArray();
        var activePeer = session?.Peer is { } active ? new
        {
            serviceName = active.ServiceName,
            displayName = active.DisplayName,
            platform = active.Platform,
            protocolVersion = active.ProtocolVersion,
            capabilities = active.Capabilities,
            addresses = active.Addresses.Select(address => address.ToString()).ToArray(),
            port = active.Port,
            online = active.Online,
            lastSeen = active.LastSeen,
        } : null;
        return JsonSerializer.SerializeToElement(new
        {
            discovery = new { running = discovery?.IsRunning == true, peers },
            session = new { state = session?.State.ToString() ?? QingTransferSessionState.Idle.ToString(), peer = activePeer },
            incomingConnection = request is null ? null : new { platform = request.Platform, name = request.Name },
            incomingFile = offer is null ? null : new { name = offer.Name, size = offer.Size },
            receive = new { defaultDirectory = settings.DefaultDirectory, useDefaultDirectory = settings.UseDefaultDirectory, autoAccept = settings.AutoAccept },
            transfer = progress is null ? null : new { name = progress.Name, completed = progress.Completed, total = progress.Total, receiving = progress.Receiving },
            lastError = error,
        });
    }

    private static string RequiredString(JsonElement? payload, string name)
    {
        if (payload is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            return item.GetString()!;
        throw new ArgumentException($"Missing {name}.");
    }

    private static bool OptionalBool(JsonElement? payload, string name, bool fallback) =>
        payload is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? item.GetBoolean() : fallback;
}
