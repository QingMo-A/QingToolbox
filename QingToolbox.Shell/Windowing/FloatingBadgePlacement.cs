using System.Runtime.InteropServices;
using System.Windows;

namespace QingToolbox.Shell.Windowing;

public static class FloatingBadgePlacement
{
    private const uint MonitorDefaultToNearest = 2;

    public static Point Initial(Rect workArea, Size badgeSize, double margin = 24) =>
        Constrain(
            new Point(workArea.Right - badgeSize.Width - margin, workArea.Top + margin),
            workArea,
            badgeSize);

    public static Point Constrain(Point requested, Rect workArea, Size badgeSize)
    {
        var fallback = new Point(workArea.Left, workArea.Top);
        if (!double.IsFinite(requested.X) || !double.IsFinite(requested.Y) ||
            !double.IsFinite(badgeSize.Width) || !double.IsFinite(badgeSize.Height))
            return fallback;

        var maxX = Math.Max(workArea.Left, workArea.Right - Math.Max(0, badgeSize.Width));
        var maxY = Math.Max(workArea.Top, workArea.Bottom - Math.Max(0, badgeSize.Height));
        return new Point(
            Math.Clamp(requested.X, workArea.Left, maxX),
            Math.Clamp(requested.Y, workArea.Top, maxY));
    }

    public static Rect GetWorkAreaInDips(Point screenPixel)
    {
        var monitor = MonitorFromPoint(
            new NativePoint((int)screenPixel.X, (int)screenPixel.Y),
            MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return SystemParameters.WorkArea;

        var dpiX = 96u;
        var dpiY = 96u;
        _ = GetDpiForMonitor(monitor, 0, out dpiX, out dpiY);
        var scaleX = dpiX > 0 ? dpiX / 96d : 1d;
        var scaleY = dpiY > 0 ? dpiY / 96d : 1d;
        return new Rect(
            info.Work.Left / scaleX,
            info.Work.Top / scaleY,
            (info.Work.Right - info.Work.Left) / scaleX,
            (info.Work.Bottom - info.Work.Top) / scaleY);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal uint Flags;
    }
}
