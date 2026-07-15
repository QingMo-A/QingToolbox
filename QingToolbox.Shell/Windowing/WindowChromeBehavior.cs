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

        internal Controller(Window window)
        {
            _window = window;
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 48,
                ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            window.SourceInitialized += OnSourceInitialized;
            window.Closed += OnClosed;
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
            if (message != NativeWindowMessages.WindowNonClientHitTest)
            {
                return IntPtr.Zero;
            }

            var titleBar = FindVisualChild<WindowTitleBar>(_window);
            var maximizeButton = titleBar?.MaximizeButtonElement;
            if (maximizeButton is not { IsVisible: true, IsEnabled: true })
            {
                return IntPtr.Zero;
            }

            var x = unchecked((short)(long)lParam);
            var y = unchecked((short)((long)lParam >> 16));
            var point = maximizeButton.PointFromScreen(new Point(x, y));
            if (point.X >= 0 && point.Y >= 0 &&
                point.X <= maximizeButton.ActualWidth &&
                point.Y <= maximizeButton.ActualHeight)
            {
                handled = true;
                return new IntPtr(NativeWindowMessages.HitTestMaximizeButton);
            }

            return IntPtr.Zero;
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
            _window.Closed -= OnClosed;
            if (_source is not null)
            {
                _source.RemoveHook(WindowProcedure);
                _source = null;
            }
        }
    }
}
