using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace QingToolbox.Modules.WindowTopmost;

public partial class WindowTopmostView : UserControl
{
    private readonly ObservableCollection<WindowInfo> _windows = [];

    public WindowTopmostView()
    {
        InitializeComponent();
        WindowGrid.ItemsSource = _windows;
        RefreshWindows();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshWindows();
    private void OnSetTopmost(object sender, RoutedEventArgs e) => SetSelected(true);
    private void OnRemoveTopmost(object sender, RoutedEventArgs e) => SetSelected(false);

    private async void OnPick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Move the cursor over a window. Picking in 3 seconds...";
        await Task.Delay(3000);
        var hwnd = WindowTopmostService.GetWindowUnderCursor();
        RefreshWindows();
        WindowGrid.SelectedItem = _windows.FirstOrDefault(item => item.Handle == hwnd);
        if (WindowGrid.SelectedItem is not null)
        {
            WindowGrid.ScrollIntoView(WindowGrid.SelectedItem);
        }
        StatusText.Text = WindowGrid.SelectedItem is null
            ? "The window under the cursor is not in the visible window list."
            : "Window picked.";
    }

    private void SetSelected(bool topmost)
    {
        if (WindowGrid.SelectedItem is not WindowInfo selected)
        {
            StatusText.Text = "Select a window first.";
            return;
        }
        StatusText.Text = WindowTopmostService.SetTopmost(selected.Handle, topmost)
            ? topmost ? "Window is now topmost." : "Topmost removed."
            : "The operation failed. The target may require administrator privileges.";
        RefreshWindows(selected.Handle);
    }

    private void RefreshWindows(nint selectedHandle = default)
    {
        if (selectedHandle == 0 && WindowGrid.SelectedItem is WindowInfo selected)
            selectedHandle = selected.Handle;
        _windows.Clear();
        foreach (var window in WindowEnumerator.Enumerate()) _windows.Add(window);
        WindowGrid.SelectedItem = _windows.FirstOrDefault(item => item.Handle == selectedHandle);
        StatusText.Text = $"{_windows.Count} visible windows.";
    }
}
