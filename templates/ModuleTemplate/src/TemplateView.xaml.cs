using System.Windows;
using System.Windows.Controls;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Modules.Template;

public partial class TemplateView : UserControl, ILocalizedModuleView
{
    private readonly ILocalizationService _localization;
    private readonly string _moduleId;

    public TemplateView(ILocalizationService localization, string moduleId)
    {
        InitializeComponent();
        _localization = localization;
        _moduleId = moduleId;
        RefreshLocalization();
    }

    private string T(string key, string fallback) =>
        _localization.GetModuleString(_moduleId, key, fallback);

    public void RefreshLocalization()
    {
        TitleText.Text = T("view.title", "Template Module");
        SubtitleText.Text = T(
            "view.subtitle",
            "Use this template to build a localized QingToolbox module.");
        PrimaryButton.Content = T("actions.primary", "Primary Action");
        StatusText.Text = T("status.ready", "Ready.");
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = T("status.done", "Done.");
    }
}
