using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Modules.ScreenPin;

public partial class PinnedImageWindow : Window, ILocalizedModuleView
{
    private const double PlacementGap = 12;
    private const string PinnedGlyph = "\uE840";
    private const string UnpinnedGlyph = "\uE77A";
    private const string AspectGlyph = "\uE8B9";
    private const string FreeGlyph = "\uE7AD";

    private readonly BitmapSource _image;
    private readonly ILocalizationService? _localization;
    private readonly string _moduleId;
    private readonly double _aspectRatio;
    private bool _isAspectRatioLocked = true;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeDeltaX;
    private double _resizeDeltaY;

    public PinnedImageWindow(
        BitmapSource image,
        Rect selectedRegionDip,
        Rect virtualScreenDip,
        ILocalizationService? localization = null,
        string moduleId = "qing.screenpin")
    {
        InitializeComponent();
        _image = image;
        _localization = localization;
        _moduleId = moduleId;
        _aspectRatio = Math.Max(0.01, selectedRegionDip.Width / Math.Max(1, selectedRegionDip.Height));

        PinnedImage.Source = image;
        SetInitialSize(selectedRegionDip);
        PlaceNearSelection(selectedRegionDip, virtualScreenDip);
        RefreshLocalization();
    }

    private string T(string key, string fallback) =>
        _localization?.GetModuleString(_moduleId, key, fallback) ?? fallback;

    public void RefreshLocalization()
    {
        CopyButton.ToolTip = T("pin.copy", "Copy");
        CloseButton.ToolTip = T("pin.close", "Close");
        CopyMenuItem.Header = T("context.copy", "Copy");
        CloseMenuItem.Header = T("context.close", "Close");
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

    private void OnCopy(object sender, RoutedEventArgs e) => Clipboard.SetImage(_image);
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
            TopmostIcon.Text = PinnedGlyph;
            TopmostButton.ToolTip = T("pin.cancelTopmost", "Unpin");
            TopmostButton.Background = new SolidColorBrush(Color.FromArgb(218, 37, 99, 235));
            TopmostButton.BorderBrush = new SolidColorBrush(Color.FromArgb(160, 191, 219, 254));
            TopmostMenuItem.Header = T("context.cancelTopmost", "Unpin");
            return;
        }

        TopmostIcon.Text = UnpinnedGlyph;
        TopmostButton.ToolTip = T("pin.topmost", "Pin on top");
        TopmostButton.Background = new SolidColorBrush(Color.FromArgb(116, 15, 23, 42));
        TopmostButton.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        TopmostMenuItem.Header = T("context.topmost", "Pin on top");
    }

    private void UpdateResizeModeVisualState()
    {
        PinnedImage.Stretch = Stretch.Fill;

        if (_isAspectRatioLocked)
        {
            ResizeModeIcon.Text = AspectGlyph;
            ResizeModeButton.ToolTip = T("pin.aspectRatio", "Aspect ratio resize");
            ResizeModeButton.Background = new SolidColorBrush(Color.FromArgb(218, 37, 99, 235));
            ResizeModeButton.BorderBrush = new SolidColorBrush(Color.FromArgb(160, 191, 219, 254));
            ResizeModeMenuItem.Header = T("context.freeStretch", "Free stretch");
            return;
        }

        ResizeModeIcon.Text = FreeGlyph;
        ResizeModeButton.ToolTip = T("pin.freeStretch", "Free stretch");
        ResizeModeButton.Background = new SolidColorBrush(Color.FromArgb(116, 15, 23, 42));
        ResizeModeButton.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        ResizeModeMenuItem.Header = T("context.aspectRatio", "Aspect ratio resize");
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
