using System.Windows;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Shell.Views;

public partial class ModuleRemovalDialog : Window
{
    public ModuleRemovalDialog(ILocalizationService localization, string moduleName)
    {
        InitializeComponent();
        Title = localization.GetString("modules.removeDialogTitle");
        DialogTitleBar.CloseText = localization.GetString("window.close");
        DialogTitleBar.SystemMenuText = localization.GetString("window.systemMenu");
        DialogTitleBar.AutomationName = localization.GetString("window.titleBar");
        HeadingText.Text = localization.GetString("modules.removeHeading", moduleName);
        DescriptionText.Text = localization.GetString("modules.removeDescription");
        DataNoticeText.Text = localization.GetString("modules.removeDataNotice");
        CancelButton.Content = localization.GetString("common.cancel");
        RemoveButton.Content = localization.GetString("modules.removeConfirm");
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnRemoveClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
