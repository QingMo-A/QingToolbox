using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using QingToolbox.Shell.Controls;

namespace QingToolbox.Shell.Windowing;

public static class WindowChromeBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(WindowChromeBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty ControllerProperty =
        DependencyProperty.RegisterAttached(
            "Controller", typeof(Controller), typeof(WindowChromeBehavior));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not Window window)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            if (window.GetValue(ControllerProperty) is null)
            {
                window.SetValue(ControllerProperty, new Controller(window));
            }
        }
        else if (window.GetValue(ControllerProperty) is Controller controller)
        {
            controller.Dispose();
            window.ClearValue(ControllerProperty);
        }
    }

    private sealed class Controller : IDisposable
    {
        private readonly Window _window;
        private HwndSource? _source;
        private WeakReference<WindowTitleBar>? _titleBarReference;
        private WeakReference<FrameworkElement>? _maximizeButtonReference;
        private readonly MaximizeButtonInteractionState _maximizeInteraction = new();
        private bool _trackingNonClientMouse;

        internal Controller(Window window)
        {
            _window = window;
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = WindowChromeMetrics.TitleBarHeight,
                ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            window.SourceInitialized += OnSourceInitialized;
            window.Loaded += OnWindowLoaded;
            window.ContentRendered += OnContentRendered;
            window.Deactivated += OnWindowDeactivated;
            window.Closed += OnClosed;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e) => CacheTitleBar();

        private void OnContentRendered(object? sender, EventArgs e) => CacheTitleBar();

        private void CacheTitleBar()
        {
            if (_titleBarReference?.TryGetTarget(out var existing) == true && existing.IsLoaded)
            {
                return;
            }

            var titleBar = FindVisualChild<WindowTitleBar>(_window);
            if (titleBar is null)
            {
                ClearTitleBarReferences();
                return;
            }

            titleBar.Unloaded -= OnTitleBarUnloaded;
            titleBar.Unloaded += OnTitleBarUnloaded;
            _titleBarReference = new WeakReference<WindowTitleBar>(titleBar);
            _maximizeButtonReference = new WeakReference<FrameworkElement>(titleBar.MaximizeButtonElement);
        }

        private void OnTitleBarUnloaded(object sender, RoutedEventArgs e) => ClearTitleBarReferences();

        private void ClearTitleBarReferences()
        {
            if (_titleBarReference?.TryGetTarget(out var titleBar) == true)
            {
                titleBar.Unloaded -= OnTitleBarUnloaded;
            }
            _titleBarReference = null;
            _maximizeButtonReference = null;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (_source is not null)
            {
                return;
            }

            _source = (HwndSource?)PresentationSource.FromVisual(_window);
            _source?.AddHook(WindowProcedure);
        }

        private IntPtr WindowProcedure(
            IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam,
            ref bool handled)
        {
            if (message is NativeWindowMessages.WindowCancelMode or
                NativeWindowMessages.WindowCaptureChanged)
            {
                CancelMaximizeInteraction();
                return IntPtr.Zero;
            }

            if (message == NativeWindowMessages.WindowNonClientMouseLeave)
            {
                CancelMaximizeInteraction();
                _trackingNonClientMouse = false;
                return IntPtr.Zero;
            }

            if (message == NativeWindowMessages.WindowNonClientMouseMove &&
                wParam.ToInt64() == NativeWindowMessages.HitTestMaximizeButton)
            {
                SetNativeMaximizeState(true, _maximizeInteraction.IsPressed);
                if (!_trackingNonClientMouse)
                {
                    NativeWindowMessages.TrackNonClientMouseLeave(hwnd);
                    _trackingNonClientMouse = true;
                }
                return IntPtr.Zero;
            }

            if (message == NativeWindowMessages.WindowNonClientLeftButtonDown &&
                wParam.ToInt64() == NativeWindowMessages.HitTestMaximizeButton)
            {
                _maximizeInteraction.Press();
                SetNativeMaximizeState(true, true);
                handled = true;
                return IntPtr.Zero;
            }

            if (message == NativeWindowMessages.WindowNonClientLeftButtonUp && _maximizeInteraction.IsPressed)
            {
                var invoke = _maximizeInteraction.Release(
                    wParam.ToInt64() == NativeWindowMessages.HitTestMaximizeButton);
                SetNativeMaximizeState(invoke, false);
                handled = true;
                if (invoke && _titleBarReference?.TryGetTarget(out var pressedTitleBar) == true)
                {
                    // HTMAXBUTTON promotes the region to non-client input, so WPF Click
                    // is not reliable. Consume the matching release and execute exactly once.
                    pressedTitleBar.ExecuteMaximizeRestore();
                }
                return IntPtr.Zero;
            }

            if (message != NativeWindowMessages.WindowNonClientHitTest) return IntPtr.Zero;

            if (_titleBarReference?.TryGetTarget(out var titleBar) != true || titleBar is null ||
                _maximizeButtonReference?.TryGetTarget(out var maximizeButton) != true || maximizeButton is null ||
                !titleBar.EffectiveShowMaximizeButton ||
                maximizeButton is not { IsVisible: true, IsEnabled: true } ||
                maximizeButton.ActualWidth <= 0 || maximizeButton.ActualHeight <= 0)
            {
                return IntPtr.Zero;
            }

            Point point;
            try
            {
                point = maximizeButton.PointFromScreen(
                    WindowHitTestService.DecodeScreenPoint(lParam));
            }
            catch (InvalidOperationException)
            {
                // The visual can briefly lose its PresentationSource during teardown.
                return IntPtr.Zero;
            }

            if (WindowHitTestService.Contains(
                point, maximizeButton.ActualWidth, maximizeButton.ActualHeight))
            {
                handled = true;
                return new IntPtr(NativeWindowMessages.HitTestMaximizeButton);
            }

            return IntPtr.Zero;
        }

        private void SetNativeMaximizeState(bool hovered, bool pressed)
        {
            if (_titleBarReference?.TryGetTarget(out var titleBar) == true)
                titleBar.SetNativeMaximizeButtonState(hovered, pressed);
        }

        private void OnWindowDeactivated(object? sender, EventArgs e) =>
            CancelMaximizeInteraction();

        private void CancelMaximizeInteraction()
        {
            _maximizeInteraction.Cancel();
            SetNativeMaximizeState(false, false);
        }

        private static T? FindVisualChild<T>(DependencyObject root)
            where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is T match)
                {
                    return match;
                }

                var descendant = FindVisualChild<T>(child);
                if (descendant is not null)
                {
                    return descendant;
                }
            }

            return null;
        }

        private void OnClosed(object? sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Loaded -= OnWindowLoaded;
            _window.ContentRendered -= OnContentRendered;
            _window.Deactivated -= OnWindowDeactivated;
            _window.Closed -= OnClosed;
            CancelMaximizeInteraction();
            ClearTitleBarReferences();
            if (_source is not null)
            {
                _source.RemoveHook(WindowProcedure);
                _source = null;
            }
        }
    }
}
