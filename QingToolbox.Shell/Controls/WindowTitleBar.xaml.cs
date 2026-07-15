using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QingToolbox.Shell.Controls;

public partial class WindowTitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty = Register(nameof(Title), string.Empty);
    public static readonly DependencyProperty SubtitleProperty = Register(nameof(Subtitle), string.Empty);
    public static readonly DependencyProperty IconSourceProperty = Register<object?>(nameof(IconSource), null);
    public static readonly DependencyProperty AdditionalActionsProperty = Register<object?>(nameof(AdditionalActions), null);
    public static readonly DependencyProperty ShowMinimizeButtonProperty = Register(nameof(ShowMinimizeButton), true);
    public static readonly DependencyProperty ShowMaximizeButtonProperty = Register(nameof(ShowMaximizeButton), true);
    public static readonly DependencyProperty ShowCloseButtonProperty = Register(nameof(ShowCloseButton), true);
    public static readonly DependencyProperty MinimizeTextProperty = Register(nameof(MinimizeText), "Minimize");
    public static readonly DependencyProperty MaximizeTextProperty = Register(nameof(MaximizeText), "Maximize", OnCaptionTextChanged);
    public static readonly DependencyProperty RestoreTextProperty = Register(nameof(RestoreText), "Restore", OnCaptionTextChanged);
    public static readonly DependencyProperty CloseTextProperty = Register(nameof(CloseText), "Close");
    public static readonly DependencyProperty SystemMenuTextProperty = Register(nameof(SystemMenuText), "System menu");

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
    public bool IsMaximized => Window.GetWindow(this)?.WindowState == WindowState.Maximized;
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
            window.StateChanged -= OnWindowStateChanged;
            window.StateChanged += OnWindowStateChanged;
            UpdateWindowStateUi();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            window.StateChanged -= OnWindowStateChanged;
        }
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

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) SystemCommands.MinimizeWindow(window);
    }
    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
        else SystemCommands.MaximizeWindow(window);
    }
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) SystemCommands.CloseWindow(window);
    }
    private void OnSystemMenuClick(object sender, RoutedEventArgs e) => ShowSystemMenu();
    private void OnSystemMenuRightClick(object sender, MouseButtonEventArgs e) { e.Handled = true; ShowSystemMenu(); }
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
