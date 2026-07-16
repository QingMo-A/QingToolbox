using System.Numerics;
using System.Text.RegularExpressions;

namespace QingToolbox.Core.Updates;

public enum ModuleUpdateChannel { Preview, Stable }
public enum ModuleUpdateStatus
{
    NotChecked, Checking, NotOfficial, NoPublishedRelease, UpToDate,
    UpdateAvailable, HostUpdateRequired, ModuleApiIncompatible,
    LocalVersionNewer, InvalidLocalVersion, SourceUnavailable,
    SourceInvalid, DisabledByEnvironment
}

public sealed record ModuleUpdatePackage(string FileName, Uri Url, long Size, string Sha256);
public sealed record ModuleUpdateRelease(
    SemanticVersion Version, ModuleUpdateChannel Channel, string ModuleApiVersion,
    SemanticVersion MinimumHostVersion, SemanticVersion? MaximumHostVersionExclusive,
    DateTimeOffset PublishedAt, ModuleUpdatePackage Package,
    IReadOnlyDictionary<string, string> ReleaseNotes);
public sealed record ModuleUpdateManifest(string ModuleId, string Publisher, IReadOnlyList<ModuleUpdateRelease> Releases);
public sealed record OfficialModuleIndex(IReadOnlyDictionary<string, string> Modules);
public sealed record ModuleUpdateResult(
    string ModuleId, ModuleUpdateStatus Status, SemanticVersion? TargetVersion = null,
    string? ReleaseNote = null, bool IsFromStaleCache = false, DateTimeOffset? CheckedAt = null);
public sealed record InstalledModuleVersion(string ModuleId, string Version);

public static class ModuleUpdateIdentity
{
    public const string ModuleApiVersion = "experimental-0.1";
    public const string OfficialIndexUrl =
        "https://raw.githubusercontent.com/QingMo-A/QingToolbox/modules/modules/index.json";
    public const string OfficialModulesBaseUrl =
        "https://raw.githubusercontent.com/QingMo-A/QingToolbox/modules/modules/";
}

public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant);
    private SemanticVersion(BigInteger major, BigInteger minor, BigInteger patch, string? prerelease, string? build)
        => (Major, Minor, Patch, Prerelease, BuildMetadata) = (major, minor, patch, prerelease, build);
    public BigInteger Major { get; }
    public BigInteger Minor { get; }
    public BigInteger Patch { get; }
    public string? Prerelease { get; }
    public string? BuildMetadata { get; }
    public bool IsPrerelease => Prerelease is not null;
    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (value is null) return false;
        var match = Pattern.Match(value);
        if (!match.Success) return false;
        var pre = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (pre?.Split('.').Any(x => x.Length > 1 && x.All(char.IsDigit) && x[0] == '0') == true) return false;
        version = new(BigInteger.Parse(match.Groups[1].Value), BigInteger.Parse(match.Groups[2].Value),
            BigInteger.Parse(match.Groups[3].Value), pre, match.Groups[5].Success ? match.Groups[5].Value : null);
        return true;
    }
    public static SemanticVersion Parse(string value) => TryParse(value, out var result)
        ? result! : throw new FormatException($"Invalid SemVer: {value}");
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var core = Major.CompareTo(other.Major); if (core != 0) return core;
        core = Minor.CompareTo(other.Minor); if (core != 0) return core;
        core = Patch.CompareTo(other.Patch); if (core != 0) return core;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var left = Prerelease.Split('.'); var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (left[i] == right[i]) continue;
            var ln = left[i].All(char.IsDigit); var rn = right[i].All(char.IsDigit);
            if (ln && rn) return BigInteger.Parse(left[i]).CompareTo(BigInteger.Parse(right[i]));
            if (ln != rn) return ln ? -1 : 1;
            return string.Compare(left[i], right[i], StringComparison.Ordinal);
        }
        return left.Length.CompareTo(right.Length);
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}" +
        (Prerelease is null ? "" : $"-{Prerelease}") + (BuildMetadata is null ? "" : $"+{BuildMetadata}");
}

public sealed class ModuleUpdateCompatibilityEvaluator(
    SemanticVersion hostVersion, string moduleApiVersion, ModuleUpdateChannel channel)
{
    public ModuleUpdateResult Evaluate(string moduleId, string localVersion, ModuleUpdateManifest manifest, DateTimeOffset checkedAt)
    {
        if (!SemanticVersion.TryParse(localVersion, out var local))
            return new(moduleId, ModuleUpdateStatus.InvalidLocalVersion, CheckedAt: checkedAt);
        if (manifest.Releases.Count == 0)
            return new(moduleId, ModuleUpdateStatus.NoPublishedRelease, CheckedAt: checkedAt);
        var allowed = manifest.Releases.Where(r => channel == ModuleUpdateChannel.Preview || r.Channel == ModuleUpdateChannel.Stable).ToArray();
        var api = allowed.Where(r => r.ModuleApiVersion == moduleApiVersion).ToArray();
        if (api.Length == 0 && allowed.Length > 0)
            return new(moduleId, ModuleUpdateStatus.ModuleApiIncompatible, CheckedAt: checkedAt);
        var compatible = api.Where(r => hostVersion.CompareTo(r.MinimumHostVersion) >= 0 &&
            (r.MaximumHostVersionExclusive is null || hostVersion.CompareTo(r.MaximumHostVersionExclusive) < 0)).ToArray();
        if (compatible.Length == 0 && api.Any(r => hostVersion.CompareTo(r.MinimumHostVersion) < 0))
            return new(moduleId, ModuleUpdateStatus.HostUpdateRequired, CheckedAt: checkedAt);
        var target = compatible.FirstOrDefault();
        if (target is null) return new(moduleId, ModuleUpdateStatus.UpToDate, CheckedAt: checkedAt);
        var comparison = target.Version.CompareTo(local);
        return new(moduleId, comparison > 0 ? ModuleUpdateStatus.UpdateAvailable :
            comparison < 0 ? ModuleUpdateStatus.LocalVersionNewer : ModuleUpdateStatus.UpToDate,
            target.Version, target.ReleaseNotes.GetValueOrDefault("en-US"), CheckedAt: checkedAt);
    }
}
