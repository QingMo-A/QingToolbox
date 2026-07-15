using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Modules.PowerGuard.ViewModels;
using QingToolbox.Modules.PowerGuard.Views;

namespace QingToolbox.Modules.PowerGuard.Services;

public sealed class WarningWindowPresenter(ILocalizationService localization, string moduleId) : IWarningPresenter
{
    private ShutdownWarningWindow? _window;
    private Func<Task>? _suppress;
    private Func<Task>? _extend;
    private Func<Task>? _shutdown;
    public void Configure(Func<Task> suppress, Func<Task> extend, Func<Task> shutdown) { _suppress=suppress; _extend=extend; _shutdown=shutdown; }

    public Task ShowAsync(bool testMode, int seconds, CancellationToken token = default) => DispatchAsync(() =>
    {
        if (_window is not null) { _window.SetSeconds(seconds); return; }
        _window = new ShutdownWarningWindow(localization, moduleId, new() { IsTestMode=testMode, Seconds=seconds },
            async () => { if (!testMode && _suppress is not null) await _suppress(); await CloseAsync(); },
            async () => { if (!testMode && _extend is not null) await _extend(); },
            async () => { if (!testMode && _shutdown is not null) await _shutdown(); else await CloseAsync(); });
        _window.Closed += (_, _) => _window = null;
        _window.Show(); _window.PositionAtPrimaryWorkArea();
    }, token);
    public Task UpdateAsync(int seconds, bool recovering, CancellationToken token = default) => DispatchAsync(() => _window?.SetSeconds(seconds), token);
    public Task CloseAsync(CancellationToken token = default) => DispatchAsync(() => { var window=_window; _window=null; window?.CloseWithoutCancel(); }, token);
    public void RefreshLocalization() { if (_window is not null) _ = DispatchAsync(_window.RefreshLocalization, CancellationToken.None); }
    private static Task DispatchAsync(Action action, CancellationToken token)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
        return dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal, token).Task;
    }
}
