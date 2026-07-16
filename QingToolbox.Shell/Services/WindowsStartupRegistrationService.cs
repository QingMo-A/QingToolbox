using Microsoft.Win32;
using QingToolbox.Core.Settings;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.Services;

public interface IStartupRegistrationStore
{
    string? Read();
    void Write(string command);
    void Delete();
}

public sealed class WindowsRunRegistrationStore : IStartupRegistrationStore
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QingToolbox";
    public string? Read() => Registry.CurrentUser.OpenSubKey(KeyPath)?.GetValue(ValueName) as string;
    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

public sealed record StartupRegistrationState(bool IsRegistered, bool MatchesCurrentExecutable, string? Error = null);

public sealed class WindowsStartupRegistrationService(
    UserSettingsService settingsService,
    IStartupRegistrationStore store,
    ApplicationExecutionEnvironment environment)
{
    public bool IsAvailable => environment.AllowWindowsStartupRegistration;
    public static string BuildCommand(string executablePath) => $"\"{executablePath}\" --startup";
    private static string CurrentCommand => BuildCommand(Environment.ProcessPath
        ?? throw new InvalidOperationException("The executable path is unavailable."));

    public Task<StartupRegistrationState> GetStateAsync() => Task.Run(() =>
    {
        if (!IsAvailable) return new StartupRegistrationState(false, false,
            "Windows login startup is unavailable in development and module test environments.");
        try
        {
            var actual = store.Read();
            return new StartupRegistrationState(actual is not null,
                string.Equals(actual, CurrentCommand, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) { return new StartupRegistrationState(false, false, exception.Message); }
    });

    public async Task SetEnabledAsync(bool enabled)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(
                "Windows login startup is unavailable in development and module test environments.");
        var previous = await Task.Run(store.Read);
        try
        {
            await Task.Run(() => { if (enabled) store.Write(CurrentCommand); else store.Delete(); });
            await settingsService.UpdateAsync(settings => settings.LaunchAtLogin = enabled);
        }
        catch
        {
            try { await Task.Run(() => { if (previous is null) store.Delete(); else store.Write(previous); }); } catch { }
            throw;
        }
    }

    public async Task ReconcileAsync(UserSettings settings)
    {
        if (!IsAvailable) return;
        if (!settings.LaunchAtLogin)
        {
            if ((await GetStateAsync()).IsRegistered) await Task.Run(store.Delete);
            return;
        }
        var state = await GetStateAsync();
        if (!state.IsRegistered || !state.MatchesCurrentExecutable) await Task.Run(() => store.Write(CurrentCommand));
    }
}
