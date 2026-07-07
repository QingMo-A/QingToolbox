using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingToolbox.Modules.ScreenPin;

internal static class ScreenCaptureService
{
    public static BitmapSource Capture(Rect regionDip, Matrix transformToDevice)
    {
        var regionPixel = DipToPixel(regionDip, transformToDevice);
        var width = Math.Max(1, (int)Math.Round(regionPixel.Width));
        var height = Math.Max(1, (int)Math.Round(regionPixel.Height));
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                (int)Math.Round(regionPixel.X),
                (int)Math.Round(regionPixel.Y),
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

    private static Rect DipToPixel(Rect regionDip, Matrix transformToDevice)
    {
        if (transformToDevice.IsIdentity)
        {
            return regionDip;
        }

        // TODO: Per-monitor mixed-DPI setups can require per-screen transforms. This
        // keeps single-monitor 100%/125%/150% captures aligned by separating WPF DIP
        // geometry from physical capture pixels.
        var topLeft = transformToDevice.Transform(regionDip.TopLeft);
        var bottomRight = transformToDevice.Transform(regionDip.BottomRight);
        return new Rect(topLeft, bottomRight);
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
