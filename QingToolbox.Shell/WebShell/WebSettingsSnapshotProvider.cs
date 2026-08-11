using System.IO;
using QingToolbox.Core.Settings;
using QingToolbox.Core.Localization;
using QingToolbox.Shell.ViewModels;
using QingToolbox.Shell.Services;

namespace QingToolbox.Shell.WebShell;

public sealed record WebSettingsSnapshotValues(WebSettingsLanguage Language, string AppearancePresetId,
    bool ShowLogsInSidebar,
    string MainWindowCloseBehavior, string CloseBehaviorMessage, bool LaunchAtLogin,
    bool CanConfigureLaunchAtLogin, bool CanRepairStartup, string StartupPresentationMode, string StartupBackend,
    string StartupStatus, string StartupMessage);

public sealed record WebFontSnapshot(WebFontSelection Current, IReadOnlyList<WebFontOption> Options);
public interface IWebFontSettingsSnapshotSource { WebFontSnapshot Read(); }
public interface IWebFontSettingsOperations
{
    Task<WebFontMutationResult> SetAsync(string id, CancellationToken cancellationToken);
    Task<WebFontMutationResult> ImportAsync(CancellationToken cancellationToken);
    Task<WebFontMutationResult> RefreshAsync(CancellationToken cancellationToken);
}
public enum WebFontMutationResult { Succeeded, Cancelled, Invalid, Failed }

public interface IWebSettingsSnapshotSource { WebSettingsSnapshotValues Read(); }
public interface IWebSettingsMutation
{
    string LanguageCode { get; }
    string AppearancePresetId { get; }
    bool ShowLogsInSidebar { get; }
    MainWindowCloseBehavior MainWindowCloseBehavior { get; }
    StartupPresentationMode StartupPresentationMode { get; }
    bool LaunchAtLogin { get; }
    bool CanConfigureLaunchAtLogin { get; }
    bool CanRepairStartup { get; }
    Task<WebSettingsMutationResult> SetLanguageAsync(string languageCode, CancellationToken cancellationToken);
    Task<WebSettingsMutationResult> SetAppearancePresetAsync(string presetId, CancellationToken cancellationToken);
    Task SetShowLogsInSidebarAsync(bool value, CancellationToken cancellationToken);
    Task SetMainWindowCloseBehaviorAsync(MainWindowCloseBehavior value, CancellationToken cancellationToken);
    Task SetStartupPresentationModeAsync(StartupPresentationMode value, CancellationToken cancellationToken);
    Task<WebSettingsMutationResult> SetLaunchAtLoginAsync(bool value, CancellationToken cancellationToken);
    Task<WebSettingsMutationResult> RepairStartupAsync(CancellationToken cancellationToken);
}

public enum WebSettingsMutationResult { Succeeded, Unavailable, Failed }

public sealed class WebSettingsMutation(MainWindowViewModel viewModel) : IWebSettingsMutation
{
    public string LanguageCode => viewModel.SelectedLanguageCode;
    public string AppearancePresetId => viewModel.AppearancePresetId;
    public bool ShowLogsInSidebar => viewModel.ShowLogsInSidebar;
    public MainWindowCloseBehavior MainWindowCloseBehavior => viewModel.SelectedMainWindowCloseBehavior;
    public StartupPresentationMode StartupPresentationMode => viewModel.SelectedStartupPresentationMode;
    public bool LaunchAtLogin => viewModel.LaunchAtLogin;
    public bool CanConfigureLaunchAtLogin => viewModel.CanConfigureWindowsStartup;
    public bool CanRepairStartup => viewModel.CanRepairStartup;
    public Task<WebSettingsMutationResult> SetLanguageAsync(string languageCode, CancellationToken cancellationToken) =>
        viewModel.SetLanguageFromWebAsync(languageCode, cancellationToken);
    public Task<WebSettingsMutationResult> SetAppearancePresetAsync(string presetId, CancellationToken cancellationToken) =>
        viewModel.SetAppearancePresetFromWebAsync(presetId, cancellationToken);
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

public sealed class WebSettingsSnapshotSource(
    MainWindowViewModel viewModel,
    LocalizationManager localizationManager) : IWebSettingsSnapshotSource
{
    public WebSettingsSnapshotValues Read()
    {
        var language = localizationManager.SupportedLanguages.First(option =>
            string.Equals(option.Code, localizationManager.ConfiguredLanguageCode, StringComparison.OrdinalIgnoreCase));
        var options = localizationManager.SupportedLanguages
            .Select(option => new WebSettingsLanguageOption(option.Code, option.DisplayName, option.NativeName))
            .ToArray();
        return new(
            new(language.Code, localizationManager.CurrentLanguageCode, language.DisplayName, options),
            viewModel.AppearancePresetId,
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

public sealed class WebSettingsSnapshotProvider(IWebSettingsSnapshotSource source, TimeProvider timeProvider,
    IWebFontSettingsSnapshotSource? fonts = null)
{
    public WebSettingsSnapshot Create()
    {
        var value = source.Read();
        var font = fonts?.Read();
        return new(timeProvider.GetUtcNow(), value.Language, value.AppearancePresetId, value.ShowLogsInSidebar,
            value.MainWindowCloseBehavior, value.CloseBehaviorMessage, value.LaunchAtLogin,
            value.CanConfigureLaunchAtLogin, value.CanRepairStartup, value.StartupPresentationMode, value.StartupBackend,
            value.StartupStatus, value.StartupMessage, font?.Current, font?.Options);
    }
}

public sealed class WebFontSettingsAdapter(FontSettingsService service) :
    IWebFontSettingsSnapshotSource, IWebFontSettingsOperations
{
    public WebFontSnapshot Read()
    {
        var snapshot = service.CreateSnapshot();
        static WebFontOption Project(FontSettingsService.FontCatalogItem item) =>
            new(item.Id, item.Source, item.DisplayName, item.FamilyName, item.ResourceUrl);
        var options = snapshot.Options.Select(Project).ToArray();
        var current = Project(snapshot.Current);
        return new(new(current.Id, current.Source, current.DisplayName, current.FamilyName, current.ResourceUrl), options);
    }

    public async Task<WebFontMutationResult> SetAsync(string id, CancellationToken cancellationToken)
    {
        var result = await service.SetAsync(id, cancellationToken).ConfigureAwait(false);
        return result.Disposition switch
        {
            FontSettingsService.FontSelectionDisposition.Succeeded => WebFontMutationResult.Succeeded,
            FontSettingsService.FontSelectionDisposition.Invalid => WebFontMutationResult.Invalid,
            _ => WebFontMutationResult.Failed
        };
    }

    public async Task<WebFontMutationResult> ImportAsync(CancellationToken cancellationToken)
    {
        var result = await service.ImportAsync(cancellationToken).ConfigureAwait(false);
        return result.Disposition switch
        {
            FontSettingsService.FontImportDisposition.Imported => WebFontMutationResult.Succeeded,
            FontSettingsService.FontImportDisposition.Cancelled => WebFontMutationResult.Cancelled,
            FontSettingsService.FontImportDisposition.Invalid => WebFontMutationResult.Invalid,
            _ => WebFontMutationResult.Failed
        };
    }

    public async Task<WebFontMutationResult> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await service.RefreshSystemFontsAsync(cancellationToken).ConfigureAwait(false);
            return WebFontMutationResult.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException)
        { return WebFontMutationResult.Failed; }
    }
}
