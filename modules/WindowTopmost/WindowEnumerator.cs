using System.Diagnostics;
using System.Text;

namespace QingToolbox.Modules.WindowTopmost;

internal static class WindowEnumerator
{
    public static IReadOnlyList<WindowInfo> Enumerate()
    {
        var result = new List<WindowInfo>();
        var currentPid = Environment.ProcessId;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            var length = NativeMethods.GetWindowTextLength(hwnd);
            if (length <= 0) return true;
            var title = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == currentPid) return true;
            string processName;
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { processName = "Unknown"; }
            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
            result.Add(new WindowInfo(
                hwnd,
                title.ToString(),
                processName,
                (style & NativeMethods.WsExTopmost) != 0));
            return true;
        }, 0);
        return result.OrderBy(item => item.Title).ToArray();
    }
}
