using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Runtime;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Core.Updates;
using QingToolbox.Shell.Startup;
using QingToolbox.Shell.Services;

namespace QingToolbox.Shell.ViewModels;

public enum StartupAuthorizationState { NotEnabled, Enabled, ChangedNeedsConfirmation, Missing, Unavailable }

public sealed partial class DiscoveredModuleViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    public DiscoveredModuleViewModel(
        DiscoveredModule module,
        ILocalizationService localization,
        IReadOnlyList<string>? localizationDiagnostics = null,
        bool downloadsDisabled = false)
    {
        Module = module;
        _localization = localization;
        Id = module.Manifest.Id;
        Name = module.Manifest.Name;
        Version = module.Manifest.Version;
        Description = module.Manifest.Description ??
            localization.GetString("module.noDescription");
        RuntimeType = module.Manifest.RuntimeType.ToString();
        LoadMode = module.Manifest.LoadMode.ToString();
        PermissionsText = module.Manifest.Permissions.Count == 0
            ? localization.GetString("module.noPermissions")
            : string.Join(", ", module.Manifest.Permissions);
        Entry = module.Manifest.Entry;
        Author = module.Manifest.Author ??
            localization.GetString("module.unknownAuthor");
        MinimumHostVersion = module.Manifest.MinimumHostVersion ??
            localization.GetString("module.notSpecified");
        State = module.State.ToString();
        var localizationErrors = localizationDiagnostics?
            .Select(error => $"Localization: {error}") ??
            [];
        Errors = module.Errors
            .Select(error => $"{error.Code}: {error.Message}")
            .Concat(localizationErrors)
            .ToArray();
        ErrorCount = Errors.Count;
        HasErrors = ErrorCount > 0;
        ErrorSummary = HasErrors
            ? $"{ErrorCount} issue(s)"
            : localization.GetString("module.noIssues");
        StateBadgeText = State;
        IsValid = module.IsValid;
        ModuleDirectory = module.ModuleDirectory;
        ManifestPath = module.ManifestPath;
        IconPath = ResolveIconPath(module);
        _runtimeState = State;
        _updateResult = new(module.Manifest.Id, ModuleUpdateStatus.NotChecked);
        _downloadsDisabled = downloadsDisabled;
    }

    public bool IsUserInstalled { get; internal set; }
    public bool CanRemove => IsUserInstalled && !IsBusy && !IsExecutionBlocked;

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Description { get; }
    public string DisplayName =>
        _localization.GetModuleString(Id, "module.name", Name);
    public string DisplayDescription =>
        _localization.GetModuleString(
            Id,
            "module.description",
            Description);
    public string RuntimeType { get; }
    public string LoadMode { get; }
    public string PermissionsText { get; }
    public string Entry { get; }
    public string Author { get; }
    public string MinimumHostVersion { get; }
    public string State { get; }
    public string StateBadgeText { get; }
    public int ErrorCount { get; }
    public bool HasErrors { get; }
    public string ErrorSummary { get; }
    public bool IsValid { get; }
    public string ModuleDirectory { get; }
    public string ManifestPath { get; }
    public string? IconPath { get; }
    public bool HasIcon => !string.IsNullOrWhiteSpace(IconPath);
    public IReadOnlyList<string> Errors { get; }
    internal DiscoveredModule Module { get; }

    [ObservableProperty] private bool _isStartupEnabled;
    [ObservableProperty] private bool _isStartupAuthorizationBusy;
    [ObservableProperty] private string _startupAuthorizationMessage = string.Empty;
    [ObservableProperty] private StartupAuthorizationState _startupAuthorizationState;
    public bool CanChangeStartupAuthorization => IsValid && !IsExecutionBlocked &&
        StartupAuthorizationState != StartupAuthorizationState.Unavailable && !IsStartupAuthorizationBusy;

    [ObservableProperty]
    private string _runtimeState;

    [ObservableProperty]
    private string _runtimeError = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private ModuleExecutionReadinessStatus _executionReadinessStatus =
        ModuleExecutionReadinessStatus.RecoveryPending;

    public bool IsExecutionBlocked =>
        ExecutionReadinessStatus != ModuleExecutionReadinessStatus.Ready;

    public string ExecutionReadinessMessage => ExecutionReadinessStatus switch
    {
        ModuleExecutionReadinessStatus.RecoveryPending =>
            _localization.GetString("module.recoveryPending"),
        ModuleExecutionReadinessStatus.BlockedByModuleRecovery =>
            _localization.GetString("module.recoveryBlocked"),
        ModuleExecutionReadinessStatus.BlockedByUnattributedRecoveryFailure =>
            _localization.GetString("module.recoveryGloballyBlocked"),
        _ => string.Empty
    };

    [ObservableProperty]
    private ModuleUpdateResult _updateResult;
    private readonly bool _downloadsDisabled;
    [ObservableProperty] private ModulePackageDownloadStatus _downloadStatus = ModulePackageDownloadStatus.NotDownloaded;
    [ObservableProperty] private long _downloadBytesReceived;
    [ObservableProperty] private long _downloadExpectedBytes;
    public bool CanDownloadUpdate => !_downloadsDisabled && DownloadStatus is not (ModulePackageDownloadStatus.ConfirmingMetadata or
        ModulePackageDownloadStatus.Downloading or ModulePackageDownloadStatus.Verifying or ModulePackageDownloadStatus.Verified or ModulePackageDownloadStatus.AlreadyVerified) && UpdateResult.Status == ModuleUpdateStatus.UpdateAvailable &&
        UpdateResult.SelectedRelease is not null && !UpdateResult.IsFromStaleCache;
    public bool IsDownloadActive => DownloadStatus is ModulePackageDownloadStatus.ConfirmingMetadata or ModulePackageDownloadStatus.Downloading or ModulePackageDownloadStatus.Verifying;
    public bool HasDownloadStatus => DownloadStatus != ModulePackageDownloadStatus.NotDownloaded;
    public string DisplayDownloadStatus => _localization.GetString($"moduleDownload.{DownloadStatus switch
    {
        ModulePackageDownloadStatus.ConfirmingMetadata => "confirmingMetadata",
        ModulePackageDownloadStatus.MetadataChanged => "metadataChanged",
        ModulePackageDownloadStatus.MetadataStale => "metadataStale",
        ModulePackageDownloadStatus.Downloading => "downloading",
        ModulePackageDownloadStatus.Verifying => "verifying",
        ModulePackageDownloadStatus.Verified => "verified",
        ModulePackageDownloadStatus.AlreadyVerified => "alreadyVerified",
        ModulePackageDownloadStatus.SizeMismatch => "sizeMismatch",
        ModulePackageDownloadStatus.HashMismatch => "hashMismatch",
        ModulePackageDownloadStatus.UntrustedRedirect => "untrustedRedirect",
        ModulePackageDownloadStatus.SourceUnavailable => "sourceUnavailable",
        ModulePackageDownloadStatus.SourceInvalid => "sourceInvalid",
        ModulePackageDownloadStatus.StorageUnavailable => "storageUnavailable",
        ModulePackageDownloadStatus.Cancelled => "cancelled",
        ModulePackageDownloadStatus.TransferTimedOut => "transferTimedOut",
        ModulePackageDownloadStatus.Failed => "failed",
        ModulePackageDownloadStatus.DisabledByEnvironment => "disabled",
        _ => "sourceUnavailable"
    }}");
    public string DisplayDownloadProgress => _localization.GetString("moduleDownload.progress", DownloadBytesReceived, DownloadExpectedBytes,
        DownloadExpectedBytes > 0 ? Math.Min(100, DownloadBytesReceived * 100d / DownloadExpectedBytes).ToString("F0") : "0");

    public string DisplayUpdateStatus => _localization.GetString(
        $"moduleUpdate.status.{UpdateResult.Status}",
        UpdateResult.TargetVersion?.ToString() ?? string.Empty);
    public string DisplayUpdateReleaseNote
    {
        get
        {
            if (UpdateResult.Status is not (ModuleUpdateStatus.UpdateAvailable or ModuleUpdateStatus.HostUpdateRequired or
                ModuleUpdateStatus.HostVersionIncompatible or ModuleUpdateStatus.ModuleApiIncompatible) || UpdateResult.ReleaseNotes is null)
                return string.Empty;
            var language = _localization.CurrentLanguageCode;
            if (UpdateResult.ReleaseNotes.TryGetValue(language, out var localized) && !string.IsNullOrWhiteSpace(localized)) return localized;
            if (UpdateResult.ReleaseNotes.TryGetValue("en-US", out var english) && !string.IsNullOrWhiteSpace(english)) return english;
            return UpdateResult.ReleaseNotes.Values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }
    }
    public bool HasUpdateReleaseNote => !string.IsNullOrWhiteSpace(DisplayUpdateReleaseNote);

    public bool HasRuntimeError => !string.IsNullOrWhiteSpace(RuntimeError);

    public string DisplayRuntimeState => _localization.GetString(
        RuntimeState switch
        {
            "NotLoaded" => "runtimeState.notLoaded",
            "Loaded" => "runtimeState.loaded",
            "Running" => "runtimeState.running",
            "Deactivated" => "runtimeState.deactivated",
            "Unloading" => "runtimeState.unloading",
            "Unloaded" => "runtimeState.unloaded",
            "Failed" => "runtimeState.failed",
            _ => RuntimeState
        });

    public bool CanLoad =>
        !IsExecutionBlocked && !IsBusy && RuntimeState is "NotLoaded" or "Unloaded";

    public bool CanActivate =>
        !IsExecutionBlocked && !IsBusy && RuntimeState is "Loaded" or "Deactivated";

    public bool CanDeactivate =>
        !IsExecutionBlocked && !IsBusy && RuntimeState == "Running";

    public bool CanUnload =>
        !IsExecutionBlocked && !IsBusy && RuntimeState is "Loaded" or "Running" or "Deactivated" or "Failed";

    public bool CanOpen =>
        !IsExecutionBlocked && !IsBusy && RuntimeState is "Loaded" or "Running" or "Deactivated";

    public void UpdateRuntimeState(ModuleRuntimeRecord? record)
    {
        RuntimeState = record?.State.ToString() ?? State;
        RuntimeError = record?.LastError ?? string.Empty;
    }

    public void UpdateOutOfProcessRuntimeState(ModuleProcessRuntimeState? state)
    {
        RuntimeState = state switch
        {
            null => "NotLoaded",
            { IsActive: true } => "Running",
            { ModuleLoaded: true } => "Loaded",
            _ => "Unloaded"
        };
        RuntimeError = string.Empty;
    }

    public void UpdateExecutionReadiness(ModuleExecutionReadiness readiness)
    {
        if (!string.Equals(readiness.ModuleId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("The readiness result belongs to another module.", nameof(readiness));
        }

        ExecutionReadinessStatus = readiness.Status;
        if (IsExecutionBlocked)
        {
            RuntimeError = ExecutionReadinessMessage;
        }
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplayDescription));
        OnPropertyChanged(nameof(DisplayRuntimeState));
        OnPropertyChanged(nameof(DisplayUpdateStatus));
        OnPropertyChanged(nameof(DisplayUpdateReleaseNote));
        OnPropertyChanged(nameof(DisplayDownloadStatus));
        OnPropertyChanged(nameof(DisplayDownloadProgress));
        OnPropertyChanged(nameof(ExecutionReadinessMessage));
    }

    partial void OnUpdateResultChanged(ModuleUpdateResult value)
    {
        if (DownloadStatus is ModulePackageDownloadStatus.Verified or ModulePackageDownloadStatus.AlreadyVerified)
            DownloadStatus = ModulePackageDownloadStatus.NotDownloaded;
        OnPropertyChanged(nameof(DisplayUpdateStatus));
        OnPropertyChanged(nameof(DisplayUpdateReleaseNote));
        OnPropertyChanged(nameof(HasUpdateReleaseNote));
        OnPropertyChanged(nameof(CanDownloadUpdate));
    }

    partial void OnDownloadStatusChanged(ModulePackageDownloadStatus value)
    { OnPropertyChanged(nameof(CanDownloadUpdate)); OnPropertyChanged(nameof(IsDownloadActive)); OnPropertyChanged(nameof(HasDownloadStatus)); OnPropertyChanged(nameof(DisplayDownloadStatus)); }
    partial void OnDownloadBytesReceivedChanged(long value) => OnPropertyChanged(nameof(DisplayDownloadProgress));
    partial void OnDownloadExpectedBytesChanged(long value) => OnPropertyChanged(nameof(DisplayDownloadProgress));

    partial void OnRuntimeStateChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayRuntimeState));
        NotifyCommandStatesChanged();
    }

    partial void OnRuntimeErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasRuntimeError));
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCommandStatesChanged();
        OnPropertyChanged(nameof(CanRemove));
    }

    partial void OnExecutionReadinessStatusChanged(ModuleExecutionReadinessStatus value)
    {
        OnPropertyChanged(nameof(IsExecutionBlocked));
        OnPropertyChanged(nameof(ExecutionReadinessMessage));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanChangeStartupAuthorization));
        NotifyCommandStatesChanged();
    }

    partial void OnIsStartupAuthorizationBusyChanged(bool value) =>
        OnPropertyChanged(nameof(CanChangeStartupAuthorization));

    partial void OnStartupAuthorizationStateChanged(StartupAuthorizationState value) =>
        OnPropertyChanged(nameof(CanChangeStartupAuthorization));

    private void NotifyCommandStatesChanged()
    {
        OnPropertyChanged(nameof(CanLoad));
        OnPropertyChanged(nameof(CanActivate));
        OnPropertyChanged(nameof(CanDeactivate));
        OnPropertyChanged(nameof(CanUnload));
        OnPropertyChanged(nameof(CanOpen));
    }

    private static string? ResolveIconPath(DiscoveredModule module)
    {
        if (string.IsNullOrWhiteSpace(module.Manifest.Icon) ||
            !string.Equals(
                Path.GetExtension(module.Manifest.Icon),
                ".svg",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var moduleDirectory = Path.GetFullPath(module.ModuleDirectory);
        var iconPath = Path.GetFullPath(
            Path.Combine(moduleDirectory, module.Manifest.Icon));
        var modulePrefix = moduleDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return iconPath.StartsWith(
                modulePrefix,
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(iconPath)
                ? iconPath
                : null;
    }
}
