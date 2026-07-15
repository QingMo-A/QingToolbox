using System.Windows;
using QingToolbox.Shell.Views;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Shell.Services;

public sealed class ModuleWindowManager(ILocalizationService localization)
{
    private readonly Dictionary<string, ModuleHostWindow> _windows =
        new(StringComparer.Ordinal);

    public bool IsWindowOpen(string moduleId) => _windows.ContainsKey(moduleId);

    public void OpenWindow(
        string moduleId,
        string title,
        object moduleView,
        Window? owner)
    {
        if (_windows.TryGetValue(moduleId, out var existingWindow))
        {
            if (existingWindow.WindowState == WindowState.Minimized)
            {
                existingWindow.WindowState = WindowState.Normal;
            }

            existingWindow.Activate();
            return;
        }

        var window = new ModuleHostWindow(moduleId, title, moduleView, localization)
        {
            Owner = owner
        };

        window.Closed += (_, _) => _windows.Remove(moduleId);
        _windows.Add(moduleId, window);
        window.Show();
        window.Activate();
    }

    public void ActivateWindow(string moduleId)
    {
        if (!_windows.TryGetValue(moduleId, out var window))
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    public void CloseWindow(string moduleId)
    {
        if (_windows.TryGetValue(moduleId, out var window))
        {
            window.Close();
        }
    }

    public void RefreshOpenWindowLocalization(Func<string, string?> titleResolver)
    {
        foreach (var (moduleId, window) in _windows)
        {
            window.UpdateLocalizedTitle(titleResolver(moduleId));
            window.RefreshLocalization();
        }
    }

    public void CloseAll()
    {
        foreach (var window in _windows.Values.ToArray())
        {
            window.Close();
        }
    }
}
