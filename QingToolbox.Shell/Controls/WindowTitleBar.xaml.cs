using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QingToolbox.Shell.Windowing;

namespace QingToolbox.Shell.Controls;

public partial class WindowTitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty = Register(nameof(Title), string.Empty);
    public static readonly DependencyProperty SubtitleProperty = Register(nameof(Subtitle), string.Empty);
    public static readonly DependencyProperty IconSourceProperty = Register<object?>(nameof(IconSource), null);
    public static readonly DependencyProperty AdditionalActionsProperty = Register<object?>(nameof(AdditionalActions), null);
    public static readonly DependencyProperty ShowMinimizeButtonProperty = Register(nameof(ShowMinimizeButton), true, OnCapabilitySettingChanged);
    public static readonly DependencyProperty ShowMaximizeButtonProperty = Register(nameof(ShowMaximizeButton), true, OnCapabilitySettingChanged);
    public static readonly DependencyProperty ShowCloseButtonProperty = Register(nameof(ShowCloseButton), true);
    public static readonly DependencyProperty MinimizeTextProperty = Register(nameof(MinimizeText), "Minimize");
    public static readonly DependencyProperty MaximizeTextProperty = Register(nameof(MaximizeText), "Maximize", OnCaptionTextChanged);
    public static readonly DependencyProperty RestoreTextProperty = Register(nameof(RestoreText), "Restore", OnCaptionTextChanged);
    public static readonly DependencyProperty CloseTextProperty = Register(nameof(CloseText), "Close");
    public static readonly DependencyProperty SystemMenuTextProperty = Register(nameof(SystemMenuText), "System menu");
    public static readonly DependencyProperty AutomationNameProperty = Register(nameof(AutomationName), "Window title bar");
    private static readonly DependencyPropertyKey EffectiveShowMinimizeButtonPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(EffectiveShowMinimizeButton), typeof(bool), typeof(WindowTitleBar), new PropertyMetadata(true));
    private static readonly DependencyPropertyKey EffectiveShowMaximizeButtonPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(EffectiveShowMaximizeButton), typeof(bool), typeof(WindowTitleBar), new PropertyMetadata(true));
    public static readonly DependencyProperty EffectiveShowMinimizeButtonProperty = EffectiveShowMinimizeButtonPropertyKey.DependencyProperty;
    public static readonly DependencyProperty EffectiveShowMaximizeButtonProperty = EffectiveShowMaximizeButtonPropertyKey.DependencyProperty;
    private DependencyPropertyDescriptor? _resizeModeDescriptor;
    private Window? _hostWindow;
    private CancellationTokenSource? _systemMenuClickCancellation;

    public WindowTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? IconSource { get => GetValue(IconSourceProperty); set => SetValue(IconSourceProperty, value); }
    public object? AdditionalActions { get => GetValue(AdditionalActionsProperty); set => SetValue(AdditionalActionsProperty, value); }
    public bool ShowMinimizeButton { get => (bool)GetValue(ShowMinimizeButtonProperty); set => SetValue(ShowMinimizeButtonProperty, value); }
    public bool ShowMaximizeButton { get => (bool)GetValue(ShowMaximizeButtonProperty); set => SetValue(ShowMaximizeButtonProperty, value); }
    public bool ShowCloseButton { get => (bool)GetValue(ShowCloseButtonProperty); set => SetValue(ShowCloseButtonProperty, value); }
    public string MinimizeText { get => (string)GetValue(MinimizeTextProperty); set => SetValue(MinimizeTextProperty, value); }
    public string MaximizeText { get => (string)GetValue(MaximizeTextProperty); set => SetValue(MaximizeTextProperty, value); }
    public string RestoreText { get => (string)GetValue(RestoreTextProperty); set => SetValue(RestoreTextProperty, value); }
    public string CloseText { get => (string)GetValue(CloseTextProperty); set => SetValue(CloseTextProperty, value); }
    public string SystemMenuText { get => (string)GetValue(SystemMenuTextProperty); set => SetValue(SystemMenuTextProperty, value); }
    public string AutomationName { get => (string)GetValue(AutomationNameProperty); set => SetValue(AutomationNameProperty, value); }
    public bool EffectiveShowMinimizeButton => (bool)GetValue(EffectiveShowMinimizeButtonProperty);
    public bool EffectiveShowMaximizeButton => (bool)GetValue(EffectiveShowMaximizeButtonProperty);
    public bool IsMaximized => WindowCaptionCapabilities.UsesRestoreAction(
        Window.GetWindow(this)?.WindowState ?? WindowState.Normal);
    public string MaximizeOrRestoreText => IsMaximized ? RestoreText : MaximizeText;
    internal Button MaximizeButtonElement => MaximizeButton;

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(WindowTitleBar), new PropertyMetadata(defaultValue));

    private static DependencyProperty Register<T>(string name, T defaultValue, PropertyChangedCallback callback) =>
        DependencyProperty.Register(name, typeof(T), typeof(WindowTitleBar), new PropertyMetadata(defaultValue, callback));

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            _hostWindow = window;
            window.StateChanged -= OnWindowStateChanged;
            window.StateChanged += OnWindowStateChanged;
            _resizeModeDescriptor = DependencyPropertyDescriptor.FromProperty(
                Window.ResizeModeProperty, typeof(Window));
            _resizeModeDescriptor?.AddValueChanged(window, OnResizeModeChanged);
            UpdateCapabilities();
            UpdateWindowStateUi();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _systemMenuClickCancellation?.Cancel();
        if (_hostWindow is { } window)
        {
            window.StateChanged -= OnWindowStateChanged;
            _resizeModeDescriptor?.RemoveValueChanged(window, OnResizeModeChanged);
        }
        _resizeModeDescriptor = null;
        _hostWindow = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateWindowStateUi();

    private void UpdateWindowStateUi()
    {
        MaximizeGlyphText.Text = IsMaximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = MaximizeOrRestoreText;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, MaximizeOrRestoreText);
    }

    private static void OnCaptionTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is WindowTitleBar titleBar && titleBar.IsLoaded)
            titleBar.UpdateWindowStateUi();
    }

    private static void OnCapabilitySettingChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is WindowTitleBar titleBar) titleBar.UpdateCapabilities();
    }

    private void OnResizeModeChanged(object? sender, EventArgs e) => UpdateCapabilities();

    private void UpdateCapabilities()
    {
        var capabilities = _hostWindow is null
            ? new WindowCaptionCapabilities(true, true)
            : WindowCaptionCapabilities.FromResizeMode(_hostWindow.ResizeMode);
        SetValue(EffectiveShowMinimizeButtonPropertyKey, ShowMinimizeButton && capabilities.CanMinimize);
        SetValue(EffectiveShowMaximizeButtonPropertyKey, ShowMaximizeButton && capabilities.CanMaximize);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) SystemCommands.MinimizeWindow(window);
    }
    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => ExecuteMaximizeRestore();

    internal void ExecuteMaximizeRestore()
    {
        var window = _hostWindow ?? Window.GetWindow(this);
        if (window is null) return;
        if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
        else SystemCommands.MaximizeWindow(window);
    }

    internal void SetNativeMaximizeButtonState(bool isHovered, bool isPressed)
    {
        if (!isHovered)
        {
            MaximizeButton.ClearValue(BackgroundProperty);
            return;
        }

        MaximizeButton.SetCurrentValue(
            BackgroundProperty,
            FindResource(isPressed ? "PrimaryAccentSoftBrush" : "SidebarHoverBrush"));
    }
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) SystemCommands.CloseWindow(window);
    }
    private void OnSystemMenuClick(object sender, RoutedEventArgs e)
    {
        // Mouse clicks are handled below so a double click can close without first
        // opening two menus. Keyboard activation still reaches this Click handler.
        if (e.OriginalSource is Button && Mouse.LeftButton != MouseButtonState.Pressed)
            ShowSystemMenu();
    }
    private async void OnSystemMenuLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _systemMenuClickCancellation?.Cancel();
        if (e.ClickCount >= 2)
        {
            if (_hostWindow is { } window) SystemCommands.CloseWindow(window);
            return;
        }

        var cancellation = _systemMenuClickCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay((int)NativeWindowMessages.GetDoubleClickTime(), cancellation.Token);
            if (!cancellation.IsCancellationRequested && IsLoaded) ShowSystemMenu();
        }
        catch (OperationCanceledException)
        {
        }
    }
    private void OnSystemMenuRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _systemMenuClickCancellation?.Cancel();
        ShowSystemMenu();
    }
    private void OnTitleBarRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null) return;
        e.Handled = true;
        var window = Window.GetWindow(this);
        if (window is not null) SystemCommands.ShowSystemMenu(window, PointToScreen(e.GetPosition(this)));
    }
    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }
    private void ShowSystemMenu()
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        var point = SystemMenuButton.PointToScreen(new Point(0, SystemMenuButton.ActualHeight));
        SystemCommands.ShowSystemMenu(window, point);
    }
}
