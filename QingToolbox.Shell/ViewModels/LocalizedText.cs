using System.ComponentModel;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Shell.ViewModels;

public sealed class LocalizedText : INotifyPropertyChanged
{
    private readonly ILocalizationService _localization;

    public LocalizedText(ILocalizationService localization)
    {
        _localization = localization;
        _localization.CultureChanged += (_, _) =>
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs("Item[]"));
    }

    public string this[string key] => _localization.GetString(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}
