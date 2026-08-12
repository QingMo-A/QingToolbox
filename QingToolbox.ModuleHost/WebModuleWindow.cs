using System.Text.Json;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

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
    private bool _readySent;

    public WebModuleWindow(string moduleId, string version, string moduleRoot, string entry,
        string dataRoot, string title, Window? owner)
    {
        _moduleId = moduleId;
        _version = version;
        _moduleRoot = Path.GetFullPath(moduleRoot);
        _entry = entry.Replace('\\', '/').TrimStart('/');
        _userDataFolder = Path.Combine(Path.GetFullPath(dataRoot), "webview2");
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
            core.SetVirtualHostNameToFolderMapping(
                _host,
                _moduleRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate($"https://{_host}/{EncodeEntry(_entry)}");
        }
        catch
        {
            Close();
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
            version = _version
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
        }
        _browser.Dispose();
    }

    private static string EncodeEntry(string entry) =>
        string.Join('/', entry.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
}
