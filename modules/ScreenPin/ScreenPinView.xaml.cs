using System.Windows;
using System.Windows.Controls;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Modules.ScreenPin;

public partial class ScreenPinView : UserControl, ILocalizedModuleView
{
    private readonly ScreenPinManager _manager;
    private readonly ILocalizationService _localization;
    private readonly string _moduleId;

    public ScreenPinView(
        ScreenPinManager manager,
        ILocalizationService localization,
        string moduleId)
    {
        InitializeComponent();
        _manager = manager;
        _localization = localization;
        _moduleId = moduleId;
        _manager.CountChanged += OnCountChanged;
        Unloaded += (_, _) => _manager.CountChanged -= OnCountChanged;
        RefreshLocalization();
        UpdateCount();
    }

    private string T(string key, string fallback) =>
        _localization.GetModuleString(_moduleId, key, fallback);

    public void RefreshLocalization()
    {
        TitleText.Text = T("view.title", "Screen Pin");
        SubtitleText.Text = T(
            "view.subtitle",
            "Capture a region and keep it floating on screen.");
        CaptureButtonText.Text = T("actions.captureRegion", "Capture Region");
        CloseAllButtonText.Text = T("actions.closeAllPins", "Close All Pins");
        ActionHeadingText.Text = T("view.actionsTitle", "Capture controls");
        ActionDescriptionText.Text = T(
            "view.actionsDescription",
            "Create a new floating image or close the current pins.");
        _manager.RefreshLocalization();
        UpdateCount();
    }

    private async void OnCapture(object sender, RoutedEventArgs e) =>
        await _manager.CaptureRegionAsync();
    private void OnCloseAll(object sender, RoutedEventArgs e)
    {
        _manager.CloseAll();
    }
    private void OnCountChanged(object? sender, EventArgs e) => UpdateCount();
    private void UpdateCount()
    {
        CountText.Text = T("status.pinnedSummary", "Pinned images currently on screen");
        CountBadgeText.Text = _manager.Count.ToString();
        CloseAllButton.IsEnabled = _manager.Count > 0;
    }
}
