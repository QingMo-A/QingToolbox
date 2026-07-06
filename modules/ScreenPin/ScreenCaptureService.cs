using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace QingToolbox.Modules.ScreenPin;

internal static class ScreenCaptureService
{
    public static BitmapSource Capture(Rect region)
    {
        // Coordinates are correct at 100% scaling. Per-monitor DPI refinement is planned.
        var width = Math.Max(1, (int)Math.Round(region.Width));
        var height = Math.Max(1, (int)Math.Round(region.Height));
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                (int)Math.Round(region.X),
                (int)Math.Round(region.Y),
                0,
                0,
                new System.Drawing.Size(width, height),
                CopyPixelOperation.SourceCopy);
        }

        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
