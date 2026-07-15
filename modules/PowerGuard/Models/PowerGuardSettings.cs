namespace QingToolbox.Modules.PowerGuard.Models;

public enum PowerActionKind { NormalShutdown }

public sealed class PowerGuardSettings
{
    public bool GuardEnabled { get; set; } = true;
    public int StartupGraceSeconds { get; set; } = 120;
    public int OfflineConfirmationSeconds { get; set; } = 60;
    public int ShutdownCountdownSeconds { get; set; } = 600;
    public int RecoveryConfirmationSeconds { get; set; } = 30;
    public PowerActionKind PowerAction { get; set; } = PowerActionKind.NormalShutdown;
    public bool SuppressedUntilConnectivityRestored { get; set; }
    public bool ShowRecoveryNotification { get; set; } = true;

    public void Normalize()
    {
        StartupGraceSeconds = Math.Clamp(StartupGraceSeconds, 0, 600);
        OfflineConfirmationSeconds = Math.Clamp(OfflineConfirmationSeconds, 15, 300);
        ShutdownCountdownSeconds = Math.Clamp(ShutdownCountdownSeconds, 60, 3600);
        RecoveryConfirmationSeconds = Math.Clamp(RecoveryConfirmationSeconds, 5, 120);
        PowerAction = PowerActionKind.NormalShutdown;
    }
}
