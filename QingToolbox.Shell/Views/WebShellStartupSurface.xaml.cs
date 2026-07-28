using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace QingToolbox.Shell.Views;

public partial class WebShellStartupSurface : UserControl
{
    private Storyboard? _loadingStoryboard;

    public WebShellStartupSurface()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateLoadingAnimation();

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopLoadingAnimation();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdateLoadingAnimation();

    private void UpdateLoadingAnimation()
    {
        if (!IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            StopLoadingAnimation();
            return;
        }

        _loadingStoryboard ??= (Storyboard)FindResource("LoadingStoryboard");
        _loadingStoryboard.Begin(this, true);
    }

    private void StopLoadingAnimation() => _loadingStoryboard?.Remove(this);
}
