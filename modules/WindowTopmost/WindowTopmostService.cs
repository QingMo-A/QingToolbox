namespace QingToolbox.Modules.WindowTopmost;

internal static class WindowTopmostService
{
    public static bool SetTopmost(nint hwnd, bool topmost) =>
        NativeMethods.SetWindowPos(
            hwnd,
            topmost ? NativeMethods.HwndTopmost : NativeMethods.HwndNoTopmost,
            0, 0, 0, 0,
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoActivate);

    public static nint GetWindowUnderCursor()
    {
        return NativeMethods.GetCursorPos(out var point)
            ? NativeMethods.GetAncestor(
                NativeMethods.WindowFromPoint(point),
                NativeMethods.GaRoot)
            : 0;
    }
}
