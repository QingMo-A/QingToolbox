namespace QingToolbox.Modules.PowerGuard.State;
public enum PowerGuardState { Disabled, StartupGrace, Online, SuspectedOffline, Countdown, SuppressedForCurrentOutage, Recovering, ExecutingShutdown, ActionFailed, MonitoringFault, Stopping }
