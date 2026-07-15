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
    private bool _suppressRestoreUntilButtonUp;
    private bool _allowClose;

    public FloatingBadgeWindow(ILocalizationService localization)
    {
        _localization = localization;
        InitializeComponent();
        _localization.CultureChanged += OnCultureChanged;
        DataContext = this;
        BadgeSurface.ContextMenu.DataContext = this;
        Loaded += (_, _) => BadgeSurface.Focus();
        Deactivated += (_, _) => ResetDragState(releaseCapture: true);
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
        _suppressRestoreUntilButtonUp = false;
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
        _suppressRestoreUntilButtonUp = true;
        BadgeSurface.ReleaseMouseCapture();
        var completed = false;
        try { DragMove(); completed = true; }
        catch (InvalidOperationException) { }
        finally
        {
            if (completed) DragCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _dragStarted || _suppressRestoreUntilButtonUp;
        if (BadgeSurface.IsMouseCaptured) BadgeSurface.ReleaseMouseCapture();
        ResetDragState(releaseCapture: false);
        _suppressRestoreUntilButtonUp = false;
        if (!wasDragging) RestoreRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ResetDragState(releaseCapture: true);
        OpenContextMenu();
    }

    private void OpenContextMenu()
    {
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
            OpenContextMenu();
        }
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e) =>
        ResetDragState(releaseCapture: false);

    private void OnContextMenuClosed(object sender, RoutedEventArgs e) => BadgeSurface.Focus();

    private void ResetDragState(bool releaseCapture)
    {
        if (releaseCapture && BadgeSurface.IsMouseCaptured) BadgeSurface.ReleaseMouseCapture();
        _dragStarted = false;
        _mouseDownPoint = default;
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
