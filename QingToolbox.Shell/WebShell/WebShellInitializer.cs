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
    WebShellState state, WebNavigationPolicy navigation, Lazy<WebAssetIdentity> assetIdentity, WebBridgeHost bridge, SessionLogService log) : IWebShellInitializer, IDisposable
{
    internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan EnvironmentTimeout = TimeSpan.FromSeconds(30);
    private WebView2? _webView;
    private Action<string>? _fallback;
    private Action<WebView2>? _ready;
    private Action<WebView2>? _preparing;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _generationLifetime;
    private Action? _unbindCore;
    private readonly object _recoverySync = new();
    private readonly WebProcessFailureGate _processFailureGate = new();
    private Task _recoveryTask = Task.CompletedTask;
    private CoreWebView2? _currentCore;
    private long _currentGeneration;
    private bool _disposed;
    private bool _navigationSucceeded;
    private bool _readyIdentitySucceeded;
    private bool _snapshotIssued;
    private bool _snapshotValidated;
    private bool _activationPingSucceeded;
    private bool _workspaceActivated;
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
        _readyIdentitySucceeded = false; _snapshotIssued = false; _snapshotValidated = false; _activationPingSucceeded = false; _workspaceActivated = false;
        state.BeginInitialization();
        WebAssetIdentity assets;
        try { assets = assetIdentity.Value; }
        catch (WebAssetIdentityException exception) { throw new WebAssetIdentityException(exception.Code); }
        var assetRoot = assets.AssetRoot;
        Directory.CreateDirectory(paths.WebView2UserDataDirectory);
        var webView = new WebView2(); _webView = webView;
        _preparing?.Invoke(webView);
        log.Information("WebShell", "Creating WebView2 environment.");
        var coreEnvironment = await CoreWebView2Environment.CreateAsync(null, paths.WebView2UserDataDirectory).WaitAsync(EnvironmentTimeout, cancellationToken);
        log.Information("WebShell", "Creating WebView2 controller.");
        await webView.EnsureCoreWebView2Async(coreEnvironment).WaitAsync(EnvironmentTimeout, cancellationToken);
        var core = webView.CoreWebView2;
        var generation = bridge.Attach(core);
        _generationLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentCore = core; _currentGeneration = generation;
        _processFailureGate.Bind(core, generation, _generationLifetime.Token);
        _unbindCore = ConfigureCore(core, assets, generation, _generationLifetime.Token);
        var activationAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChallenge(long acceptedGeneration) { if (acceptedGeneration == generation) { _readyIdentitySucceeded = true; _snapshotIssued = true; } }
        void OnActivation(long acceptedGeneration) { if (acceptedGeneration == generation) { _snapshotValidated = true; _activationPingSucceeded = true; activationAccepted.TrySetResult(); } }
        bridge.ReadyChallengeIssued += OnChallenge; bridge.ActivationAccepted += OnActivation;
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
            log.Information("WebShell", "Local navigation completed; awaiting acknowledged activation.");
            await activationAccepted.Task.WaitAsync(ReadyTimeout, cancellationToken);
            state.MarkReady();
            log.Information("WebShell", $"Development Web Shell ready; protocol={WebBridgeProtocol.Version}; generation={generation}.");
            _ready?.Invoke(webView);
            _workspaceActivated = true;
            if (launchOptions.WebShellProbeId is { } probeId)
                CompleteProbe(probeId, null);
            return webView;
        }
        finally { bridge.ReadyChallengeIssued -= OnChallenge; bridge.ActivationAccepted -= OnActivation; }
    }

    private Action ConfigureCore(CoreWebView2 core, WebAssetIdentity assets, long generation, CancellationToken generationToken)
    {
        core.SetVirtualHostNameToFolderMapping(WebNavigationPolicy.VirtualHost, assets.AssetRoot, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = environment.IsDevelopment && launchOptions.EnableWebDevTools;
        core.Settings.AreBrowserAcceleratorKeysEnabled = core.Settings.AreDevToolsEnabled;
        void OnNavigationStarting(object? _, CoreWebView2NavigationStartingEventArgs args)
        {
            if (string.Equals(args.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) return;
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !navigation.IsAllowed(uri)) { log.Warning("WebShell", "Main navigation denied by policy."); args.Cancel = true; }
            else log.Information("WebShell", $"Main navigation allowed; path={uri.AbsolutePath}.");
        }
        core.NavigationStarting += OnNavigationStarting;
        core.AddWebResourceRequestedFilter("http://*/*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter("https://*/*", CoreWebView2WebResourceContext.All);
        void OnWebResourceRequested(object? _, CoreWebView2WebResourceRequestedEventArgs args)
        {
            if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) || !navigation.IsAllowed(uri))
            { log.Warning("WebShell", "External web resource request denied."); args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Type: text/plain"); }
            else
            {
                if (!assets.TryResolve(uri.AbsolutePath, out var file))
                { args.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain"); return; }
                var contentType = Path.GetExtension(file).ToLowerInvariant() switch { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".json" => "application/json; charset=utf-8", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
                args.Response = core.Environment.CreateWebResourceResponse(File.OpenRead(file), 200, "OK", $"Content-Type: {contentType}\r\nCache-Control: no-store");
                log.Information("WebShell", $"Local web resource served; path={uri.AbsolutePath}.");
            }
        }
        void OnNewWindow(object? _, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;
        void OnDownload(object? _, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;
        void OnPermission(object? _, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;
        void OnProcessFailure(object? sender, CoreWebView2ProcessFailedEventArgs args)
        { if (sender is CoreWebView2 failedCore) QueueProcessRecovery(failedCore, generation, generationToken, args.ProcessFailedKind); }
        core.WebResourceRequested += OnWebResourceRequested; core.NewWindowRequested += OnNewWindow;
        core.DownloadStarting += OnDownload; core.PermissionRequested += OnPermission; core.ProcessFailed += OnProcessFailure;
        return () =>
        {
            core.NavigationStarting -= OnNavigationStarting; core.WebResourceRequested -= OnWebResourceRequested;
            core.NewWindowRequested -= OnNewWindow; core.DownloadStarting -= OnDownload;
            core.PermissionRequested -= OnPermission; core.ProcessFailed -= OnProcessFailure;
        };
    }

    private void QueueProcessRecovery(CoreWebView2 core, long generation, CancellationToken generationToken,
        CoreWebView2ProcessFailedKind kind)
    {
        lock (_recoverySync)
        {
            if (!_processFailureGate.TryAccept(core, generation, generationToken, _disposed)) return;
            _recoveryTask = RecoverProcessAsync(generation, kind);
        }
    }

    private async Task RecoverProcessAsync(long generation, CoreWebView2ProcessFailedKind kind)
    {
        try
        {
            log.Warning("WebShell", $"Current WebView generation failed; generation={generation}; kind={kind}.");
            _fallback?.Invoke("WebShell.Recovering");
            if (!state.TryBeginProcessRecovery()) { FallBack("WebShell.ProcessFailure.Repeated"); return; }
            DisposeCurrent();
            if (_lifetime is null) return;
            await CreateAndHandshakeAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime?.IsCancellationRequested != false) { }
        catch { FallBack("WebShell.ProcessFailure.RecoveryFailed"); }
    }

    private static string Classify(Exception exception) => exception switch
    {
        TimeoutException => "ReadyTimeout",
        WebAssetIdentityException asset => $"AssetIdentityMismatch.{asset.Code}",
        InvalidOperationException invalid when invalid.Message.StartsWith("Navigation.", StringComparison.Ordinal) => invalid.Message,
        _ => exception.GetType().Name
    };
    private void CompleteProbe(Guid probeId, string? failureCode)
    {
        var result = new { probeId, protocolVersion = WebBridgeProtocol.Version,
            assetBuildId = assetIdentity.IsValueCreated ? assetIdentity.Value.AssetBuildId : WebAssetBuildInfo.ExpectedAssetBuildId, navigationSucceeded = _navigationSucceeded,
            readyIdentitySucceeded = _readyIdentitySucceeded, snapshotIssued = _snapshotIssued, snapshotValidated = _snapshotValidated,
            activationPingSucceeded = _activationPingSucceeded, workspaceActivated = _workspaceActivated,
            usedMockTransport = false, failureCode };
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
    private void DisposeCurrent()
    {
        _generationLifetime?.Cancel(); _generationLifetime?.Dispose(); _generationLifetime = null;
        _processFailureGate.Unbind();
        _unbindCore?.Invoke(); _unbindCore = null; bridge.Detach(); _webView?.Dispose(); _webView = null;
        _currentCore = null; _currentGeneration = 0;
    }
    public void PublishSnapshot() { if (state.IsReady) bridge.PublishSnapshot(); }
    public void Dispose() { _disposed = true; state.BeginDisposal(); _lifetime?.Cancel(); _lifetime?.Dispose(); DisposeCurrent(); state.MarkDisposed(); }
}
