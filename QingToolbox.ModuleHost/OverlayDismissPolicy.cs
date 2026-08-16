using QingToolbox.Abstractions.Modules;

namespace QingToolbox.ModuleHost;

internal enum OverlayDismissDecision
{
    Keep,
    Hide,
    Defer,
}

internal static class OverlayDismissPolicy
{
    public const long DropGraceMilliseconds = 500;

    public static OverlayDismissDecision OnDeactivated(
        ModuleHostWindowPresentationMode mode,
        bool closed,
        bool visible,
        bool externalFileDragActive,
        bool leftButtonDown)
    {
        if (!WebModuleWindowPresentation.IsOverlay(mode) || closed || !visible || externalFileDragActive)
            return OverlayDismissDecision.Keep;
        return leftButtonDown ? OverlayDismissDecision.Defer : OverlayDismissDecision.Hide;
    }

    public static OverlayDismissDecision OnDeferredTick(
        bool closed,
        bool visible,
        bool active,
        bool externalFileDragActive,
        bool leftButtonDown,
        long elapsedSinceExternalDropMilliseconds)
    {
        if (closed || !visible || active)
            return OverlayDismissDecision.Keep;
        if (leftButtonDown) return OverlayDismissDecision.Defer;
        if (externalFileDragActive) return OverlayDismissDecision.Defer;
        return elapsedSinceExternalDropMilliseconds is >= 0 and < DropGraceMilliseconds
            ? OverlayDismissDecision.Keep
            : OverlayDismissDecision.Hide;
    }
}
