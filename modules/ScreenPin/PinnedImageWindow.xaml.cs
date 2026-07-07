using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace QingToolbox.Modules.ScreenPin;

public partial class PinnedImageWindow : Window
{
    private readonly BitmapSource _image;

    public PinnedImageWindow(BitmapSource image)
    {
        InitializeComponent();

        _image = image;
        PinnedImage.Source = image;
        Width = Math.Max(MinWidth, image.PixelWidth);
        Height = Math.Max(MinHeight, image.PixelHeight);
    }

    private void OnHostMouseEnter(object sender, MouseEventArgs e) => ShowOverlay(true);

    private void OnHostMouseLeave(object sender, MouseEventArgs e) => ShowOverlay(false);

    private void OnImageHostMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractiveElement(e.OriginalSource))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse button state changes during activation.
        }
    }

    private void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        TopmostButton.ToolTip = Topmost ? "取消置顶" : "置顶";
        TopmostButton.Opacity = Topmost ? 1 : 0.72;
        TopmostMenuItem.Header = Topmost ? "取消置顶" : "置顶";
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        Clipboard.SetImage(_image);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void ShowOverlay(bool show)
    {
        OverlayControls.IsHitTestVisible = show;
        var animation = new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = (IEasingFunction)FindResource("OverlayEase")
        };
        OverlayControls.BeginAnimation(OpacityProperty, animation);
    }

    private static bool IsInteractiveElement(object originalSource)
    {
        if (originalSource is not DependencyObject element)
        {
            return false;
        }

        while (element is not null)
        {
            if (element is ButtonBase or Thumb or System.Windows.Controls.ContextMenu or MenuItem)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void OnResizeGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    protected override void OnClosed(EventArgs e)
    {
        PinnedImage.Source = null;
        base.OnClosed(e);
    }
}
