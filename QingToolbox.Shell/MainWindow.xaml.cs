using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.RefreshModulesCommand.ExecuteAsync(null);
    }

    private void OnSidebarMouseEnter(object sender, MouseEventArgs e)
    {
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
        _viewModel.CloseModuleWindows();
    }
}
