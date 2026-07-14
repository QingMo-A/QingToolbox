using System.IO;

namespace QingToolbox.Shell.Services;

public sealed class ApplicationPaths
{
    public string DevelopmentModulesDirectory { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "Modules");

    public string UserModulesDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QingToolbox",
        "Modules");

    public string ModuleDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QingToolbox",
        "Data");

    public void EnsureUserDirectories()
    {
        Directory.CreateDirectory(UserModulesDirectory);
        Directory.CreateDirectory(ModuleDataDirectory);
    }
}
