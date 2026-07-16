using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace QingToolbox.Shell.Startup;

public enum ApplicationEnvironmentKind { Production, Development, ModuleTest }

public sealed record ApplicationExecutionEnvironment
{
    private ApplicationExecutionEnvironment(
        ApplicationEnvironmentKind kind,
        string profileName,
        string? sandboxRoot)
    {
        Kind = kind;
        ProfileName = profileName;
        SandboxRoot = sandboxRoot;
        IsProduction = kind == ApplicationEnvironmentKind.Production;
        IsDevelopment = kind == ApplicationEnvironmentKind.Development;
        IsModuleTest = kind == ApplicationEnvironmentKind.ModuleTest;
        AllowWindowsStartupRegistration = IsProduction;
        DisplayName = kind switch
        {
            ApplicationEnvironmentKind.Development => $"QingToolbox [DEV: {profileName}]",
            ApplicationEnvironmentKind.ModuleTest => $"QingToolbox [MODULE TEST: {profileName}]",
            _ => "QingToolbox"
        };
        InstanceScope = IsProduction ? "Production.Default" : BuildScope(kind, profileName, sandboxRoot!);
    }

    public ApplicationEnvironmentKind Kind { get; }
    public string ProfileName { get; }
    public string? SandboxRoot { get; }
    public string InstanceScope { get; }
    public string DisplayName { get; }
    public bool IsProduction { get; }
    public bool IsDevelopment { get; }
    public bool IsModuleTest { get; }
    public bool AllowWindowsStartupRegistration { get; }

    public static ApplicationExecutionEnvironment Production() =>
        new(ApplicationEnvironmentKind.Production, "Default", null);

    public static ApplicationExecutionEnvironment Sandbox(
        ApplicationEnvironmentKind kind,
        string profileName,
        string sandboxRoot)
    {
        if (kind == ApplicationEnvironmentKind.Production)
            throw new ArgumentException("Production cannot use a sandbox root.", nameof(kind));
        ValidateProfileName(profileName);
        var normalized = ValidateSandboxRoot(sandboxRoot);
        return new(kind, profileName, normalized);
    }

    public static void ValidateProfileName(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName) || profileName.Length > 64 ||
            profileName != profileName.Trim() || profileName is "." or ".." ||
            profileName.Any(character => !(char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-')))
            throw new ArgumentException("Profile must contain 1-64 letters, digits, '.', '_' or '-' only.", nameof(profileName));
    }

    public static string ValidateSandboxRoot(string sandboxRoot)
    {
        if (string.IsNullOrWhiteSpace(sandboxRoot) || !Path.IsPathFullyQualified(sandboxRoot))
            throw new ArgumentException("Data root must be an absolute path.", nameof(sandboxRoot));
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxRoot));
        var pathRoot = Path.GetPathRoot(normalized);
        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(pathRoot ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Data root cannot be a drive or UNC root.", nameof(sandboxRoot));
        if (File.Exists(normalized)) throw new ArgumentException("Data root cannot point to a file.", nameof(sandboxRoot));
        foreach (var productionRoot in GetProductionRoots())
        {
            if (normalized.Equals(productionRoot, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(productionRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Data root cannot be the production data directory or a child of it.", nameof(sandboxRoot));
        }
        return normalized;
    }

    private static IEnumerable<string> GetProductionRoots()
    {
        yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QingToolbox")));
        yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingToolbox")));
    }

    private static string BuildScope(ApplicationEnvironmentKind kind, string profileName, string root)
    {
        var identity = $"{kind}\0{profileName}\0{Path.TrimEndingDirectorySeparator(root).ToUpperInvariant()}";
        return $"{kind}.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24]}";
    }
}
