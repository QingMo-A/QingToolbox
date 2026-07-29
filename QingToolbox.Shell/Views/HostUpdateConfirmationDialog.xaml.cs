using System.Windows;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Shell.Views;

public partial class HostUpdateConfirmationDialog : Window
{
    public HostUpdateConfirmationDialog(ILocalizationService localization, string version)
    {
        InitializeComponent();
        Title = localization.GetString("hostUpdate.confirmTitle");
        DialogTitleBar.CloseText = localization.GetString("window.close");
        DialogTitleBar.SystemMenuText = localization.GetString("window.systemMenu");
        DialogTitleBar.AutomationName = localization.GetString("window.titleBar");
        HeadingText.Text = localization.GetString("hostUpdate.confirmHeading", version);
        DescriptionText.Text = localization.GetString("hostUpdate.confirmDescription");
        DataNoticeText.Text = localization.GetString("hostUpdate.confirmPreservation");
        CancelButton.Content = localization.GetString("common.cancel");
        CancelButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, localization.GetString("common.cancel"));
        UpdateButton.Content = localization.GetString("hostUpdate.installNow");
        UpdateButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, localization.GetString("hostUpdate.installNow"));
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnUpdateClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
