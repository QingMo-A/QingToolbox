using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace QingToolbox.Shell.Converters;

public sealed class ModuleIconSourceConverter : IValueConverter
{
    private static readonly Uri DefaultIconUri = new(
        "pack://application:,,,/QingToolbox.Shell;component/Assets/Icons/Nieobie/modules.svg",
        UriKind.Absolute);

    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        if (value is string iconPath && File.Exists(iconPath))
        {
            return new Uri(iconPath, UriKind.Absolute);
        }

        return DefaultIconUri;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
