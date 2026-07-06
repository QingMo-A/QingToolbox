using CommunityToolkit.Mvvm.ComponentModel;

namespace QingToolbox.Shell.ViewModels;

public sealed partial class LanguageOptionViewModel(
    string code,
    string displayText) : ObservableObject
{
    public string Code { get; } = code;

    [ObservableProperty]
    private string _displayText = displayText;
}
