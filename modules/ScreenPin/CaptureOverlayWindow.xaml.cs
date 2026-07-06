using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace QingToolbox.Modules.ScreenPin;

public partial class CaptureOverlayWindow : Window
{
    private Point? _start;
    public Rect? SelectedRegion { get; private set; }

    public CaptureOverlayWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        Selection.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_start is null || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateSelection(_start.Value, e.GetPosition(this));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null) return;
        var end = e.GetPosition(this);
        var local = new Rect(_start.Value, end);
        SelectedRegion = new Rect(
            local.X + SystemParameters.VirtualScreenLeft,
            local.Y + SystemParameters.VirtualScreenTop,
            local.Width,
            local.Height);
        ReleaseMouseCapture();
        DialogResult = local.Width >= 2 && local.Height >= 2;
    }

    private void UpdateSelection(Point start, Point end)
    {
        var rect = new Rect(start, end);
        Canvas.SetLeft(Selection, rect.X);
        Canvas.SetTop(Selection, rect.Y);
        Selection.Width = rect.Width;
        Selection.Height = rect.Height;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
