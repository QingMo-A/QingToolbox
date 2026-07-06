using System.Runtime.InteropServices;
using System.Text;

namespace QingToolbox.Modules.WindowTopmost;

internal static class NativeMethods
{
    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    internal const int GwlExStyle = -20;
    internal const long WsExTopmost = 0x00000008L;
    internal static readonly nint HwndTopmost = new(-1);
    internal static readonly nint HwndNoTopmost = new(-2);
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    internal const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X; public int Y; }
}
