using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Modules.QingTransfer;

public partial class QingTransferView : UserControl, ILocalizedModuleView, IAsyncDisposable
{
    private readonly QingTransferDiscoveryService _discovery;
    private readonly ILocalizationService _localization;
    private readonly string _moduleId;
    private readonly ObservableCollection<PeerRow> _rows = [];
    private bool _disposed;

    public QingTransferView(QingTransferDiscoveryService discovery, ILocalizationService localization, string moduleId)
    {
        InitializeComponent();
        _discovery = discovery; _localization = localization; _moduleId = moduleId;
        PeersList.ItemsSource = _rows;
        _discovery.PeersChanged += OnPeersChanged;
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
        StatusText.Text = _discovery.IsRunning ? T("status.searching", "Looking for nearby devices…") : T("status.paused", "Discovery paused.");
        UpdatePeers(_discovery.Peers);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try { await _discovery.RestartAsync(); }
        catch (Exception ex) { StatusText.Text = T("status.failed", "Discovery is unavailable: ") + ex.Message; }
        finally { RefreshButton.IsEnabled = true; }
    }

    private void OnPeersChanged(object? sender, IReadOnlyList<QingTransferPeer> peers)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => UpdatePeers(peers)); return; }
        UpdatePeers(peers);
    }

    private void UpdatePeers(IReadOnlyList<QingTransferPeer> peers)
    {
        _rows.Clear();
        foreach (var peer in peers) _rows.Add(new PeerRow(peer, T("status.online", "Online"), T("platform.windows", "Windows"), T("platform.android", "Android")));
        EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeersList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = _discovery.IsRunning ? T("status.searching", "Looking for nearby devices…") : T("status.paused", "Discovery paused.");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _discovery.PeersChanged -= OnPeersChanged;

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true; _discovery.PeersChanged -= OnPeersChanged; Unloaded -= OnUnloaded;
        return ValueTask.CompletedTask;
    }

    private sealed class PeerRow
    {
        public string DisplayName { get; }
        public string Platform { get; }
        public string OnlineText { get; }
        public string Endpoint { get; }
        public PeerRow(QingTransferPeer peer, string online, string windows, string android)
        {
            DisplayName = peer.DisplayName;
            Platform = string.Equals(peer.Platform, "android", StringComparison.OrdinalIgnoreCase) ? android : windows;
            OnlineText = peer.Online ? online : "—";
            Endpoint = peer.Port == 0 ? "—" : string.Join(", ", peer.Addresses.Select(address => address.ToString())) + $":{peer.Port}";
        }
    }
}
