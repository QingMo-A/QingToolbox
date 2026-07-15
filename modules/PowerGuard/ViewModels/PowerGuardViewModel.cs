using System.ComponentModel;
using System.Runtime.CompilerServices;
using QingToolbox.Modules.PowerGuard.Models;
using QingToolbox.Modules.PowerGuard.Services;

namespace QingToolbox.Modules.PowerGuard.ViewModels;

public sealed class PowerGuardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PowerGuardController _controller;
    private PowerGuardSnapshot _snapshot;
    public event PropertyChangedEventHandler? PropertyChanged;
    public PowerGuardSnapshot Snapshot { get => _snapshot; private set { _snapshot = value; Changed(); Changed(nameof(State)); Changed(nameof(Countdown)); } }
    public PowerGuardSettings EditableSettings { get; private set; }
    public string State => Snapshot.State.ToString();
    public string Countdown => TimeSpan.FromSeconds(Snapshot.RemainingSeconds).ToString(@"mm\:ss");
    public string OperationStatus { get; set; } = "";

    public PowerGuardViewModel(PowerGuardController controller)
    {
        _controller = controller; _snapshot = controller.Snapshot; EditableSettings = Clone(controller.Settings);
        controller.SnapshotChanged += OnSnapshotChanged;
    }
    private void OnSnapshotChanged(object? sender, PowerGuardSnapshot snapshot)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Snapshot = snapshot;
        else _ = dispatcher.InvokeAsync(() => Snapshot = snapshot);
    }
    public async Task SaveAsync() { await _controller.SaveSettingsAsync(EditableSettings); EditableSettings = Clone(_controller.Settings); Changed(nameof(EditableSettings)); }
    public Task<ConnectivityProbeResult> ProbeAsync() => _controller.ProbeNowAsync();
    public Task TestAsync() => _controller.ShowTestWarningAsync();
    public Task SuppressAsync() => _controller.SuppressCurrentOutageAsync();
    public Task RearmAsync() => _controller.RearmAsync();
    private static PowerGuardSettings Clone(PowerGuardSettings s) => new() { GuardEnabled=s.GuardEnabled, StartupGraceSeconds=s.StartupGraceSeconds, OfflineConfirmationSeconds=s.OfflineConfirmationSeconds, ShutdownCountdownSeconds=s.ShutdownCountdownSeconds, RecoveryConfirmationSeconds=s.RecoveryConfirmationSeconds, PowerAction=s.PowerAction, SuppressedUntilConnectivityRestored=s.SuppressedUntilConnectivityRestored, ShowRecoveryNotification=s.ShowRecoveryNotification };
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose() => _controller.SnapshotChanged -= OnSnapshotChanged;
}
