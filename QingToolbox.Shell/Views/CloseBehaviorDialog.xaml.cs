using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Core.Settings;

namespace QingToolbox.Shell.Views;

public partial class CloseBehaviorDialog : Window
{
    public CloseBehaviorDialog(ILocalizationService localization)
    {
        InitializeComponent();
        Title = localization.GetString("closeDialog.title");
        DialogTitleBar.CloseText = localization.GetString("window.close");
        DialogTitleBar.SystemMenuText = localization.GetString("window.systemMenu");
        DialogTitleBar.AutomationName = localization.GetString("window.titleBar");
        HeadingText.Text = Title;
        DescriptionText.Text = localization.GetString("closeDialog.description");
        TrayTitleText.Text = localization.GetString("closeDialog.trayTitle");
        TrayDescriptionText.Text = localization.GetString("closeDialog.trayDescription");
        ExitTitleText.Text = localization.GetString("closeDialog.exitTitle");
        ExitDescriptionText.Text = localization.GetString("closeDialog.exitDescription");
        CancelButton.Content = localization.GetString("common.cancel");
    }

    public MainWindowCloseBehavior? SelectedBehavior { get; private set; }
    private void OnTrayClick(object sender, RoutedEventArgs e) => Complete(MainWindowCloseBehavior.MinimizeToNotificationArea);
    private void OnExitClick(object sender, RoutedEventArgs e) => Complete(MainWindowCloseBehavior.ExitApplication);
    private void OnCancelClick(object sender, RoutedEventArgs e) { SelectedBehavior = null; DialogResult = false; }
    private void Complete(MainWindowCloseBehavior behavior) { SelectedBehavior = behavior; DialogResult = true; }
}
