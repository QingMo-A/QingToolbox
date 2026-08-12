using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using System.Windows.Threading;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Modules.QingTransfer;

public partial class QingTransferView : System.Windows.Controls.UserControl, ILocalizedModuleView, IAsyncDisposable
{
    private readonly QingTransferDiscoveryService _discovery;
    private readonly QingTransferSession _session;
    private readonly QingTransferReceiveSettingsStore _settingsStore;
    private QingTransferReceiveSettings _settings;
    private bool _updatingSettingsUi;
    private readonly ILocalizationService _localization;
    private readonly string _moduleId;
    private readonly ObservableCollection<PeerRow> _rows = [];
    private bool _disposed;

    public QingTransferView(QingTransferDiscoveryService discovery, QingTransferSession session, QingTransferReceiveSettingsStore settingsStore, ILocalizationService localization, string moduleId)
    {
        InitializeComponent();
        _discovery = discovery; _session = session; _settingsStore = settingsStore; _settings = settingsStore.Load(); _localization = localization; _moduleId = moduleId;
        PeersList.ItemsSource = _rows;
        _discovery.PeersChanged += OnPeersChanged;
        _session.StateChanged += OnSessionStateChanged;
        _session.IncomingRequest += OnIncomingRequest;
        _session.IncomingFileOffer += OnIncomingFileOffer;
        _session.Error += OnSessionError;
        _session.StateChanged += OnSessionTransferStateChanged;
        Unloaded += OnUnloaded;
        RefreshLocalization(); UpdatePeers(_discovery.Peers);
    }

    private string T(string key, string fallback) => _localization.GetModuleString(_moduleId, key, fallback);

    public void RefreshLocalization()
    {
        TitleText.Text = T("view.title", "QingTransfer");
        SubtitleText.Text = T("view.subtitle", "Discover nearby QingToolbox devices on this local network.");
        RefreshButtonText.Text = T("actions.refresh", "Refresh");
        SendFileButtonText.Text = T("actions.sendFile", "Send file");
        ReceiveSettingsTitleText.Text = T("receive.title", "Receiving files");
        ChooseDirectoryButtonText.Text = T("receive.chooseDirectory", "Choose folder");
        ClearDirectoryButtonText.Text = T("receive.clearDirectory", "Clear");
        UseDefaultDirectoryText.Text = T("receive.useDefault", "Use default folder");
        AutoAcceptText.Text = T("receive.autoAccept", "Automatically accept files");
        AutoAcceptHintText.Text = T("receive.autoAcceptHint", "Automatic acceptance is used only when the default folder is enabled, configured, and writable.");
        _updatingSettingsUi = true;
        try
        {
            UseDefaultDirectoryCheckBox.IsChecked = _settings.UseDefaultDirectory;
            AutoAcceptCheckBox.IsChecked = _settings.AutoAccept;
        }
        finally { _updatingSettingsUi = false; }
        ReceiveDirectoryText.Text = string.IsNullOrWhiteSpace(_settings.DefaultDirectory) ? T("receive.noDirectory", "No default folder configured") : _settings.DefaultDirectory;
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
        try { await _session.DisconnectAsync(); await _discovery.RestartAsync(); }
        catch (Exception ex) { StatusText.Text = T("status.failed", "Discovery is unavailable: ") + ex.Message; }
        finally { RefreshButton.IsEnabled = true; }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PeerRow row) await _session.ConnectAsync(row.Peer);
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e) => await _session.DisconnectAsync();

    private async void OnSendFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog() == true)
            try { await _session.SendFileAsync(dialog.FileName); }
            catch (Exception ex) { StatusText.Text = T("status.failed", "Transfer failed: ") + ex.Message; }
    }

    private void OnChooseDirectory(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = T("receive.chooseDirectory", "Choose a folder"), UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        _settings = _settings with { DefaultDirectory = dialog.SelectedPath };
        _settingsStore.Save(_settings);
        RefreshLocalization();
    }

    private void OnClearDirectory(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { DefaultDirectory = null, UseDefaultDirectory = false, AutoAccept = false };
        _settingsStore.Save(_settings);
        RefreshLocalization();
    }

    private void OnReceiveSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _updatingSettingsUi) return;
        _settings = _settings with { UseDefaultDirectory = UseDefaultDirectoryCheckBox.IsChecked == true, AutoAccept = AutoAcceptCheckBox.IsChecked == true };
        _settingsStore.Save(_settings);
        RefreshLocalization();
    }

    private async void OnIncomingFileOffer(object? sender, QingTransferFileOffer offer)
    {
        try
        {
            // CommonDialog instances are thread-affine: construct and show the
            // dialog on the WPF dispatcher, then return only its plain path.
            var destination = await Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = offer.Name,
                    AddExtension = false,
                    OverwritePrompt = true,
                };
                return dialog.ShowDialog() == true ? dialog.FileName : null;
            });
            if (string.IsNullOrWhiteSpace(destination)) { _session.RejectIncomingFile(); return; }
            await _session.AcceptIncomingFileAsync(destination);
        }
        catch (Exception ex)
        {
            _session.RejectIncomingFile();
            await Dispatcher.InvokeAsync(() => StatusText.Text = T("status.failed", "Transfer failed: ") + ex.Message);
        }
    }

    private void OnSessionTransferStateChanged(object? sender, QingTransferSessionState state) => Dispatcher.BeginInvoke(UpdateTransferUi);
    private void UpdateTransferUi()
    {
        SendFileButton.Visibility = _session.State == QingTransferSessionState.Connected ? Visibility.Visible : Visibility.Collapsed;
        UpdatePeers(_discovery.Peers);
    }

    private async void OnIncomingRequest(object? sender, QingTransferProtocol.HelloMessage request)
    {
        try
        {
            var accepted = await Dispatcher.InvokeAsync(() =>
            {
                var message = string.Format(T("status.incoming", "Incoming request from {0}. Accept?"), request.Name);
                return System.Windows.MessageBox.Show(message, T("view.title", "QingTransfer"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            });
            if (accepted) await _session.AcceptIncomingAsync(); else await _session.RejectIncomingAsync();
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => StatusText.Text = T("status.failed", "Connection failed: ") + ex.Message);
            await _session.RejectIncomingAsync();
        }
    }

    private void OnSessionStateChanged(object? sender, QingTransferSessionState state) => Dispatcher.BeginInvoke(() => UpdatePeers(_discovery.Peers));
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
        var sessionState = _session.State;
        var activePeer = _session.Peer;
        foreach (var peer in peers) _rows.Add(new PeerRow(peer, online, windows, android, connect, disconnect, unknown, sessionState, activePeer));
        EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeersList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = _discovery.IsRunning ? T("status.searching", "Looking for nearby devices...") : T("status.paused", "Discovery paused.");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _discovery.PeersChanged -= OnPeersChanged; _session.StateChanged -= OnSessionStateChanged;
        _session.IncomingRequest -= OnIncomingRequest; _session.Error -= OnSessionError;
        _session.IncomingFileOffer -= OnIncomingFileOffer;
        _session.StateChanged -= OnSessionTransferStateChanged;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true; _discovery.PeersChanged -= OnPeersChanged; _session.StateChanged -= OnSessionStateChanged;
        _session.IncomingRequest -= OnIncomingRequest; _session.Error -= OnSessionError; Unloaded -= OnUnloaded;
        _session.IncomingFileOffer -= OnIncomingFileOffer;
        _session.StateChanged -= OnSessionTransferStateChanged;
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
        public Visibility ConnectVisibility { get; }
        public Visibility DisconnectVisibility { get; }
        public bool ConnectEnabled { get; }
        public bool DisconnectEnabled { get; }
        public QingTransferPeer Peer { get; }

        public PeerRow(QingTransferPeer peer, string online, string windows, string android, string connect, string disconnect, string unknown, QingTransferSessionState state, QingTransferPeer? activePeer)
        {
            Peer = peer; DisplayName = peer.DisplayName;
            Platform = string.Equals(peer.Platform, "android", StringComparison.OrdinalIgnoreCase) ? android : windows;
            OnlineText = peer.Online ? online : unknown;
            Endpoint = peer.Port == 0 ? unknown : string.Join(", ", peer.Addresses.Select(address => address.ToString())) + $":{peer.Port}";
            ConnectText = connect; DisconnectText = disconnect;
            ConnectEnabled = QingTransferUiState.CanConnect(state, peer);
            DisconnectEnabled = QingTransferUiState.CanDisconnect(state, peer, activePeer);
            ConnectVisibility = ConnectEnabled ? Visibility.Visible : Visibility.Collapsed;
            DisconnectVisibility = DisconnectEnabled ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
