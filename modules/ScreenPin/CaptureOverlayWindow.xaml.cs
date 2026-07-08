using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Modules.ScreenPin;

public partial class CaptureOverlayWindow : Window
{
    private readonly ILocalizationService? _localization;
    private readonly string _moduleId;
    private Point? _start;

    public Rect? SelectedRegionDip { get; private set; }
    public Matrix TransformToDevice { get; private set; } = Matrix.Identity;

    public CaptureOverlayWindow(
        ILocalizationService? localization = null,
        string moduleId = "qing.screenpin")
    {
        InitializeComponent();
        _localization = localization;
        _moduleId = moduleId;
        RefreshLocalization();
        var virtualScreenDip = GetVirtualScreenDip();
        Left = virtualScreenDip.Left;
        Top = virtualScreenDip.Top;
        Width = virtualScreenDip.Width;
        Height = virtualScreenDip.Height;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } compositionTarget)
        {
            TransformToDevice = compositionTarget.TransformToDevice;
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        Selection.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        UpdateSelection(_start.Value, _start.Value);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_start is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateSelection(_start.Value, e.GetPosition(this));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null)
        {
            return;
        }

        var end = e.GetPosition(this);
        var local = new Rect(_start.Value, end);
        SelectedRegionDip = new Rect(
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

        var width = Math.Max(0, (int)Math.Round(rect.Width));
        var height = Math.Max(0, (int)Math.Round(rect.Height));
        SizeText.Text = _localization?.GetModuleString(
            _moduleId,
            "overlay.size",
            "{0} × {1}",
            width,
            height) ?? $"{width} × {height}";

        Canvas.SetLeft(SizeBadge, rect.X + 8);
        Canvas.SetTop(SizeBadge, Math.Max(8, rect.Y - 34));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    public static Rect GetVirtualScreenDip() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    private void RefreshLocalization()
    {
        InstructionText.Text = _localization?.GetModuleString(
            _moduleId,
            "overlay.instruction",
            "Drag to capture a region · Esc to cancel")
            ?? "Drag to capture a region · Esc to cancel";
    }
}
