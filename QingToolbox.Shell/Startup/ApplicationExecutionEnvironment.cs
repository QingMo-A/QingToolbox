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
        string? repositoryRoot,
        string? sandboxRoot)
    {
        Kind = kind;
        ProfileName = profileName;
        RepositoryRoot = repositoryRoot;
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
    public string? RepositoryRoot { get; }
    public string? SandboxRoot { get; }
    public string InstanceScope { get; }
    public string DisplayName { get; }
    public bool IsProduction { get; }
    public bool IsDevelopment { get; }
    public bool IsModuleTest { get; }
    public bool AllowWindowsStartupRegistration { get; }

    public static ApplicationExecutionEnvironment Production() =>
        new(ApplicationEnvironmentKind.Production, "Default", null, null);

    public static ApplicationExecutionEnvironment Sandbox(
        ApplicationEnvironmentKind kind,
        string profileName,
        string repositoryRoot)
    {
        if (kind is not (ApplicationEnvironmentKind.Development or ApplicationEnvironmentKind.ModuleTest))
            throw new ArgumentException(
                "Sandbox environment must be Development or ModuleTest; Production must use Production().",
                nameof(kind));
        ValidateProfileName(profileName);
        var normalizedRepositoryRoot = ValidateRepositoryRoot(repositoryRoot);
        var sandboxRoot = BuildSandboxRoot(kind, profileName, normalizedRepositoryRoot);
        AssertNoSandboxReparsePoints(kind, profileName, normalizedRepositoryRoot, sandboxRoot);
        return new(kind, profileName, normalizedRepositoryRoot, sandboxRoot);
    }

    public static void ValidateProfileName(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName) || profileName.Length > 64 ||
            profileName != profileName.Trim() || profileName is "." or ".." ||
            profileName.Any(character => !(char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-')))
            throw new ArgumentException("Profile must contain 1-64 letters, digits, '.', '_' or '-' only.", nameof(profileName));
    }

    public static string ValidateRepositoryRoot(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Path.IsPathFullyQualified(repositoryRoot))
            throw new ArgumentException("Repository root must be an absolute path.", nameof(repositoryRoot));
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var pathRoot = Path.GetPathRoot(normalized);
        if (string.Equals(normalized, Path.TrimEndingDirectorySeparator(pathRoot ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Repository root cannot be a drive or UNC root.", nameof(repositoryRoot));
        if (File.Exists(normalized) || !Directory.Exists(normalized))
            throw new ArgumentException("Repository root must be an existing directory.", nameof(repositoryRoot));

        var rootPrefix = normalized + Path.DirectorySeparatorChar;
        foreach (var relativeMarker in new[]
        {
            Path.Combine("QingToolbox.Shell", "QingToolbox.Shell.csproj"),
            Path.Combine("scripts", "start-dev-host.ps1"),
            "Directory.Build.props"
        })
        {
            var marker = Path.GetFullPath(Path.Combine(normalized, relativeMarker));
            if (!marker.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(marker))
                throw new ArgumentException(
                    $"Repository root is not a valid QingToolbox source root; missing marker: {relativeMarker}.",
                    nameof(repositoryRoot));
            AssertOrdinaryPath(marker, expectDirectory: false, "repository marker");
            AssertOrdinaryPath(Path.GetDirectoryName(marker)!, expectDirectory: true, "repository marker parent");
        }
        return normalized;
    }

    private static string BuildSandboxRoot(
        ApplicationEnvironmentKind kind,
        string profileName,
        string repositoryRoot)
    {
        var environmentDirectory = kind == ApplicationEnvironmentKind.Development ? "development" : "module-test";
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
            repositoryRoot, ".qingtoolbox", environmentDirectory, profileName)));
    }

    public static void AssertNoSandboxReparsePoints(
        ApplicationEnvironmentKind kind,
        string profileName,
        string repositoryRoot,
        string sandboxRoot)
    {
        var normalizedRepositoryRoot = ValidateRepositoryRoot(repositoryRoot);
        var normalized = BuildSandboxRoot(kind, profileName, normalizedRepositoryRoot);
        if (!normalized.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxRoot)),
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("Sandbox root does not belong to the declared repository root.");
        var profileDirectory = new DirectoryInfo(normalized);
        var environmentDirectory = profileDirectory.Parent!;
        var localDirectory = environmentDirectory.Parent!;
        foreach (var path in new[] { localDirectory.FullName, environmentDirectory.FullName, profileDirectory.FullName })
        {
            if (File.Exists(path))
                throw new IOException($"Sandbox path segment is not a directory: {path}");
            if (!Directory.Exists(path)) continue;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Sandbox path segment could not be verified safely: {path}", exception);
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Sandbox path cannot contain a reparse point: {path}");
        }
    }

    private static void AssertOrdinaryPath(string path, bool expectDirectory, string description)
    {
        try
        {
            if (expectDirectory ? !Directory.Exists(path) : !File.Exists(path))
                throw new IOException($"Required {description} is unavailable: {path}");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Required {description} cannot be a reparse point: {path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Required {description} could not be verified safely: {path}", exception);
        }
    }

    private static string BuildScope(ApplicationEnvironmentKind kind, string profileName, string root)
    {
        var identity = $"{kind}\0{profileName.ToUpperInvariant()}\0{Path.TrimEndingDirectorySeparator(root).ToUpperInvariant()}";
        return $"{kind}.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24]}";
    }
}
