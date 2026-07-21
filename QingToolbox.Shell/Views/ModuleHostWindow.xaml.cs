using System.Windows;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Shell.Views;

public partial class ModuleHostWindow : Window
{
    private readonly ILocalizationService _localization;

    public ModuleHostWindow(
        string moduleId,
        string title,
        object moduleView,
        ILocalizationService localization)
    {
        _localization = localization;
        InitializeComponent();
        ModuleId = moduleId;
        Title = title;
        ModuleContent.Content = moduleView;
        UpdateWindowLocalization();
        Closed += OnClosed;
    }

    public string ModuleId { get; }
    internal object? HostedContent => ModuleContent.Content;
    public string ModuleSubtitle => _localization.GetString("moduleHost.subtitle");
    public string MinimizeText => _localization.GetString("window.minimize");
    public string MaximizeText => _localization.GetString("window.maximize");
    public string RestoreText => _localization.GetString("window.restore");
    public string CloseText => _localization.GetString("window.close");
    public string SystemMenuText => _localization.GetString("window.systemMenu");
    public string TitleBarAutomationName => _localization.GetString("window.titleBar");

    public void RefreshLocalization()
    {
        UpdateWindowLocalization();
        if (ModuleContent.Content is ILocalizedModuleView localizedView)
        {
            localizedView.RefreshLocalization();
        }
        else if (ModuleContent.Content is FrameworkElement { Tag: ILocalizedModuleView tagView })
        {
            tagView.RefreshLocalization();
        }
    }

    private void UpdateWindowLocalization()
    {
        TitleBar.Subtitle = ModuleSubtitle;
        TitleBar.MinimizeText = MinimizeText;
        TitleBar.MaximizeText = MaximizeText;
        TitleBar.RestoreText = RestoreText;
        TitleBar.CloseText = CloseText;
        TitleBar.SystemMenuText = SystemMenuText;
        TitleBar.AutomationName = TitleBarAutomationName;
    }

    public void UpdateLocalizedTitle(string? title)
    {
        if (!string.IsNullOrWhiteSpace(title)) Title = title;
    }

    internal void ReleaseModuleContent()
    {
        ModuleContent.Content = null;
        DataContext = null;
        UpdateLayout();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        ReleaseModuleContent();
    }
}
