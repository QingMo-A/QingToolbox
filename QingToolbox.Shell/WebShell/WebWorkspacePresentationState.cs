namespace QingToolbox.Shell.WebShell;

internal enum WebWorkspacePresentationPhase
{
    Native,
    Preparing,
    Ready
}

internal readonly record struct WebWorkspacePresentationSnapshot(
    bool ShowNativeWorkspace,
    bool ShowStartupSurface,
    bool AttachWebWorkspace,
    bool EnableWebWorkspace);

internal sealed class WebWorkspacePresentationState(bool webShellAllowed)
{
    public WebWorkspacePresentationPhase Phase { get; private set; } = webShellAllowed
        ? WebWorkspacePresentationPhase.Preparing
        : WebWorkspacePresentationPhase.Native;

    public WebWorkspacePresentationSnapshot Snapshot => Phase switch
    {
        WebWorkspacePresentationPhase.Preparing => new(false, true, true, false),
        WebWorkspacePresentationPhase.Ready => new(false, false, true, true),
        _ => new(true, false, false, false)
    };

    public bool TryPrepare(bool isExiting)
    {
        if (!webShellAllowed || isExiting) return false;
        Phase = WebWorkspacePresentationPhase.Preparing;
        return true;
    }

    public bool TryShowReady(bool isExiting)
    {
        if (!webShellAllowed || isExiting) return false;
        Phase = WebWorkspacePresentationPhase.Ready;
        return true;
    }

    public void ShowNativeFallback() => Phase = WebWorkspacePresentationPhase.Native;
}
