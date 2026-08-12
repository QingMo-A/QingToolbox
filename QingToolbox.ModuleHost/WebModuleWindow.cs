using System.Text.Json;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QingToolbox.Abstractions.Modules;

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
    private readonly WebModuleBridgeDispatcher? _bridge;
    private bool _readySent;

    public WebModuleWindow(string moduleId, string version, string moduleRoot, string entry,
        string dataRoot, string title, Window? owner, WebModuleBridgeDispatcher? bridge = null)
    {
        _moduleId = moduleId;
        _version = version;
        _moduleRoot = Path.GetFullPath(moduleRoot);
        _entry = entry.Replace('\\', '/').TrimStart('/');
        _userDataFolder = Path.Combine(Path.GetFullPath(dataRoot), moduleId, "webview2");
        _bridge = bridge;
        _host = "qing-module.local";
        Title = title;
        Width = 900;
        Height = 680;
        MinWidth = 520;
        MinHeight = 380;
        Owner = owner;
        Content = _browser;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            await _browser.EnsureCoreWebView2Async(environment);
            var core = _browser.CoreWebView2;
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
        if (!e.IsSuccess || _readySent || _browser.CoreWebView2 is null) return;
        _readySent = true;
        var message = JsonSerializer.Serialize(new
        {
            type = "hostReady",
            protocolVersion = 1,
            moduleId = _moduleId,
            version = _version,
            bridge = true
        });
        _browser.CoreWebView2.PostWebMessageAsJson(message);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
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
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args) =>
        await HandleWebMessageAsync(args, _bridge);

    private async Task HandleWebMessageAsync(CoreWebView2WebMessageReceivedEventArgs args,
        WebModuleBridgeDispatcher? bridge)
    {
        if (bridge is null || _browser.CoreWebView2 is null) return;
        var response = await bridge.DispatchAsync(args.WebMessageAsJson, CancellationToken.None);
        if (response is not null) _browser.CoreWebView2.PostWebMessageAsJson(response);
    }

    public void PostEvent(ModuleWebEventArgs eventArgs)
    {
        if (!_readySent || _browser.CoreWebView2 is null || _bridge is null) return;
        var message = _bridge.SerializeEvent(eventArgs);
        if (message is not null) _browser.CoreWebView2.PostWebMessageAsJson(message);
    }

    private static string EncodeEntry(string entry) =>
        string.Join('/', entry.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
}
