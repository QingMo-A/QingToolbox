using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace QingToolbox.Modules.ScreenPin;

public partial class PinnedImageWindow : Window
{
    private const double PlacementGap = 12;
    private static readonly Geometry PinnedGeometry = Geometry.Parse("M5,2 L13,2 L13,4 L11.8,4 L11.8,9.2 L14,11.4 L14,13 L9.8,13 L9.8,18 L8.2,18 L8.2,13 L4,13 L4,11.4 L6.2,9.2 L6.2,4 L5,4 Z");
    private static readonly Geometry UnpinnedGeometry = Geometry.Parse("M6,3 C6,1.3 7.3,0 9,0 C10.7,0 12,1.3 12,3 L12,5 L10.4,5 L10.4,3 C10.4,2.2 9.8,1.6 9,1.6 C8.2,1.6 7.6,2.2 7.6,3 L7.6,6 L14,6 L14,15 L4,15 L4,6 L6,6 Z M6,8 L6,13 L12,13 L12,8 Z");
    private static readonly Geometry AspectGeometry = Geometry.Parse("M3,3 L9,3 L9,5 L6.4,5 L11,9.6 L9.6,11 L5,6.4 L5,9 L3,9 Z M13,15 L7,15 L7,13 L9.6,13 L5,8.4 L6.4,7 L11,11.6 L11,9 L13,9 Z");
    private static readonly Geometry FreeGeometry = Geometry.Parse("M2,4 L7,4 L7,6 L5.4,6 L8,8.6 L6.6,10 L4,7.4 L4,9 L2,9 Z M16,12 L11,12 L11,10 L12.6,10 L10,7.4 L11.4,6 L14,8.6 L14,7 L16,7 Z M2,14 L2,11 L4,11 L4,12 L7,12 L7,14 Z M11,4 L16,4 L16,6 L11,6 Z");

    private readonly BitmapSource _image;
    private readonly double _aspectRatio;
    private bool _isAspectRatioLocked = true;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeDeltaX;
    private double _resizeDeltaY;

    public PinnedImageWindow(BitmapSource image, Rect selectedRegionDip, Rect virtualScreenDip)
    {
        InitializeComponent();

        _image = image;
        _aspectRatio = Math.Max(0.01, selectedRegionDip.Width / Math.Max(1, selectedRegionDip.Height));

        PinnedImage.Source = image;
        SetInitialSize(selectedRegionDip);
        PlaceNearSelection(selectedRegionDip, virtualScreenDip);
        UpdateTopmostVisualState();
        UpdateResizeModeVisualState();
    }

    private void SetInitialSize(Rect selectedRegionDip)
    {
        var width = Math.Max(1, selectedRegionDip.Width);
        var height = Math.Max(1, selectedRegionDip.Height);

        if (width < MinWidth)
        {
            width = MinWidth;
            height = width / _aspectRatio;
        }

        if (height < MinHeight)
        {
            height = MinHeight;
            width = height * _aspectRatio;
        }

        Width = width;
        Height = height;
    }

    private void PlaceNearSelection(Rect selectedRegionDip, Rect virtualScreenDip)
    {
        var rightLeft = selectedRegionDip.Right + PlacementGap;
        var leftLeft = selectedRegionDip.Left - PlacementGap - Width;
        var belowTop = selectedRegionDip.Bottom + PlacementGap;
        var aboveTop = selectedRegionDip.Top - PlacementGap - Height;

        var desiredLeft = rightLeft;
        var desiredTop = selectedRegionDip.Top;

        if (rightLeft + Width <= virtualScreenDip.Right)
        {
            desiredLeft = rightLeft;
        }
        else if (leftLeft >= virtualScreenDip.Left)
        {
            desiredLeft = leftLeft;
        }
        else if (belowTop + Height <= virtualScreenDip.Bottom)
        {
            desiredLeft = selectedRegionDip.Left;
            desiredTop = belowTop;
        }
        else if (aboveTop >= virtualScreenDip.Top)
        {
            desiredLeft = selectedRegionDip.Left;
            desiredTop = aboveTop;
        }

        Left = Clamp(desiredLeft, virtualScreenDip.Left, virtualScreenDip.Right - Math.Min(Width, virtualScreenDip.Width));
        Top = Clamp(desiredTop, virtualScreenDip.Top, virtualScreenDip.Bottom - Math.Min(Height, virtualScreenDip.Height));
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
        UpdateTopmostVisualState();
    }

    private void OnToggleResizeMode(object sender, RoutedEventArgs e)
    {
        _isAspectRatioLocked = !_isAspectRatioLocked;
        if (_isAspectRatioLocked)
        {
            NormalizeWindowToAspectRatio();
        }

        UpdateResizeModeVisualState();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        Clipboard.SetImage(_image);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnResizeGripDragStarted(object sender, DragStartedEventArgs e)
    {
        _resizeStartWidth = ActualWidth > 0 ? ActualWidth : Width;
        _resizeStartHeight = ActualHeight > 0 ? ActualHeight : Height;
        _resizeDeltaX = 0;
        _resizeDeltaY = 0;
    }

    private void OnResizeGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        _resizeDeltaX += e.HorizontalChange;
        _resizeDeltaY += e.VerticalChange;

        if (_isAspectRatioLocked)
        {
            ResizeKeepingAspectRatio();
            return;
        }

        Width = Math.Max(MinWidth, _resizeStartWidth + _resizeDeltaX);
        Height = Math.Max(MinHeight, _resizeStartHeight + _resizeDeltaY);
    }

    private void ResizeKeepingAspectRatio()
    {
        var widthCandidate = Math.Max(MinWidth, _resizeStartWidth + _resizeDeltaX);
        var heightCandidate = Math.Max(MinHeight, _resizeStartHeight + _resizeDeltaY);
        var widthFromHeight = heightCandidate * _aspectRatio;

        var newWidth = Math.Abs(_resizeDeltaX) >= Math.Abs(_resizeDeltaY)
            ? widthCandidate
            : widthFromHeight;
        var newHeight = newWidth / _aspectRatio;

        if (newHeight < MinHeight)
        {
            newHeight = MinHeight;
            newWidth = newHeight * _aspectRatio;
        }

        if (newWidth < MinWidth)
        {
            newWidth = MinWidth;
            newHeight = newWidth / _aspectRatio;
        }

        Width = newWidth;
        Height = newHeight;
    }

    private void NormalizeWindowToAspectRatio()
    {
        var currentWidth = Math.Max(MinWidth, ActualWidth > 0 ? ActualWidth : Width);
        var currentHeight = Math.Max(MinHeight, ActualHeight > 0 ? ActualHeight : Height);
        var widthByCurrentHeight = currentHeight * _aspectRatio;
        var heightByCurrentWidth = currentWidth / _aspectRatio;

        if (Math.Abs(widthByCurrentHeight - currentWidth) < Math.Abs(heightByCurrentWidth - currentHeight))
        {
            Width = Math.Max(MinWidth, widthByCurrentHeight);
            Height = Math.Max(MinHeight, currentHeight);
        }
        else
        {
            Width = Math.Max(MinWidth, currentWidth);
            Height = Math.Max(MinHeight, heightByCurrentWidth);
        }

        if (Height < MinHeight)
        {
            Height = MinHeight;
            Width = Height * _aspectRatio;
        }

        if (Width < MinWidth)
        {
            Width = MinWidth;
            Height = Width / _aspectRatio;
        }
    }

    private void UpdateTopmostVisualState()
    {
        if (Topmost)
        {
            TopmostIcon.Data = PinnedGeometry;
            TopmostButton.ToolTip = "取消置顶";
            TopmostButton.Background = new SolidColorBrush(Color.FromArgb(218, 37, 99, 235));
            TopmostButton.BorderBrush = new SolidColorBrush(Color.FromArgb(160, 191, 219, 254));
            TopmostMenuItem.Header = "取消置顶";
            return;
        }

        TopmostIcon.Data = UnpinnedGeometry;
        TopmostButton.ToolTip = "置顶";
        TopmostButton.Background = new SolidColorBrush(Color.FromArgb(116, 15, 23, 42));
        TopmostButton.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        TopmostMenuItem.Header = "置顶";
    }

    private void UpdateResizeModeVisualState()
    {
        PinnedImage.Stretch = Stretch.Fill;

        if (_isAspectRatioLocked)
        {
            ResizeModeIcon.Data = AspectGeometry;
            ResizeModeButton.ToolTip = "等比例缩放";
            ResizeModeButton.Background = new SolidColorBrush(Color.FromArgb(204, 20, 184, 166));
            ResizeModeMenuItem.Header = "自由拉伸";
            return;
        }

        ResizeModeIcon.Data = FreeGeometry;
        ResizeModeButton.ToolTip = "自由拉伸";
        ResizeModeButton.Background = new SolidColorBrush(Color.FromArgb(116, 15, 23, 42));
        ResizeModeMenuItem.Header = "等比例缩放";
    }

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

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
        {
            return min;
        }

        return Math.Min(Math.Max(value, min), max);
    }

    protected override void OnClosed(EventArgs e)
    {
        PinnedImage.Source = null;
        base.OnClosed(e);
    }
}
