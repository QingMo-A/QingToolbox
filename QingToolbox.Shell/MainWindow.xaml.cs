using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using QingToolbox.Shell.ViewModels;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;
using QingToolbox.Core.Settings;

namespace QingToolbox.Shell;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly FloatingBadgeManager _floatingBadgeManager;
    private readonly ApplicationLaunchOptions _launchOptions;

    public MainWindow(
        MainWindowViewModel viewModel,
        FloatingBadgeManager floatingBadgeManager,
        ApplicationLaunchOptions launchOptions)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _floatingBadgeManager = floatingBadgeManager;
        _launchOptions = launchOptions;
        _floatingBadgeManager.Attach(this);
        DataContext = viewModel;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
    }

    private async void OnFloatingBadgeClick(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        button.IsEnabled = false;
        try { await _floatingBadgeManager.EnterAsync(); }
        catch { _viewModel.StatusMessage = _viewModel.Strings["floatingBadge.restoreFailed"]; }
        finally { button.IsEnabled = true; }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.IsCompactWindow = e.NewSize.Width < 720;
        if (_viewModel.IsCompactWindow)
        {
            _viewModel.IsSidebarExpanded = false;
            AnimateSidebarWidth(76, TimeSpan.FromMilliseconds(180));
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
        if (!_launchOptions.IsStartupLaunch) return;

        switch (_viewModel.SelectedStartupPresentationMode)
        {
            case StartupPresentationMode.Minimized:
                Opacity = 1;
                ShowActivated = true;
                WindowState = WindowState.Minimized;
                break;
            case StartupPresentationMode.FloatingBadge:
                ShowActivated = true;
                try
                {
                    await _floatingBadgeManager.EnterAsync();
                    Opacity = 1;
                }
                catch { Opacity = 1; Show(); Activate(); }
                break;
            default:
                Opacity = 1;
                ShowActivated = true;
                Activate();
                break;
        }
    }

    private void OnSidebarMouseEnter(object sender, MouseEventArgs e)
    {
        if (_viewModel.IsCompactWindow) return;
        _viewModel.IsSidebarExpanded = true;
        AnimateSidebarWidth(236, TimeSpan.FromMilliseconds(380));
    }

    private void OnSidebarMouseLeave(object sender, MouseEventArgs e)
    {
        if (_viewModel.IsSidebarPinned)
        {
            return;
        }

        _viewModel.IsSidebarExpanded = false;
        AnimateSidebarWidth(76, TimeSpan.FromMilliseconds(340));
    }

    private void OnPinSidebarClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsCompactWindow) return;
        _viewModel.ToggleSidebarPinCommand.Execute(null);
        _viewModel.IsSidebarExpanded = _viewModel.IsSidebarPinned || Sidebar.IsMouseOver;
        AnimateSidebarWidth(
            _viewModel.IsSidebarExpanded ? 236 : 76,
            TimeSpan.FromMilliseconds(_viewModel.IsSidebarExpanded ? 380 : 340));
    }

    private void AnimateSidebarWidth(double targetWidth, TimeSpan duration)
    {
        Sidebar.BeginAnimation(WidthProperty, null);
        var animation = new DoubleAnimation
        {
            From = Sidebar.ActualWidth,
            To = targetWidth,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        Sidebar.BeginAnimation(WidthProperty, animation);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _floatingBadgeManager.OnMainWindowClosing();
        _viewModel.CloseModuleWindows();
    }
}
