using System.Windows;
using QingToolbox.Shell.ViewModels;

namespace QingToolbox.Shell;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
