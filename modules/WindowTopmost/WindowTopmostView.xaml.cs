using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Modules.WindowTopmost;

public partial class WindowTopmostView : UserControl, ILocalizedModuleView
{
    private readonly ObservableCollection<LocalizedWindowInfo> _windows = [];
    private readonly ILocalizationService _localization;
    private readonly string _moduleId;

    public WindowTopmostView(ILocalizationService localization, string moduleId)
    {
        InitializeComponent();
        _localization = localization;
        _moduleId = moduleId;
        WindowGrid.ItemsSource = _windows;
        RefreshLocalization();
        RefreshWindows();
    }

    private string T(string key, string fallback) =>
        _localization.GetModuleString(_moduleId, key, fallback);

    public void RefreshLocalization()
    {
        TitleText.Text = T("view.title", "Window Topmost");
        SubtitleText.Text = T(
            "view.subtitle",
            "Choose a visible window and toggle always-on-top.");
        RefreshButtonText.Text = T("actions.refresh", "Refresh");
        PickButtonText.Text = T("actions.pickWindow", "Pick Window");
        SetTopmostButtonText.Text = T("actions.setTopmost", "Set Topmost");
        RemoveTopmostButtonText.Text = T("actions.removeTopmost", "Remove Topmost");
        TitleColumn.Header = T("columns.title", "Title");
        ProcessColumn.Header = T("columns.process", "Process");
        HwndColumn.Header = T("columns.hwnd", "HWND");
        TopmostColumn.Header = T("columns.topmost", "Topmost");

        foreach (var window in _windows)
        {
            window.RefreshLocalization(_localization, _moduleId);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshWindows();
    private void OnSetTopmost(object sender, RoutedEventArgs e) => SetSelected(true);
    private void OnRemoveTopmost(object sender, RoutedEventArgs e) => SetSelected(false);

    private async void OnPick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = T("status.pickPending", "Move the cursor over a window and confirm.");
        await Task.Delay(3000);
        var hwnd = WindowTopmostService.GetWindowUnderCursor();
        RefreshWindows();
        WindowGrid.SelectedItem = _windows.FirstOrDefault(item => item.Handle == hwnd);
        if (WindowGrid.SelectedItem is not null)
        {
            WindowGrid.ScrollIntoView(WindowGrid.SelectedItem);
        }
        StatusText.Text = WindowGrid.SelectedItem is null
            ? T("status.pickFailed", "No window was picked.")
            : _localization.GetModuleString(
                _moduleId,
                "status.selected",
                "Selected window: {0}",
                ((LocalizedWindowInfo)WindowGrid.SelectedItem).Title);
    }

    private void SetSelected(bool topmost)
    {
        if (WindowGrid.SelectedItem is not LocalizedWindowInfo selected)
        {
            StatusText.Text = T("status.noWindowSelected", "No window selected.");
            return;
        }

        StatusText.Text = WindowTopmostService.SetTopmost(selected.Handle, topmost)
            ? topmost
                ? T("status.topmostSet", "Window set to topmost.")
                : T("status.topmostRemoved", "Window removed from topmost.")
            : _localization.GetModuleString(
                _moduleId,
                "errors.operationFailed",
                "Operation failed: {0}",
                "The target may require administrator privileges.");
        RefreshWindows(selected.Handle);
    }

    private void RefreshWindows(nint selectedHandle = default)
    {
        if (selectedHandle == 0 && WindowGrid.SelectedItem is LocalizedWindowInfo selected)
        {
            selectedHandle = selected.Handle;
        }

        _windows.Clear();
        foreach (var window in WindowEnumerator.Enumerate())
        {
            _windows.Add(new LocalizedWindowInfo(window, _localization, _moduleId));
        }

        WindowGrid.SelectedItem = _windows.FirstOrDefault(item => item.Handle == selectedHandle);
        StatusText.Text = _localization.GetModuleString(
            _moduleId,
            "status.refreshed",
            "Window list refreshed.");
    }

    private sealed class LocalizedWindowInfo : INotifyPropertyChanged
    {
        private readonly WindowInfo _window;
        private string _topmostText;

        public LocalizedWindowInfo(
            WindowInfo window,
            ILocalizationService localization,
            string moduleId)
        {
            _window = window;
            _topmostText = string.Empty;
            RefreshLocalization(localization, moduleId);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public nint Handle => _window.Handle;
        public string Title => _window.Title;
        public string ProcessName => _window.ProcessName;
        public string HandleText => _window.HandleText;

        public string TopmostText
        {
            get => _topmostText;
            private set
            {
                if (_topmostText == value)
                {
                    return;
                }

                _topmostText = value;
                OnPropertyChanged();
            }
        }

        public void RefreshLocalization(
            ILocalizationService localization,
            string moduleId)
        {
            TopmostText = _window.IsTopmost
                ? localization.GetModuleString(moduleId, "values.yes", "Yes")
                : localization.GetModuleString(moduleId, "values.no", "No");
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
