using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell.WebShell;

public sealed record WebSettingsSnapshotValues(WebSettingsLanguage Language, bool ShowLogsInSidebar,
    string MainWindowCloseBehavior, string CloseBehaviorMessage, bool LaunchAtLogin,
    bool CanConfigureLaunchAtLogin, string StartupPresentationMode, string StartupBackend,
    string StartupStatus, string StartupMessage);

public interface IWebSettingsSnapshotSource { WebSettingsSnapshotValues Read(); }
public interface IWebSettingsMutation { bool ShowLogsInSidebar { get; } Task SetShowLogsInSidebarAsync(bool value, CancellationToken cancellationToken); }

public sealed class WebSettingsMutation(MainWindowViewModel viewModel) : IWebSettingsMutation
{
    public bool ShowLogsInSidebar => viewModel.ShowLogsInSidebar;
    public Task SetShowLogsInSidebarAsync(bool value, CancellationToken cancellationToken) =>
        viewModel.SetShowLogsInSidebarAsync(value, cancellationToken);
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
            value.CanConfigureLaunchAtLogin, value.StartupPresentationMode, value.StartupBackend,
            value.StartupStatus, value.StartupMessage);
    }
}
