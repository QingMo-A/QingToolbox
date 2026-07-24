using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using System.Text.Json;
using System.Windows;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.WebShell;

public interface IWebShellInitializer
{
    bool IsAllowed { get; }
    Task<WebView2?> InitializeAsync(Action<WebView2> preparing, Action<WebView2> ready, Action<string> fallback, CancellationToken cancellationToken);
    void PublishSnapshot();
}

public sealed class DisabledWebShellInitializer : IWebShellInitializer
{
    public bool IsAllowed => false;
    public Task<WebView2?> InitializeAsync(Action<WebView2> preparing, Action<WebView2> ready, Action<string> fallback, CancellationToken cancellationToken) => Task.FromResult<WebView2?>(null);
    public void PublishSnapshot() { }
}

public sealed class WebShellInitializer(
    ApplicationExecutionEnvironment environment, ApplicationLaunchOptions launchOptions, ApplicationPaths paths,
    WebShellState state, WebNavigationPolicy navigation, WebBridgeHost bridge, SessionLogService log) : IWebShellInitializer, IDisposable
{
    internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan EnvironmentTimeout = TimeSpan.FromSeconds(30);
    private WebView2? _webView;
    private Action<string>? _fallback;
    private Action<WebView2>? _ready;
    private Action<WebView2>? _preparing;
    private CancellationTokenSource? _lifetime;
    private bool _disposed;
    private bool _navigationSucceeded;
    public bool IsAllowed => environment.IsDevelopment;

    public async Task<WebView2?> InitializeAsync(Action<WebView2> preparing, Action<WebView2> ready, Action<string> fallback, CancellationToken cancellationToken)
    {
        if (!IsAllowed || _disposed) return null;
        _preparing = preparing; _ready = ready; _fallback = fallback;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try { return await CreateAndHandshakeAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _disposed) { DisposeCurrent(); return null; }
        catch (Exception exception)
        {
            log.Warning("WebShell", $"Web Shell initialization failed; failure={Classify(exception)}.");
            FallBack($"WebShell.{Classify(exception)}"); return null;
        }
    }

    private async Task<WebView2> CreateAndHandshakeAsync(CancellationToken cancellationToken)
    {
        _navigationSucceeded = false;
        state.BeginInitialization();
        var assetRoot = Path.Combine(AppContext.BaseDirectory, "WebUI");
        if (!File.Exists(Path.Combine(assetRoot, "index.html")) || !File.Exists(Path.Combine(assetRoot, "qing-web-assets.json")))
            throw new FileNotFoundException("Verified Web UI assets are missing.");
        Directory.CreateDirectory(paths.WebView2UserDataDirectory);
        var webView = new WebView2(); _webView = webView;
        _preparing?.Invoke(webView);
        log.Information("WebShell", "Creating WebView2 environment.");
        var coreEnvironment = await CoreWebView2Environment.CreateAsync(null, paths.WebView2UserDataDirectory).WaitAsync(EnvironmentTimeout, cancellationToken);
        log.Information("WebShell", "Creating WebView2 controller.");
        await webView.EnsureCoreWebView2Async(coreEnvironment).WaitAsync(EnvironmentTimeout, cancellationToken);
        var core = webView.CoreWebView2;
        ConfigureCore(core, assetRoot);
        var generation = bridge.Attach(core);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady(long acceptedGeneration) { if (acceptedGeneration == generation) ready.TrySetResult(); }
        bridge.ReadyAccepted += OnReady;
        void OnCommand(string command, long commandGeneration)
        {
            if (command == "app.ping" && commandGeneration == generation) ping.TrySetResult();
        }
        bridge.CommandSucceeded += OnCommand;
        try
        {
            state.BeginNavigation();
            log.Information("WebShell", "Navigating to the verified local entry point.");
            var navigationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ulong targetNavigationId = 0;
            void OnStarting(object? _, CoreWebView2NavigationStartingEventArgs args)
            {
                if (string.Equals(args.Uri, WebNavigationPolicy.StartUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                { targetNavigationId = args.NavigationId; log.Information("WebShell", $"Target navigation started; id={targetNavigationId}."); }
            }
            void OnNavigation(object? _, CoreWebView2NavigationCompletedEventArgs args)
            {
                if (targetNavigationId == 0 || args.NavigationId != targetNavigationId) return;
                log.Information("WebShell", $"Target navigation completed; id={args.NavigationId}; success={args.IsSuccess}; status={args.WebErrorStatus}.");
                if (args.IsSuccess) navigationCompleted.TrySetResult();
                else navigationCompleted.TrySetException(new InvalidOperationException($"Navigation.{args.WebErrorStatus}"));
            }
            core.NavigationStarting += OnStarting; core.NavigationCompleted += OnNavigation;
            try { core.Navigate(WebNavigationPolicy.StartUri.AbsoluteUri); await navigationCompleted.Task.WaitAsync(ReadyTimeout, cancellationToken); _navigationSucceeded = true; }
            finally { core.NavigationStarting -= OnStarting; core.NavigationCompleted -= OnNavigation; }
            state.AwaitReady();
            log.Information("WebShell", "Local navigation completed; awaiting trusted web.ready.");
            await ready.Task.WaitAsync(ReadyTimeout, cancellationToken);
            state.MarkReady();
            log.Information("WebShell", $"Development Web Shell ready; protocol={WebBridgeProtocol.Version}; generation={generation}.");
            _ready?.Invoke(webView);
            if (launchOptions.WebShellProbeId is { } probeId)
            {
                await ping.Task.WaitAsync(ReadyTimeout, cancellationToken);
                CompleteProbe(probeId, null);
            }
            return webView;
        }
        finally { bridge.ReadyAccepted -= OnReady; bridge.CommandSucceeded -= OnCommand; }
    }

    private void ConfigureCore(CoreWebView2 core, string assetRoot)
    {
        core.SetVirtualHostNameToFolderMapping(WebNavigationPolicy.VirtualHost, assetRoot, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = environment.IsDevelopment && launchOptions.EnableWebDevTools;
        core.Settings.AreBrowserAcceleratorKeysEnabled = core.Settings.AreDevToolsEnabled;
        core.NavigationStarting += (_, args) =>
        {
            if (string.Equals(args.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) return;
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !navigation.IsAllowed(uri)) { log.Warning("WebShell", "Main navigation denied by policy."); args.Cancel = true; }
            else log.Information("WebShell", $"Main navigation allowed; path={uri.AbsolutePath}.");
        };
        core.AddWebResourceRequestedFilter("http://*/*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter("https://*/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) || !navigation.IsAllowed(uri))
            { log.Warning("WebShell", "External web resource request denied."); args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Type: text/plain"); }
            else
            {
                var relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
                if (relative.Length == 0) relative = "index.html";
                var root = Path.GetFullPath(assetRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var file = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                { args.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain"); return; }
                var contentType = Path.GetExtension(file).ToLowerInvariant() switch { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".json" => "application/json; charset=utf-8", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
                args.Response = core.Environment.CreateWebResourceResponse(File.OpenRead(file), 200, "OK", $"Content-Type: {contentType}\r\nCache-Control: no-store");
                log.Information("WebShell", $"Local web resource served; path={uri.AbsolutePath}.");
            }
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += OnProcessFailed;
    }

    private async void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (_disposed || _lifetime?.IsCancellationRequested != false) return;
        log.Warning("WebShell", $"WebView process failed; kind={args.ProcessFailedKind}.");
        _fallback?.Invoke("WebShell.Recovering");
        if (!state.TryBeginProcessRecovery()) { FallBack("WebShell.ProcessFailure.Repeated"); return; }
        try { DisposeCurrent(); await CreateAndHandshakeAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { FallBack("WebShell.ProcessFailure.RecoveryFailed"); }
    }

    private static string Classify(Exception exception) => exception switch
    {
        TimeoutException => "ReadyTimeout",
        InvalidOperationException invalid when invalid.Message.StartsWith("Navigation.", StringComparison.Ordinal) => invalid.Message,
        _ => exception.GetType().Name
    };
    private void CompleteProbe(Guid probeId, string? failureCode)
    {
        var result = new { probeId, protocolVersion = WebBridgeProtocol.Version,
            assetBuildId = new WebAssetIdentity(Path.Combine(AppContext.BaseDirectory, "WebUI")).AssetBuildId,
            navigationSucceeded = _navigationSucceeded, readySucceeded = failureCode is null, snapshotSucceeded = failureCode is null,
            pingSucceeded = failureCode is null, usedMockTransport = false, failureCode };
        Directory.CreateDirectory(paths.TempDirectory);
        var output = Path.Combine(paths.TempDirectory, $"web-shell-probe-{probeId:D}.json");
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
    }
    private void FallBack(string code)
    {
        state.FallBack(code); DisposeCurrent(); _fallback?.Invoke(code);
        if (launchOptions.WebShellProbeId is { } probeId) CompleteProbe(probeId, code);
    }
    private void DisposeCurrent() { bridge.Detach(); _webView?.Dispose(); _webView = null; }
    public void PublishSnapshot() { if (state.IsReady) bridge.PublishSnapshot(); }
    public void Dispose() { _disposed = true; state.BeginDisposal(); _lifetime?.Cancel(); _lifetime?.Dispose(); DisposeCurrent(); state.MarkDisposed(); }
}
