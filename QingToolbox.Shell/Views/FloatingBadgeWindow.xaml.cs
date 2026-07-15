using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Shell.Views;

public partial class FloatingBadgeWindow : Window, INotifyPropertyChanged
{
    private readonly ILocalizationService _localization;
    private Point _mouseDownPoint;
    private bool _dragStarted;
    private bool _allowClose;

    public FloatingBadgeWindow(ILocalizationService localization)
    {
        _localization = localization;
        InitializeComponent();
        _localization.CultureChanged += OnCultureChanged;
        DataContext = this;
        BadgeSurface.ContextMenu.DataContext = this;
        Loaded += (_, _) => BadgeSurface.Focus();
    }

    public string AutomationName => _localization.GetString("floatingBadge.automationName");
    public string OpenText => _localization.GetString("floatingBadge.open");
    public string ExitText => _localization.GetString("floatingBadge.exit");
    public string ContextMenuText => _localization.GetString("floatingBadge.contextMenu");
    public event EventHandler? RestoreRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? DragCompleted;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void AllowClose() => _allowClose = true;

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownPoint = e.GetPosition(this);
        _dragStarted = false;
        BadgeSurface.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !BadgeSurface.IsMouseCaptured || _dragStarted) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _mouseDownPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _mouseDownPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragStarted = true;
        BadgeSurface.ReleaseMouseCapture();
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { DragCompleted?.Invoke(this, EventArgs.Empty); }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (BadgeSurface.IsMouseCaptured) BadgeSurface.ReleaseMouseCapture();
        if (!_dragStarted) RestoreRequested?.Invoke(this, EventArgs.Empty);
        _dragStarted = false;
        e.Handled = true;
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BadgeSurface.ContextMenu.PlacementTarget = BadgeSurface;
        BadgeSurface.ContextMenu.IsOpen = true;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke(this, EventArgs.Empty);
    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            RestoreRequested?.Invoke(this, EventArgs.Empty);
        }
        else if ((e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) || e.Key == Key.Apps)
        {
            e.Handled = true;
            BadgeSurface.ContextMenu.IsOpen = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _localization.CultureChanged -= OnCultureChanged;
            return;
        }
        e.Cancel = true;
        RestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OpenText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExitText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContextMenuText)));
    }
}
