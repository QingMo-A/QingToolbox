using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell.WebShell;

public sealed class WebHostUpdateOperations(MainWindowViewModel viewModel, TimeProvider timeProvider)
{
    public WebHostUpdateSnapshot CreateSnapshot() => new(
        timeProvider.GetUtcNow(), viewModel.HostUpdateState.ToString(), viewModel.VersionDisplay,
        viewModel.HostUpdateLatestVersion, viewModel.HostUpdatePublishedAt, viewModel.HostUpdateLastChecked,
        viewModel.HostUpdateSummary, viewModel.ShowHostUpdateBanner, viewModel.HostUpdateDownloadState.ToString(),
        Math.Max(0, viewModel.HostUpdateBytesReceived), Math.Max(0, viewModel.HostUpdateExpectedBytes),
        viewModel.HostUpdateDownloadError, viewModel.CheckHostUpdateCommand.CanExecute(null),
        viewModel.CanDownloadHostUpdate, viewModel.CanCancelHostUpdateDownload, viewModel.CanInstallHostUpdate,
        viewModel.HostInstallationSupported, viewModel.HostUpdateInstallMessage);

    public async Task<WebHostUpdateSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        await viewModel.CheckHostUpdateFromWebAsync(cancellationToken);
        return CreateSnapshot();
    }

    public async Task<WebHostUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken)
    {
        await viewModel.DownloadHostUpdateFromWebAsync(cancellationToken);
        return CreateSnapshot();
    }

    public WebHostUpdateSnapshot CancelDownload()
    {
        viewModel.CancelHostUpdateDownloadFromWeb();
        return CreateSnapshot();
    }

    public async Task<WebHostUpdateSnapshot> InstallAsync(CancellationToken cancellationToken)
    {
        await viewModel.InstallHostUpdateFromWebAsync(cancellationToken);
        return CreateSnapshot();
    }
}
