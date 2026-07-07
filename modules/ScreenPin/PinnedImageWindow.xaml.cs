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
    private const double PlacementGap = 12;

    private readonly BitmapSource _image;
    private readonly double _aspectRatio;
    private bool _isAspectRatioLocked = true;

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
        UpdateResizeModeVisualState();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        Clipboard.SetImage(_image);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnResizeGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isAspectRatioLocked)
        {
            ResizeKeepingAspectRatio(e.HorizontalChange, e.VerticalChange);
            return;
        }

        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void ResizeKeepingAspectRatio(double horizontalChange, double verticalChange)
    {
        var widthCandidate = Math.Max(MinWidth, Width + horizontalChange);
        var heightCandidate = Math.Max(MinHeight, Height + verticalChange);

        var newWidth = Math.Abs(horizontalChange) >= Math.Abs(verticalChange)
            ? widthCandidate
            : heightCandidate * _aspectRatio;
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

    private void UpdateTopmostVisualState()
    {
        if (Topmost)
        {
            TopmostButton.Content = "LOCK";
            TopmostButton.ToolTip = "取消置顶";
            TopmostButton.Background = new SolidColorBrush(Color.FromArgb(218, 37, 99, 235));
            TopmostButton.BorderBrush = new SolidColorBrush(Color.FromArgb(160, 191, 219, 254));
            TopmostMenuItem.Header = "取消置顶";
            return;
        }

        TopmostButton.Content = "OPEN";
        TopmostButton.ToolTip = "置顶";
        TopmostButton.Background = new SolidColorBrush(Color.FromArgb(116, 15, 23, 42));
        TopmostButton.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        TopmostMenuItem.Header = "置顶";
    }

    private void UpdateResizeModeVisualState()
    {
        if (_isAspectRatioLocked)
        {
            PinnedImage.Stretch = Stretch.Uniform;
            ResizeModeButton.Content = "1:1";
            ResizeModeButton.ToolTip = "等比例缩放";
            ResizeModeButton.Background = new SolidColorBrush(Color.FromArgb(204, 20, 184, 166));
            ResizeModeMenuItem.Header = "自由拉伸";
            return;
        }

        PinnedImage.Stretch = Stretch.Fill;
        ResizeModeButton.Content = "FREE";
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
