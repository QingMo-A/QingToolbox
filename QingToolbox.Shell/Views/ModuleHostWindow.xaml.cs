using System.Windows;

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

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        ModuleContent.Content = null;
    }
}
