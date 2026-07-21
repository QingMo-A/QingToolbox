using System.Windows;
using System.Windows.Threading;
using QingToolbox.Shell.Views;
using QingToolbox.Abstractions.Localization;
using System.Reflection;

namespace QingToolbox.Shell.Services;

public sealed class ModuleWindowManager(ILocalizationService localization)
{
    private readonly Dictionary<string, ModuleHostWindow> _windows =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, WindowState> _suspendedWindows =
        new(StringComparer.Ordinal);

    public bool IsWindowOpen(string moduleId) => _windows.ContainsKey(moduleId);

    public async Task<bool> IsWindowOpenAsync(
        string moduleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return IsWindowOpen(moduleId);
        }

        return await dispatcher.InvokeAsync(
            () => IsWindowOpen(moduleId),
            DispatcherPriority.Send,
            cancellationToken);
    }

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

    public async Task<bool> CloseWindowAsync(
        string moduleId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return !IsWindowOpen(moduleId);
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            try
            {
                if (!_windows.TryGetValue(moduleId, out var window))
                {
                    completion.TrySetResult(true);
                    return;
                }

                window.ReleaseModuleContent();
                window.Close();
                _ = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                    completion.TrySetResult(!_windows.ContainsKey(moduleId)));
            }
            catch
            {
                completion.TrySetResult(false);
            }
        });

        try
        {
            return await completion.Task.WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    internal async Task<string?> GetOpenViewCanaryVersionAsync(
        string moduleId,
        CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return null;
        }

        return await dispatcher.InvokeAsync(() =>
        {
            if (!_windows.TryGetValue(moduleId, out var window) ||
                window.HostedContent is null)
            {
                return null;
            }

            return window.HostedContent.GetType().Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "QingToolboxCanaryVersion")?
                .Value;
        }, DispatcherPriority.Send, cancellationToken);
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

    public int CloseAllSafely()
    {
        var failures = 0;
        foreach (var (moduleId, window) in _windows.ToArray())
        {
            try
            {
                window.Close();
                if (!window.IsVisible) _windows.Remove(moduleId);
            }
            catch
            {
                failures++;
            }
        }
        _suspendedWindows.Clear();
        return failures;
    }

    public void SuspendForFloatingBadge()
    {
        _suspendedWindows.Clear();
        foreach (var (moduleId, window) in _windows)
        {
            if (!window.IsVisible) continue;
            _suspendedWindows[moduleId] = window.WindowState;
            window.Hide();
        }
    }

    public void RestoreAfterFloatingBadge()
    {
        foreach (var (moduleId, state) in _suspendedWindows.ToArray())
        {
            if (!_windows.TryGetValue(moduleId, out var window)) continue;
            window.Show();
            window.WindowState = state;
        }
        _suspendedWindows.Clear();
    }
}
