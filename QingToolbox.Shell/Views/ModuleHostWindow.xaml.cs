using System.Windows;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Shell.Views;

public partial class ModuleHostWindow : Window
{
    public ModuleHostWindow(string moduleId, string title, object moduleView)
    {
        InitializeComponent();
        ModuleId = moduleId;
        Title = title;
        ModuleContent.Content = moduleView;
        Closed += OnClosed;
    }

    public string ModuleId { get; }

    public void RefreshLocalization()
    {
        if (ModuleContent.Content is ILocalizedModuleView localizedView)
        {
            localizedView.RefreshLocalization();
        }
        else if (ModuleContent.Content is FrameworkElement { Tag: ILocalizedModuleView tagView })
        {
            tagView.RefreshLocalization();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        ModuleContent.Content = null;
    }
}
