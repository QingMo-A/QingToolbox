using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly string _moduleId;
    private readonly string _version;
    private readonly string _host;
    private readonly string _moduleRoot;
    private readonly string _entry;
    private readonly string _userDataFolder;
    private readonly WebView2 _browser = new();
    private readonly Grid _surface = new();
    private readonly TextBlock _status = new();
    private readonly string? _iconPath;
    private readonly WebModuleBridgeDispatcher? _bridge;
    private readonly Action? _onClosed;
    private string _appearancePresetId;
    private string _languageCode;
    private bool _readySent;
    private bool _closed;
    private readonly string _presentationNonce = Guid.NewGuid().ToString("N");

    public WebModuleWindow(string moduleId, string version, string moduleRoot, string entry,
        string dataRoot, string title, Window? owner, WebModuleBridgeDispatcher? bridge = null,
        string appearancePresetId = AppearancePresetIds.QingDefault, string languageCode = "en-US", string? iconPath = null,
        Action? onClosed = null)
    {
        _moduleId = moduleId;
        _version = version;
        _moduleRoot = Path.GetFullPath(moduleRoot);
        _entry = entry.Replace('\\', '/').TrimStart('/');
        _userDataFolder = Path.Combine(Path.GetFullPath(dataRoot), moduleId, "webview2");
        _bridge = bridge;
        _onClosed = onClosed;
        _appearancePresetId = AppearancePresetIds.Normalize(appearancePresetId);
        _languageCode = languageCode is "en-US" or "zh-CN" ? languageCode : "en-US";
        _iconPath = iconPath;
        _host = "qing-module.local";
        Title = title;
        Width = 900;
        Height = 680;
        MinWidth = 520;
        MinHeight = 380;
        Owner = owner;
        _browser.Visibility = Visibility.Hidden;
        _browser.IsHitTestVisible = false;
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
        var dark = _appearancePresetId is not AppearancePresetIds.QingDefault;
        _surface.Background = new SolidColorBrush(dark ? Color.FromRgb(8, 15, 37) : Color.FromRgb(243, 247, 253));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            await _browser.EnsureCoreWebView2Async(environment);
            var core = _browser.CoreWebView2;
            await core.AddScriptToExecuteOnDocumentCreatedAsync($"(() => {{ const n='{_presentationNonce}'; const ready=()=>requestAnimationFrame(()=>requestAnimationFrame(()=>chrome.webview.postMessage({{type:'qing-internal-presentation-ready',nonce:n}}))); if(document.readyState==='loading') document.addEventListener('DOMContentLoaded',ready,{{once:true}}); else ready(); }})();");
            core.Settings.AreDevToolsEnabled = false;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;
            core.SetVirtualHostNameToFolderMapping(
                _host,
                _moduleRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate($"https://{_host}/{EncodeEntry(_entry)}");
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
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
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
                if (_closed || _surface.Children.Count < 2) return;
                _browser.Visibility = Visibility.Visible;
                _browser.IsHitTestVisible = true;
                _surface.Children.RemoveAt(1);
            }));
            return true;
        }
        catch (JsonException) { return false; }
        catch (KeyNotFoundException) { return false; }
    }

    public void PostEvent(ModuleWebEventArgs eventArgs)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => PostEvent(eventArgs)));
            return;
        }
        if (_closed) return;
        if (!_readySent || _browser.CoreWebView2 is null || _bridge is null) return;
        var message = _bridge.SerializeEvent(eventArgs);
        if (message is not null) _browser.CoreWebView2.PostWebMessageAsJson(message);
    }

    public void ApplyPresentation(string appearancePresetId, string languageCode)
    {
        _appearancePresetId = AppearancePresetIds.Normalize(appearancePresetId);
        _languageCode = languageCode is "en-US" or "zh-CN" ? languageCode : "en-US";
        if (!_readySent || _browser.CoreWebView2 is null) return;
        _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "presentationChanged",
            appearancePresetId = _appearancePresetId,
            languageCode = _languageCode
        }));
    }

    private static string EncodeEntry(string entry) =>
        string.Join('/', entry.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
}
