using QingToolbox.Core.Settings;
using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell.WebShell;

public sealed record WebSettingsSnapshotValues(WebSettingsLanguage Language, bool ShowLogsInSidebar,
    string MainWindowCloseBehavior, string CloseBehaviorMessage, bool LaunchAtLogin,
    bool CanConfigureLaunchAtLogin, bool CanRepairStartup, string StartupPresentationMode, string StartupBackend,
    string StartupStatus, string StartupMessage);

public interface IWebSettingsSnapshotSource { WebSettingsSnapshotValues Read(); }
public interface IWebSettingsMutation
{
    bool ShowLogsInSidebar { get; }
    MainWindowCloseBehavior MainWindowCloseBehavior { get; }
    StartupPresentationMode StartupPresentationMode { get; }
    bool LaunchAtLogin { get; }
    bool CanConfigureLaunchAtLogin { get; }
    bool CanRepairStartup { get; }
    Task SetShowLogsInSidebarAsync(bool value, CancellationToken cancellationToken);
    Task SetMainWindowCloseBehaviorAsync(MainWindowCloseBehavior value, CancellationToken cancellationToken);
    Task SetStartupPresentationModeAsync(StartupPresentationMode value, CancellationToken cancellationToken);
    Task<WebSettingsMutationResult> SetLaunchAtLoginAsync(bool value, CancellationToken cancellationToken);
    Task<WebSettingsMutationResult> RepairStartupAsync(CancellationToken cancellationToken);
}

public enum WebSettingsMutationResult { Succeeded, Unavailable, Failed }

public sealed class WebSettingsMutation(MainWindowViewModel viewModel) : IWebSettingsMutation
{
    public bool ShowLogsInSidebar => viewModel.ShowLogsInSidebar;
    public MainWindowCloseBehavior MainWindowCloseBehavior => viewModel.SelectedMainWindowCloseBehavior;
    public StartupPresentationMode StartupPresentationMode => viewModel.SelectedStartupPresentationMode;
    public bool LaunchAtLogin => viewModel.LaunchAtLogin;
    public bool CanConfigureLaunchAtLogin => viewModel.CanConfigureWindowsStartup;
    public bool CanRepairStartup => viewModel.CanRepairStartup;
    public Task SetShowLogsInSidebarAsync(bool value, CancellationToken cancellationToken) =>
        viewModel.SetShowLogsInSidebarAsync(value, cancellationToken);
    public Task SetMainWindowCloseBehaviorAsync(MainWindowCloseBehavior value, CancellationToken cancellationToken) =>
        viewModel.SetMainWindowCloseBehaviorAsync(value, cancellationToken);
    public Task SetStartupPresentationModeAsync(StartupPresentationMode value, CancellationToken cancellationToken) =>
        viewModel.SetStartupPresentationModeAsync(value, cancellationToken);
    public Task<WebSettingsMutationResult> SetLaunchAtLoginAsync(bool value, CancellationToken cancellationToken) =>
        viewModel.SetLaunchAtLoginFromWebAsync(value, cancellationToken);
    public Task<WebSettingsMutationResult> RepairStartupAsync(CancellationToken cancellationToken) =>
        viewModel.RepairStartupFromWebAsync(cancellationToken);
}

public sealed class WebSettingsSnapshotSource(MainWindowViewModel viewModel) : IWebSettingsSnapshotSource
{
    public WebSettingsSnapshotValues Read()
    {
        var language = viewModel.LanguageOptions.FirstOrDefault(option =>
            string.Equals(option.Code, viewModel.SelectedLanguageCode, StringComparison.OrdinalIgnoreCase));
        return new(
            new(viewModel.SelectedLanguageCode, language?.DisplayText ?? viewModel.SelectedLanguageCode),
            viewModel.ShowLogsInSidebar,
            viewModel.SelectedMainWindowCloseBehavior.ToString(),
            viewModel.CloseBehaviorMessage,
            viewModel.LaunchAtLogin,
            viewModel.CanConfigureWindowsStartup,
            viewModel.CanRepairStartup,
            viewModel.SelectedStartupPresentationMode.ToString(),
            viewModel.StartupBackendDisplay,
            viewModel.StartupHealthDisplay,
            viewModel.StartupSettingsMessage);
    }
}

public sealed class WebSettingsSnapshotProvider(IWebSettingsSnapshotSource source, TimeProvider timeProvider)
{
    public WebSettingsSnapshot Create()
    {
        var value = source.Read();
        return new(timeProvider.GetUtcNow(), value.Language, value.ShowLogsInSidebar,
            value.MainWindowCloseBehavior, value.CloseBehaviorMessage, value.LaunchAtLogin,
            value.CanConfigureLaunchAtLogin, value.CanRepairStartup, value.StartupPresentationMode, value.StartupBackend,
            value.StartupStatus, value.StartupMessage);
    }
}
