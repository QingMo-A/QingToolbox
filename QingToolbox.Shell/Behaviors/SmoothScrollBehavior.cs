using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace QingToolbox.Shell.Behaviors;

public static class SmoothScrollBehavior
{
    private const double ScrollStep = 52;
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ScrollViewer scrollViewer)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
            scrollViewer.Unloaded += OnScrollViewerUnloaded;
        }
        else
        {
            Detach(scrollViewer);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (sender is not ScrollViewer scrollViewer ||
            scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var state = States.GetValue(
            scrollViewer,
            viewer => new ScrollState(viewer));
        var currentOffset = state.Offset;
        var scrollDistance = Math.Clamp(
            -args.Delta / 120d * ScrollStep,
            -86,
            86);
        if (Math.Abs(scrollDistance) < 3)
        {
            scrollDistance = Math.Sign(scrollDistance) * 3;
        }
        var direction = Math.Sign(scrollDistance);

        if (!state.HasAnimatedProperties || state.LastDirection != direction)
        {
            currentOffset = scrollViewer.VerticalOffset;
            state.TargetOffset = currentOffset;
        }

        state.BeginAnimation(ScrollState.OffsetProperty, null);
        state.Offset = currentOffset;
        state.LastDirection = direction;
        state.TargetOffset = Math.Clamp(
            state.TargetOffset + scrollDistance,
            0,
            scrollViewer.ScrollableHeight);

        var distance = Math.Abs(state.TargetOffset - currentOffset);
        var duration = TimeSpan.FromMilliseconds(
            Math.Clamp(85 + distance * 0.22, 95, 180));
        var animation = new DoubleAnimation
        {
            From = currentOffset,
            To = state.TargetOffset,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        animation.Completed += (_, _) =>
        {
            var targetOffset = state.TargetOffset;
            state.BeginAnimation(ScrollState.OffsetProperty, null);
            state.Offset = targetOffset;
        };
        state.BeginAnimation(ScrollState.OffsetProperty, animation);
        args.Handled = true;
    }

    private static void OnScrollViewerUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            Detach(scrollViewer);
        }
    }

    private static void Detach(ScrollViewer scrollViewer)
    {
        scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        scrollViewer.Unloaded -= OnScrollViewerUnloaded;
        if (States.TryGetValue(scrollViewer, out var state))
        {
            state.BeginAnimation(ScrollState.OffsetProperty, null);
            States.Remove(scrollViewer);
        }
    }

    private sealed class ScrollState : Animatable
    {
        private readonly ScrollViewer _scrollViewer;

        public static readonly DependencyProperty OffsetProperty =
            DependencyProperty.Register(
                nameof(Offset),
                typeof(double),
                typeof(ScrollState),
                new PropertyMetadata(0d, OnOffsetChanged));

        public ScrollState(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
            Offset = scrollViewer.VerticalOffset;
            TargetOffset = scrollViewer.VerticalOffset;
        }

        public double Offset
        {
            get => (double)GetValue(OffsetProperty);
            set => SetValue(OffsetProperty, value);
        }

        public double TargetOffset { get; set; }

        public int LastDirection { get; set; }

        protected override Freezable CreateInstanceCore() =>
            new ScrollState(_scrollViewer);

        private static void OnOffsetChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            ((ScrollState)dependencyObject)._scrollViewer.ScrollToVerticalOffset(
                (double)args.NewValue);
        }
    }
}
