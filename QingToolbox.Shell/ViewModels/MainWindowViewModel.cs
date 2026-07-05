using CommunityToolkit.Mvvm.ComponentModel;

namespace QingToolbox.Shell.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "QingToolbox";

    [ObservableProperty]
    private string _status = "选择一个模块以开始";
}
