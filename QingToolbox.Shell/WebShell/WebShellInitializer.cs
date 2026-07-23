using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.WebShell;

public interface IWebShellInitializer
{
    bool IsAllowed { get; }
    Task<WebView2?> InitializeAsync(Action<string> fallback, CancellationToken cancellationToken);
    void PublishSnapshot();
}

public sealed class DisabledWebShellInitializer : IWebShellInitializer
{
    public bool IsAllowed => false;
    public Task<WebView2?> InitializeAsync(Action<string> fallback, CancellationToken cancellationToken) =>
        Task.FromResult<WebView2?>(null);
    public void PublishSnapshot() { }
}

public sealed class WebShellInitializer(
    ApplicationExecutionEnvironment environment,
    ApplicationLaunchOptions launchOptions,
    ApplicationPaths paths,
    WebShellState state,
    WebNavigationPolicy navigation,
    WebBridgeHost bridge,
    SessionLogService log) : IWebShellInitializer, IDisposable
{
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(12);
    private WebView2? _webView;
    private Action<string>? _fallback;
    public bool IsAllowed => environment.IsDevelopment;

    public async Task<WebView2?> InitializeAsync(Action<string> fallback, CancellationToken cancellationToken)
    {
        if (!IsAllowed) return null;
        _fallback = fallback;
        try
        {
            var assetRoot = Path.Combine(AppContext.BaseDirectory, "WebUI");
            if (!File.Exists(Path.Combine(assetRoot, "index.html"))) throw new FileNotFoundException("Web UI entry point is missing.");
            Directory.CreateDirectory(paths.WebView2UserDataDirectory);
            var webView = new WebView2(); _webView = webView;
            var coreEnvironment = await CoreWebView2Environment
                .CreateAsync(null, paths.WebView2UserDataDirectory)
                .WaitAsync(InitializationTimeout, cancellationToken);
            await webView.EnsureCoreWebView2Async(coreEnvironment)
                .WaitAsync(InitializationTimeout, cancellationToken);
            var core = webView.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(WebNavigationPolicy.VirtualHost, assetRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDevToolsEnabled = environment.IsDevelopment && launchOptions.EnableWebDevTools;
            core.Settings.AreBrowserAcceleratorKeysEnabled = core.Settings.AreDevToolsEnabled;
            core.NavigationStarting += (_, args) => { if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !navigation.IsAllowed(uri)) args.Cancel = true; };
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.ProcessFailed += OnProcessFailed;
            bridge.Attach(core);
            webView.Source = WebNavigationPolicy.StartUri;
            state.MarkReady();
            log.Information("WebShell", $"Development Web Shell initialized; protocol={WebBridgeProtocol.Version}; source=LocalVirtualHost.");
            bridge.PublishHostEvent("webShell.initialized");
            return webView;
        }
        catch (Exception exception)
        {
            log.Warning("WebShell", $"Web Shell initialization failed; failure={exception.GetType().Name}.");
            FallBack($"WebShell.{exception.GetType().Name}"); return null;
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        log.Warning("WebShell", $"WebView process failed; kind={args.ProcessFailedKind}.");
        if (state.TryBeginProcessRecovery()) { try { _webView?.Reload(); return; } catch { } }
        FallBack("WebShell.ProcessFailure");
    }
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess) FallBack($"WebShell.Navigation.{args.WebErrorStatus}");
    }
    private void FallBack(string code) { state.FallBack(code); bridge.Detach(); _webView?.Dispose(); _webView = null; _fallback?.Invoke(code); }
    public void PublishSnapshot() { if (state.Availability == WebShellAvailability.Ready) bridge.PublishSnapshot(); }
    public void Dispose() { bridge.Detach(); _webView?.Dispose(); _webView = null; }
}
