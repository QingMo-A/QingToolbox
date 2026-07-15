using System.Runtime.InteropServices;

namespace QingToolbox.Shell.Windowing;

internal static class NativeWindowMessages
{
    internal const int WindowNonClientHitTest = 0x0084;
    internal const int WindowNonClientMouseMove = 0x00A0;
    internal const int WindowNonClientLeftButtonDown = 0x00A1;
    internal const int WindowNonClientLeftButtonUp = 0x00A2;
    internal const int WindowNonClientMouseLeave = 0x02A2;
    internal const int WindowCancelMode = 0x001F;
    internal const int WindowCaptureChanged = 0x0215;
    internal const int HitTestMaximizeButton = 9;
    private const uint TrackMouseEventLeave = 0x00000002;
    private const uint TrackMouseEventNonClient = 0x00000010;

    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData eventTrack);

    internal static void TrackNonClientMouseLeave(IntPtr windowHandle)
    {
        var eventTrack = new TrackMouseEventData
        {
            Size = (uint)Marshal.SizeOf<TrackMouseEventData>(),
            Flags = TrackMouseEventLeave | TrackMouseEventNonClient,
            WindowHandle = windowHandle
        };
        TrackMouseEvent(ref eventTrack);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        internal uint Size;
        internal uint Flags;
        internal IntPtr WindowHandle;
        internal uint HoverTime;
    }
}
