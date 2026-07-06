using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace QingToolbox.Modules.ScreenPin;

public partial class PinnedImageWindow : Window
{
    public PinnedImageWindow(ImageSource image)
    {
        InitializeComponent();
        PinnedImage.Source = image;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnTopmostChanged(object sender, RoutedEventArgs e) =>
        Topmost = TopmostToggle.IsChecked == true;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        PinnedImage.Source = null;
        base.OnClosed(e);
    }
}
