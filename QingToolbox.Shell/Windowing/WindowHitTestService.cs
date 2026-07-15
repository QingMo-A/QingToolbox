using System.Windows;

namespace QingToolbox.Shell.Windowing;

public static class WindowHitTestService
{
    public static Point DecodeScreenPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new Point(
            unchecked((short)(value & 0xFFFF)),
            unchecked((short)((value >> 16) & 0xFFFF)));
    }

    public static bool Contains(Point point, double width, double height) =>
        width > 0 && height > 0 &&
        point.X >= 0 && point.Y >= 0 &&
        point.X <= width && point.Y <= height;
}
