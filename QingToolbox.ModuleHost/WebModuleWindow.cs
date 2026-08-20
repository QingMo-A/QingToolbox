using System.Text.Json;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Settings;

namespace QingToolbox.ModuleHost;

/// <summary>Small, isolated WebView2 host for a module's local Web entry.</summary>
internal sealed class WebModuleWindow : Window
{
    private const int MaximumPendingLiveEvents = 256;
    private const int MaximumEventsPerDispatch = 32;
    private const int BackgroundSuspendDelayMilliseconds = 1000;

    private readonly string _moduleId;
    private readonly string _version;
    private readonly string _host;
    private readonly string _moduleRoot;
    private readonly string _entry;
    private readonly string _userDataFolder;
    private readonly ModuleHostWindowPresentationMode _presentationMode;
    // Composition hosting keeps the WPF visual in the routed input tree so the
    // host can receive an OS FileDrop without inspecting a child HWND.
    private readonly WebView2CompositionControl _browser = new();
    private readonly Grid _surface = new();
    private readonly TextBlock _status = new();
    private readonly string? _iconPath;
    private readonly WebModuleBridgeDispatcher? _bridge;
    private readonly Action? _onClosed;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task>? _externalDropHandler;
    private readonly DragEventHandler? _externalDragOverHandler = null;
    private readonly DragEventHandler? _externalDragEnterHandler = null;
    private readonly DragEventHandler? _externalDragLeaveHandler = null;
    private readonly DragEventHandler? _externalDropEventHandler = null;
    private readonly DispatcherTimer? _deferredDismissTimer;
    private readonly object _eventGate = new();
    private readonly Queue<ModuleWebEventArgs> _pendingLiveEvents = new();
    private readonly CoalescedWebEventBuffer _deferredEvents = new(64);
    private readonly SemaphoreSlim _webViewLifecycleGate = new(1, 1);
    private HwndSource? _windowSource;
    private string _appearancePresetId;
    private string _languageCode;
    private bool _readySent;
    private bool _presentationReady;
    private bool _presentationFallbackSent;
    private bool _closed;
    private volatile bool _backgroundSuspended;
    private bool _eventDispatchScheduled;
    private bool _presentationDirty;
    private int _lifecycleGeneration;
    private CancellationTokenSource? _suspendDelayCancellation;
    private bool _externalFileDragActive;
    private long _lastExternalDropTick = long.MinValue;
    private readonly string _presentationNonce = Guid.NewGuid().ToString("N");

    public WebModuleWindow(string moduleId, string version, string moduleRoot, string entry,
        string dataRoot, string title, Window? owner, WebModuleBridgeDispatcher? bridge = null,
        string appearancePresetId = AppearancePresetIds.QingDefault, string languageCode = "en-US", string? iconPath = null,
        Action? onClosed = null,
        Func<IReadOnlyList<string>, CancellationToken, Task>? externalDropHandler = null,
        ModuleHostWindowPresentationMode presentationMode = ModuleHostWindowPresentationMode.Standard)
    {
        _moduleId = moduleId;
        _version = version;
        _moduleRoot = Path.GetFullPath(moduleRoot);
        _entry = entry.Replace('\\', '/').TrimStart('/');
        _userDataFolder = Path.Combine(Path.GetFullPath(dataRoot), moduleId, "webview2");
        _presentationMode = Enum.IsDefined(presentationMode) ? presentationMode : ModuleHostWindowPresentationMode.Standard;
        _bridge = bridge;
        _onClosed = onClosed;
        _externalDropHandler = externalDropHandler;
        _appearancePresetId = AppearancePresetIds.Normalize(appearancePresetId);
        _languageCode = languageCode is "en-US" or "zh-CN" ? languageCode : "en-US";
        _iconPath = iconPath;
        _host = "qing-module.local";
        Title = title;
        WebModuleWindowPresentation.Apply(this, _presentationMode);
        Owner = owner;
        _browser.Visibility = Visibility.Hidden;
        _browser.IsHitTestVisible = false;
        // Keep WebView's own drop surface disabled; routed WPF events on the
        // composition visual are the only path forwarded to the optional sink.
        _browser.AllowExternalDrop = false;
        if (_externalDropHandler is not null)
        {
            AllowDrop = true;
            _externalDragEnterHandler = OnExternalDragEnter;
            _externalDragOverHandler = OnExternalDragOver;
            _externalDragLeaveHandler = OnExternalDragLeave;
            _externalDropEventHandler = OnExternalDrop;
            AddHandler(DragDrop.DragEnterEvent, _externalDragEnterHandler, true);
            AddHandler(DragDrop.DragOverEvent, _externalDragOverHandler, true);
            AddHandler(DragDrop.DragLeaveEvent, _externalDragLeaveHandler, true);
            AddHandler(DragDrop.DropEvent, _externalDropEventHandler, true);
        }
        if (WebModuleWindowPresentation.IsOverlay(_presentationMode))
        {
            _deferredDismissTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(40),
            };
            _deferredDismissTimer.Tick += OnDeferredDismissTick;
            Deactivated += OnOverlayDeactivated;
            SourceInitialized += OnOverlaySourceInitialized;
            KeyDown += OnOverlayKeyDown;
            KeyUp += OnOverlayKeyUp;
        }
        IsVisibleChanged += OnVisibilityChanged;
        ApplySurfaceTheme();
        _surface.Children.Add(_browser);
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (_iconPath is not null)
        {
            try
            {
                stack.Children.Add(new SvgViewbox { Width = 54, Height = 54, Source = new Uri(_iconPath, UriKind.Absolute), HorizontalAlignment = HorizontalAlignment.Center });
                var drawing = new FileSvgReader(new WpfDrawingSettings { IncludeRuntime = true }, false).Read(_iconPath);
                if (drawing is not null) Icon = new DrawingImage(drawing);
            }
            catch { }
        }
        stack.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(16, 33, 61)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) });
        _status.Text = "Preparing workspace...";
        _status.FontSize = 13;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(80, 99, 126));
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.Margin = new Thickness(0, 7, 0, 0);
        stack.Children.Add(_status);
        var ring = new ProgressBar { Width = 120, Height = 4, IsIndeterminate = SystemParameters.ClientAreaAnimation, Value = 50, Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(ring);
        _surface.Children.Add(stack);
        Content = _surface;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void ApplySurfaceTheme()
    {
        if (_presentationMode == ModuleHostWindowPresentationMode.Overlay)
        {
            _surface.Background = Brushes.Transparent;
            return;
        }
        var dark = _appearancePresetId is not AppearancePresetIds.QingDefault;
        _surface.Background = new SolidColorBrush(dark ? Color.FromRgb(8, 15, 37) : Color.FromRgb(243, 247, 253));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            if (_presentationMode == ModuleHostWindowPresentationMode.Overlay)
            {
                _browser.Background = Brushes.Transparent;
                _browser.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            }
            await _browser.EnsureCoreWebView2Async(environment);
            var core = _browser.CoreWebView2;
            // Do not gate the host surface on requestAnimationFrame: WebView2 starts hidden while
            // the native loading surface is shown, and a hidden controller is allowed to defer
            // frame callbacks. A zero-delay task after DOMContentLoaded is a deterministic
            // page-ready signal without changing the hostReady bridge contract.
            await core.AddScriptToExecuteOnDocumentCreatedAsync($"(() => {{ const n={JsonSerializer.Serialize(_presentationNonce)}; const signal=()=>setTimeout(()=>chrome.webview.postMessage({{type:'qing-internal-presentation-ready',nonce:n}}),0); const ready=()=>Promise.resolve().then(signal); if(document.readyState==='loading') document.addEventListener('DOMContentLoaded',ready,{{once:true}}); else ready(); }})();");
            core.Settings.AreDevToolsEnabled = false;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;
            core.SetVirtualHostNameToFolderMapping(
                _host,
                _moduleRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            // Keep each window's local entry URL unique so a reused WebView2
            // profile cannot replay a stale cached index.html after a module
            // update. The query is host-only and never reaches the bridge.
            core.Navigate($"https://{_host}/{EncodeEntry(_entry)}?qingHostSession={_presentationNonce}");
            if (_backgroundSuspended && _suspendDelayCancellation is null)
                await TrySuspendWebViewAsync(_lifecycleGeneration);
        }
        catch (Exception exception)
        {
            // Keep the host process alive and leave a clear, host-owned failure surface.
            Title = $"{Title} - Web module failed to initialize";
            System.Diagnostics.Debug.WriteLine($"Web module '{_moduleId}' failed to initialize: {exception.GetType().Name}");
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) =>
        e.Handled = true;

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, _host, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _browser.CoreWebView2 is null) { _status.Text = "Unable to load module UI."; return; }
        if (_readySent || _browser.CoreWebView2 is null) return;
        _readySent = true;
        var message = JsonSerializer.Serialize(new
        {
            type = "hostReady",
            protocolVersion = 1,
            moduleId = _moduleId,
            version = _version,
            bridge = true,
            appearancePresetId = _appearancePresetId,
            languageCode = _languageCode
        });
        _browser.CoreWebView2.PostWebMessageAsJson(message);
        _presentationDirty = false;
        SchedulePendingEventsIfNeeded();
        // Keep a host-owned fallback for pages that replace the document or suppress the
        // document-created callback. This is the same nonce- and origin-checked presentation
        // signal, and is intentionally separate from the hostReady protocol message.
        if (!_presentationFallbackSent)
        {
            _presentationFallbackSent = true;
            _ = PostPresentationReadyFallbackAsync(_browser.CoreWebView2);
        }
    }

    private async Task PostPresentationReadyFallbackAsync(CoreWebView2 core)
    {
        try
        {
            var nonce = JsonSerializer.Serialize(_presentationNonce);
            await core.ExecuteScriptAsync($"(() => {{ const n={nonce}; Promise.resolve().then(() => setTimeout(() => chrome.webview.postMessage({{type:'qing-internal-presentation-ready',nonce:n}}),0)); }})();");
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Web module '{_moduleId}' presentation fallback unavailable: {exception.GetType().Name}");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        lock (_eventGate)
        {
            _closed = true;
            _backgroundSuspended = true;
            _lifecycleGeneration++;
            _pendingLiveEvents.Clear();
            _deferredEvents.Clear();
            _eventDispatchScheduled = false;
        }
        CancelPendingSuspendDelay();
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        Deactivated -= OnOverlayDeactivated;
        IsVisibleChanged -= OnVisibilityChanged;
        SourceInitialized -= OnOverlaySourceInitialized;
        KeyDown -= OnOverlayKeyDown;
        KeyUp -= OnOverlayKeyUp;
        _windowSource?.RemoveHook(OnOverlayWindowMessage);
        _windowSource = null;
        StopDeferredDismiss();
        if (_deferredDismissTimer is not null) _deferredDismissTimer.Tick -= OnDeferredDismissTick;
        if (_externalDropHandler is not null)
        {
            if (_externalDragEnterHandler is not null) RemoveHandler(DragDrop.DragEnterEvent, _externalDragEnterHandler);
            if (_externalDragOverHandler is not null) RemoveHandler(DragDrop.DragOverEvent, _externalDragOverHandler);
            if (_externalDragLeaveHandler is not null) RemoveHandler(DragDrop.DragLeaveEvent, _externalDragLeaveHandler);
            if (_externalDropEventHandler is not null) RemoveHandler(DragDrop.DropEvent, _externalDropEventHandler);
        }
        if (_browser.CoreWebView2 is { } core)
        {
            core.NewWindowRequested -= OnNewWindowRequested;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.WebMessageReceived -= OnWebMessageReceived;
        }
        _browser.Dispose();
        _onClosed?.Invoke();
    }

    private void OnOverlaySourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(OnOverlayWindowMessage);
    }

    private IntPtr OnOverlayWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (ShouldSuppressSystemMenu(_presentationMode, message, wParam.ToInt64())) handled = true;
        return IntPtr.Zero;
    }

    internal static bool ShouldSuppressSystemMenu(ModuleHostWindowPresentationMode mode, int message, long wParam)
    {
        const int WmSysCommand = 0x0112;
        const long ScKeyMenu = 0xF100;
        return WebModuleWindowPresentation.IsOverlay(mode) && message == WmSysCommand && (wParam & 0xFFF0) == ScKeyMenu;
    }

    private void OnOverlayKeyDown(object sender, KeyEventArgs e) => ForwardOverlayAltSpace(e, "keydown");

    private void OnOverlayKeyUp(object sender, KeyEventArgs e) => ForwardOverlayAltSpace(e, "keyup");

    private async void ForwardOverlayAltSpace(KeyEventArgs e, string eventType)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!ShouldForwardAltSpace(_presentationMode, key == Key.Space ? 0x20u : 0u, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))) return;
        e.Handled = true;
        try
        {
            await _browser.CoreWebView2.ExecuteScriptAsync(
                $"window.dispatchEvent(new KeyboardEvent('{eventType}',{{key:' ',code:'Space',altKey:true,bubbles:true,cancelable:true}}));");
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Web module '{_moduleId}' Alt+Space forwarding unavailable: {exception.GetType().Name}");
        }
    }

    internal static bool ShouldForwardAltSpace(ModuleHostWindowPresentationMode mode, uint virtualKey, bool menuKeyDown) =>
        WebModuleWindowPresentation.IsOverlay(mode) && virtualKey == 0x20 && menuKeyDown;

    private void OnOverlayDeactivated(object? sender, EventArgs e)
    {
        var decision = OverlayDismissPolicy.OnDeactivated(
            _presentationMode, _closed, IsVisible, _externalFileDragActive, IsLeftMouseButtonDown());
        if (decision == OverlayDismissDecision.Hide) Hide();
        else if (decision == OverlayDismissDecision.Defer) _deferredDismissTimer?.Start();
    }

    private void OnDeferredDismissTick(object? sender, EventArgs e)
    {
        var elapsed = _lastExternalDropTick == long.MinValue
            ? long.MaxValue
            : Math.Max(0, Environment.TickCount64 - _lastExternalDropTick);
        var decision = OverlayDismissPolicy.OnDeferredTick(
            _closed, IsVisible, IsActive, _externalFileDragActive, IsLeftMouseButtonDown(), elapsed);
        if (decision == OverlayDismissDecision.Defer) return;
        StopDeferredDismiss();
        if (decision == OverlayDismissDecision.Hide) Hide();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            StopDeferredDismiss();
            _ = SuspendForBackgroundAsync();
            return;
        }

        ResumeFromBackground();
    }

    private void StopDeferredDismiss() => _deferredDismissTimer?.Stop();

    private static bool IsLeftMouseButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private void OnExternalDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) _externalFileDragActive = true;
    }

    private void OnExternalDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            _externalFileDragActive = true;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnExternalDragLeave(object sender, DragEventArgs e) => _externalFileDragActive = false;

    private void OnExternalDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _externalFileDragActive = false;
        if (_closed || _externalDropHandler is null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        _lastExternalDropTick = Environment.TickCount64;
        _ = ForwardExternalDropAsync(paths);
    }

    private async Task ForwardExternalDropAsync(IReadOnlyList<string> paths)
    {
        try
        {
            await _externalDropHandler!(paths, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Web module '{_moduleId}' external drop failed: {exception.GetType().Name}");
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args) =>
        await HandleWebMessageAsync(args, _bridge);

    private async Task HandleWebMessageAsync(CoreWebView2WebMessageReceivedEventArgs args,
        WebModuleBridgeDispatcher? bridge)
    {
        if (TryPresentationReady(args)) return;
        if (bridge is null || _browser.CoreWebView2 is null) return;
        var response = await bridge.DispatchAsync(args.WebMessageAsJson, CancellationToken.None);
        if (response is not null) _browser.CoreWebView2.PostWebMessageAsJson(response);
    }

    private bool TryPresentationReady(CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source) ||
            !string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Host, _host, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var json = JsonDocument.Parse(args.WebMessageAsJson);
            var root = json.RootElement;
            if (root.GetProperty("type").GetString() != "qing-internal-presentation-ready" ||
                root.GetProperty("nonce").GetString() != _presentationNonce) return false;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed || _presentationReady || _surface.Children.Count < 2) return;
                _presentationReady = true;
                if (!_backgroundSuspended)
                {
                    _browser.Visibility = Visibility.Visible;
                    _browser.IsHitTestVisible = true;
                }
                _surface.Children.RemoveAt(1);
            }));
            return true;
        }
        catch (JsonException) { return false; }
        catch (KeyNotFoundException) { return false; }
    }

    public void PostEvent(ModuleWebEventArgs eventArgs)
    {
        var scheduleDispatch = false;
        lock (_eventGate)
        {
            if (_closed) return;
            if (_backgroundSuspended)
            {
                _deferredEvents.Add(eventArgs);
                return;
            }

            if (_pendingLiveEvents.Count >= MaximumPendingLiveEvents) _pendingLiveEvents.Dequeue();
            _pendingLiveEvents.Enqueue(eventArgs);
            if (!_eventDispatchScheduled)
            {
                _eventDispatchScheduled = true;
                scheduleDispatch = true;
            }
        }

        if (scheduleDispatch) ScheduleEventDispatch();
    }

    public void ApplyPresentation(string appearancePresetId, string languageCode)
    {
        _appearancePresetId = AppearancePresetIds.Normalize(appearancePresetId);
        _languageCode = languageCode is "en-US" or "zh-CN" ? languageCode : "en-US";
        if (_backgroundSuspended)
        {
            _presentationDirty = true;
            return;
        }
        if (!_readySent || _browser.CoreWebView2 is null) return;
        PostPresentationChanged(_browser.CoreWebView2);
    }

    public Task SuspendForBackgroundAsync(bool immediate = false)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(() => SuspendForBackgroundAsync(immediate)).Task.Unwrap();

        int generation;
        lock (_eventGate)
        {
            if (_closed || (_backgroundSuspended && !immediate)) return Task.CompletedTask;
            _backgroundSuspended = true;
            generation = ++_lifecycleGeneration;
            while (_pendingLiveEvents.TryDequeue(out var eventArgs)) _deferredEvents.Add(eventArgs);
        }
        CancelPendingSuspendDelay();
        if (IsVisible) Hide();
        _browser.IsHitTestVisible = false;
        _browser.Visibility = Visibility.Hidden;
        if (immediate) return TrySuspendWebViewAsync(generation);

        var delayCancellation = new CancellationTokenSource();
        _suspendDelayCancellation = delayCancellation;
        return SuspendAfterDelayAsync(generation, delayCancellation);
    }

    public void ResumeFromBackground()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ResumeFromBackground));
            return;
        }
        if (_closed) return;

        lock (_eventGate)
        {
            _backgroundSuspended = false;
            _lifecycleGeneration++;
        }
        CancelPendingSuspendDelay();
        TryResumeWebView();
        if (_presentationReady)
        {
            _browser.Visibility = Visibility.Visible;
            _browser.IsHitTestVisible = true;
        }
        if (_presentationDirty && _readySent && _browser.CoreWebView2 is { } core)
        {
            _presentationDirty = false;
            PostPresentationChanged(core);
        }

        var scheduleDispatch = false;
        lock (_eventGate)
        {
            foreach (var eventArgs in _deferredEvents.Drain())
            {
                if (_pendingLiveEvents.Count >= MaximumPendingLiveEvents) _pendingLiveEvents.Dequeue();
                _pendingLiveEvents.Enqueue(eventArgs);
            }
            if (_pendingLiveEvents.Count > 0 && !_eventDispatchScheduled)
            {
                _eventDispatchScheduled = true;
                scheduleDispatch = true;
            }
        }
        if (scheduleDispatch) ScheduleEventDispatch();
    }

    private async Task SuspendAfterDelayAsync(int generation, CancellationTokenSource delayCancellation)
    {
        try
        {
            await Task.Delay(BackgroundSuspendDelayMilliseconds, delayCancellation.Token);
        }
        catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_suspendDelayCancellation, delayCancellation))
                _suspendDelayCancellation = null;
            delayCancellation.Dispose();
        }

        await TrySuspendWebViewAsync(generation);
    }

    private void CancelPendingSuspendDelay()
    {
        var cancellation = _suspendDelayCancellation;
        _suspendDelayCancellation = null;
        if (cancellation is not null && !cancellation.IsCancellationRequested) cancellation.Cancel();
    }

    private async Task TrySuspendWebViewAsync(int generation)
    {
        // The SDK requires the controller to be invisible before TrySuspendAsync.
        // Yield once so WebView2CompositionControl can propagate WPF visibility.
        await Dispatcher.Yield(DispatcherPriority.Background);
        await _webViewLifecycleGate.WaitAsync();
        try
        {
            if (!ShouldTrySuspend(_closed, _backgroundSuspended, generation, _lifecycleGeneration)) return;
            try
            {
                var core = _browser.CoreWebView2;
                if (core is null || core.IsSuspended) return;
                if (!await core.TrySuspendAsync())
                    System.Diagnostics.Debug.WriteLine($"Web module '{_moduleId}' could not enter suspended state; hidden fallback remains active.");
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException or ObjectDisposedException)
            {
                // Older runtimes or a controller racing shutdown may reject suspend.
                // The native window remains hidden and can still be restored safely.
                System.Diagnostics.Debug.WriteLine($"Web module '{_moduleId}' suspend unavailable: {exception.GetType().Name}");
            }

            if (!_closed && (!_backgroundSuspended || generation != _lifecycleGeneration)) TryResumeWebView();
        }
        finally
        {
            _webViewLifecycleGate.Release();
        }
    }

    internal static bool ShouldTrySuspend(bool closed, bool backgroundSuspended,
        int scheduledGeneration, int currentGeneration) =>
        !closed && backgroundSuspended && scheduledGeneration == currentGeneration;

    private void TryResumeWebView()
    {
        try
        {
            if (_browser.CoreWebView2 is { IsSuspended: true } core) core.Resume();
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine($"Web module '{_moduleId}' resume unavailable: {exception.GetType().Name}");
        }
    }

    private void ScheduleEventDispatch()
    {
        try
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DrainPendingEvents));
        }
        catch (TaskCanceledException)
        {
            lock (_eventGate) _eventDispatchScheduled = false;
        }
        catch (InvalidOperationException)
        {
            lock (_eventGate) _eventDispatchScheduled = false;
        }
    }

    private void DrainPendingEvents()
    {
        for (var delivered = 0; delivered < MaximumEventsPerDispatch; delivered++)
        {
            ModuleWebEventArgs? eventArgs;
            lock (_eventGate)
            {
                if (_closed)
                {
                    _pendingLiveEvents.Clear();
                    _eventDispatchScheduled = false;
                    return;
                }
                if (_backgroundSuspended)
                {
                    while (_pendingLiveEvents.TryDequeue(out var deferred)) _deferredEvents.Add(deferred);
                    _eventDispatchScheduled = false;
                    return;
                }
                if (!_readySent || _browser.CoreWebView2 is null || _bridge is null)
                {
                    _eventDispatchScheduled = false;
                    return;
                }
                if (!_pendingLiveEvents.TryDequeue(out eventArgs))
                {
                    _eventDispatchScheduled = false;
                    return;
                }
            }

            var message = _bridge.SerializeEvent(eventArgs);
            if (message is null) continue;
            try
            {
                _browser.CoreWebView2.PostWebMessageAsJson(message);
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException or ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine($"Web module '{_moduleId}' event delivery unavailable: {exception.GetType().Name}");
                lock (_eventGate) _eventDispatchScheduled = false;
                return;
            }
        }

        lock (_eventGate)
        {
            if (_pendingLiveEvents.Count == 0)
            {
                _eventDispatchScheduled = false;
                return;
            }
        }
        ScheduleEventDispatch();
    }

    private void SchedulePendingEventsIfNeeded()
    {
        var scheduleDispatch = false;
        lock (_eventGate)
        {
            if (!_closed && !_backgroundSuspended && _pendingLiveEvents.Count > 0 && !_eventDispatchScheduled)
            {
                _eventDispatchScheduled = true;
                scheduleDispatch = true;
            }
        }
        if (scheduleDispatch) ScheduleEventDispatch();
    }

    private void PostPresentationChanged(CoreWebView2 core) =>
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "presentationChanged",
            appearancePresetId = _appearancePresetId,
            languageCode = _languageCode
        }));

    private static string EncodeEntry(string entry) =>
        string.Join('/', entry.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
}

internal sealed class CoalescedWebEventBuffer(int capacity)
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly Dictionary<string, ModuleWebEventArgs> _events = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public int Count => _events.Count;

    public void Add(ModuleWebEventArgs eventArgs)
    {
        var name = eventArgs.Name ?? string.Empty;
        if (_events.ContainsKey(name))
        {
            _events[name] = eventArgs;
            return;
        }
        while (_events.Count >= _capacity && _order.TryDequeue(out var oldest)) _events.Remove(oldest);
        _events.Add(name, eventArgs);
        _order.Enqueue(name);
    }

    public IReadOnlyList<ModuleWebEventArgs> Drain()
    {
        if (_events.Count == 0) return [];
        var result = new List<ModuleWebEventArgs>(_events.Count);
        while (_order.TryDequeue(out var name))
        {
            if (_events.Remove(name, out var eventArgs)) result.Add(eventArgs);
        }
        return result;
    }

    public void Clear()
    {
        _events.Clear();
        _order.Clear();
    }
}
