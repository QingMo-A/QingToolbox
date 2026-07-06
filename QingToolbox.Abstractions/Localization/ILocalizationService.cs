using System.Globalization;

namespace QingToolbox.Abstractions.Localization;

public interface ILocalizationService
{
    CultureInfo CurrentCulture { get; }
    string CurrentLanguageCode { get; }
    event EventHandler? CultureChanged;
    string GetString(string key);
    string GetString(string key, params object[] args);
    string GetModuleString(string moduleId, string key, string? fallback = null);
    string GetModuleString(
        string moduleId,
        string key,
        string? fallback,
        params object[] args);
}
