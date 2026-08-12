using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Modules.QingTransfer;

public partial class QingTransferView : UserControl, ILocalizedModuleView, IAsyncDisposable
{
    private readonly QingTransferDiscoveryService _discovery;
    private readonly QingTransferSession _session;
    private readonly ILocalizationService _localization;
    private readonly string _moduleId;
    private readonly ObservableCollection<PeerRow> _rows = [];
    private bool _disposed;

    public QingTransferView(QingTransferDiscoveryService discovery, QingTransferSession session, ILocalizationService localization, string moduleId)
    {
        InitializeComponent();
        _discovery = discovery; _session = session; _localization = localization; _moduleId = moduleId;
        PeersList.ItemsSource = _rows;
        _discovery.PeersChanged += OnPeersChanged;
        _session.StateChanged += OnSessionStateChanged;
        _session.IncomingRequest += OnIncomingRequest;
        _session.Error += OnSessionError;
        Unloaded += OnUnloaded;
        RefreshLocalization(); UpdatePeers(_discovery.Peers);
    }

    private string T(string key, string fallback) => _localization.GetModuleString(_moduleId, key, fallback);

    public void RefreshLocalization()
    {
        TitleText.Text = T("view.title", "QingTransfer");
        SubtitleText.Text = T("view.subtitle", "Discover nearby QingToolbox devices on this local network.");
        RefreshButtonText.Text = T("actions.refresh", "Refresh");
        EmptyText.Text = T("view.empty", "No QingToolbox devices are online yet.");
        HintText.Text = T("view.hint", "Discovery uses local DNS-SD. No connection or transfer is started here.");
        FooterText.Text = T("view.footer", "Nearby devices");
        DeviceColumn.Header = T("columns.device", "Device");
        PlatformColumn.Header = T("columns.platform", "Platform");
        StatusColumn.Header = T("columns.status", "Status");
        EndpointColumn.Header = T("columns.endpoint", "Endpoint");
        ActionColumn.Header = T("columns.action", "Action");
        StatusText.Text = _discovery.IsRunning ? T("status.searching", "Looking for nearby devices...") : T("status.paused", "Discovery paused.");
        UpdatePeers(_discovery.Peers);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try { await _discovery.RestartAsync(); }
        catch (Exception ex) { StatusText.Text = T("status.failed", "Discovery is unavailable: ") + ex.Message; }
        finally { RefreshButton.IsEnabled = true; }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PeerRow row) await _session.ConnectAsync(row.Peer);
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e) => await _session.DisconnectAsync();

    private async void OnIncomingRequest(object? sender, QingTransferProtocol.HelloMessage request)
    {
        var message = string.Format(T("status.incoming", "Incoming request from {0}. Accept?"), request.Name);
        var result = MessageBox.Show(message, T("view.title", "QingTransfer"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes) await _session.AcceptIncomingAsync(); else await _session.RejectIncomingAsync();
    }

    private void OnSessionStateChanged(object? sender, QingTransferSessionState state) => Dispatcher.BeginInvoke(UpdateSessionButtons);
    private void OnSessionError(object? sender, string error) => Dispatcher.BeginInvoke(() => StatusText.Text = T("status.failed", "Connection failed: ") + error);
    private void UpdateSessionButtons() => PeersList.Items.Refresh();

    private void OnPeersChanged(object? sender, IReadOnlyList<QingTransferPeer> peers)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => UpdatePeers(peers)); return; }
        UpdatePeers(peers);
    }

    private void UpdatePeers(IReadOnlyList<QingTransferPeer> peers)
    {
        _rows.Clear();
        var online = T("status.online", "Online");
        var windows = T("platform.windows", "Windows");
        var android = T("platform.android", "Android");
        var connect = T("actions.connect", "Connect");
        var disconnect = T("actions.disconnect", "Disconnect");
        var unknown = T("status.unknown", "—");
        foreach (var peer in peers) _rows.Add(new PeerRow(peer, online, windows, android, connect, disconnect, unknown));
        EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeersList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = _discovery.IsRunning ? T("status.searching", "Looking for nearby devices...") : T("status.paused", "Discovery paused.");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _discovery.PeersChanged -= OnPeersChanged; _session.StateChanged -= OnSessionStateChanged;
        _session.IncomingRequest -= OnIncomingRequest; _session.Error -= OnSessionError;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true; _discovery.PeersChanged -= OnPeersChanged; _session.StateChanged -= OnSessionStateChanged;
        _session.IncomingRequest -= OnIncomingRequest; _session.Error -= OnSessionError; Unloaded -= OnUnloaded;
        return ValueTask.CompletedTask;
    }

    private sealed class PeerRow
    {
        public string DisplayName { get; }
        public string Platform { get; }
        public string OnlineText { get; }
        public string Endpoint { get; }
        public string ConnectText { get; }
        public string DisconnectText { get; }
        public QingTransferPeer Peer { get; }

        public PeerRow(QingTransferPeer peer, string online, string windows, string android, string connect, string disconnect, string unknown)
        {
            Peer = peer; DisplayName = peer.DisplayName;
            Platform = string.Equals(peer.Platform, "android", StringComparison.OrdinalIgnoreCase) ? android : windows;
            OnlineText = peer.Online ? online : unknown;
            Endpoint = peer.Port == 0 ? unknown : string.Join(", ", peer.Addresses.Select(address => address.ToString())) + $":{peer.Port}";
            ConnectText = connect; DisconnectText = disconnect;
        }
    }
}
