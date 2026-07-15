using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace QingToolbox.Shell.Windowing;

public sealed record MonitorWorkArea(
    string DeviceName,
    Rect PixelBounds,
    Rect PixelWorkArea,
    double DpiX,
    double DpiY)
{
    public double ScaleX => DpiX > 0 ? DpiX / 96d : 1d;
    public double ScaleY => DpiY > 0 ? DpiY / 96d : 1d;
    public Rect LocalWorkAreaInDips => new(
        0, 0, PixelWorkArea.Width / ScaleX, PixelWorkArea.Height / ScaleY);
}

public static class FloatingBadgePlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static Point Initial(Rect workArea, Size badgeSize, double margin = 24) =>
        Constrain(new Point(workArea.Right - badgeSize.Width - margin, workArea.Top + margin), workArea, badgeSize);

    public static Point Constrain(Point requested, Rect workArea, Size badgeSize)
    {
        var fallback = new Point(workArea.Left, workArea.Top);
        if (!IsFinite(requested.X, requested.Y, badgeSize.Width, badgeSize.Height,
                workArea.Left, workArea.Top, workArea.Width, workArea.Height)) return fallback;
        var maxX = Math.Max(workArea.Left, workArea.Right - Math.Max(0, badgeSize.Width));
        var maxY = Math.Max(workArea.Top, workArea.Bottom - Math.Max(0, badgeSize.Height));
        return new Point(Math.Clamp(requested.X, workArea.Left, maxX), Math.Clamp(requested.Y, workArea.Top, maxY));
    }

    public static Point PositionFromRatios(
        MonitorWorkArea monitor, Size badgePixelSize, double? ratioX, double? ratioY)
    {
        var work = monitor.PixelWorkArea;
        var availableWidth = Math.Max(0, work.Width - Math.Max(0, badgePixelSize.Width));
        var availableHeight = Math.Max(0, work.Height - Math.Max(0, badgePixelSize.Height));
        var x = ValidRatio(ratioX) ?? 1;
        var y = ValidRatio(ratioY) ?? 0;
        return new Point(work.Left + availableWidth * x, work.Top + availableHeight * y);
    }

    public static (double Horizontal, double Vertical) RatiosFromPosition(
        MonitorWorkArea monitor, Point pixelPosition, Size badgePixelSize)
    {
        var work = monitor.PixelWorkArea;
        var availableWidth = Math.Max(0, work.Width - Math.Max(0, badgePixelSize.Width));
        var availableHeight = Math.Max(0, work.Height - Math.Max(0, badgePixelSize.Height));
        var horizontal = availableWidth <= 0 ? 0 : (pixelPosition.X - work.Left) / availableWidth;
        var vertical = availableHeight <= 0 ? 0 : (pixelPosition.Y - work.Top) / availableHeight;
        return (Math.Clamp(FiniteOrZero(horizontal), 0, 1), Math.Clamp(FiniteOrZero(vertical), 0, 1));
    }

    public static Rect ConstrainWindowBounds(Rect requested, Rect workArea, Size minimumSize)
    {
        if (!IsFinite(requested.Left, requested.Top, requested.Width, requested.Height,
                workArea.Left, workArea.Top, workArea.Width, workArea.Height))
            requested = new Rect(workArea.TopLeft, workArea.Size);
        var minimumWidth = Math.Min(Math.Max(0, minimumSize.Width), Math.Max(0, workArea.Width));
        var minimumHeight = Math.Min(Math.Max(0, minimumSize.Height), Math.Max(0, workArea.Height));
        var width = Math.Clamp(requested.Width, minimumWidth, Math.Max(minimumWidth, workArea.Width));
        var height = Math.Clamp(requested.Height, minimumHeight, Math.Max(minimumHeight, workArea.Height));
        var position = Constrain(requested.TopLeft, workArea, new Size(width, height));
        return new Rect(position, new Size(width, height));
    }

    public static MonitorWorkArea ResolveMonitor(string? deviceName, Point fallbackPixel)
    {
        var monitors = GetMonitors();
        var saved = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        return saved ?? GetNearestMonitor(fallbackPixel, monitors);
    }

    public static MonitorWorkArea GetMonitorAt(Point pixelPoint) =>
        GetNearestMonitor(pixelPoint, GetMonitors());

    public static IReadOnlyList<MonitorWorkArea> GetMonitors()
    {
        var monitors = new List<MonitorWorkArea>();
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(handle, ref info)) return true;
            var dpiX = 96u;
            var dpiY = 96u;
            try { if (GetDpiForMonitor(handle, 0, out dpiX, out dpiY) != 0) (dpiX, dpiY) = (96, 96); }
            catch (DllNotFoundException) { (dpiX, dpiY) = (96, 96); }
            catch (EntryPointNotFoundException) { (dpiX, dpiY) = (96, 96); }
            monitors.Add(new MonitorWorkArea(
                info.DeviceName,
                ToRect(info.Monitor),
                ToRect(info.Work),
                dpiX == 0 ? 96 : dpiX,
                dpiY == 0 ? 96 : dpiY));
            return true;
        }, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            var work = SystemParameters.WorkArea;
            monitors.Add(new MonitorWorkArea("DISPLAY", work, work, 96, 96));
        }
        return monitors;
    }

    public static void SetBadgePixelPosition(Window badge, MonitorWorkArea monitor, Point requestedPixel)
    {
        var source = (HwndSource?)PresentationSource.FromVisual(badge);
        if (source is null) return;
        var pixelSize = GetBadgePixelSize(badge, monitor);
        var constrained = Constrain(requestedPixel, monitor.PixelWorkArea, pixelSize);
        if (!SetWindowPos(source.Handle, IntPtr.Zero, (int)Math.Round(constrained.X), (int)Math.Round(constrained.Y), 0, 0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public static Size GetBadgePixelSize(Window badge, MonitorWorkArea monitor) =>
        new(Math.Max(1, badge.Width * monitor.ScaleX), Math.Max(1, badge.Height * monitor.ScaleY));

    public static Point GetWindowPixelTopLeft(Window window)
    {
        var source = (HwndSource?)PresentationSource.FromVisual(window);
        if (source is not null && GetWindowRect(source.Handle, out var rect)) return new Point(rect.Left, rect.Top);
        return window.PointToScreen(new Point());
    }

    public static Rect MonitorWorkAreaToWindowDips(MonitorWorkArea monitor, Visual visual)
    {
        if (visual is not Window window || PresentationSource.FromVisual(visual) is null)
            return monitor.LocalWorkAreaInDips;

        // PointFromScreen performs the HWND-aware conversion. Adding the returned
        // client delta to WPF's window position avoids scaling virtual-desktop
        // absolute coordinates with a different monitor's DPI.
        var localTopLeft = window.PointFromScreen(monitor.PixelWorkArea.TopLeft);
        var localBottomRight = window.PointFromScreen(monitor.PixelWorkArea.BottomRight);
        var topLeft = new Point(window.Left + localTopLeft.X, window.Top + localTopLeft.Y);
        var bottomRight = new Point(window.Left + localBottomRight.X, window.Top + localBottomRight.Y);
        return new Rect(topLeft, bottomRight);
    }

    private static MonitorWorkArea GetNearestMonitor(Point point, IReadOnlyList<MonitorWorkArea> monitors)
    {
        var handle = MonitorFromPoint(new NativePoint((int)point.X, (int)point.Y), MonitorDefaultToNearest);
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (handle != IntPtr.Zero && GetMonitorInfo(handle, ref info))
        {
            var match = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, info.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return monitors[0];
    }

    private static double? ValidRatio(double? value) => value is { } v && double.IsFinite(v) ? Math.Clamp(v, 0, 1) : null;
    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
    private static bool IsFinite(params double[] values) => values.All(double.IsFinite);
    private static Rect ToRect(NativeRect value) => new(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll", SetLastError = true)]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
    }
}
