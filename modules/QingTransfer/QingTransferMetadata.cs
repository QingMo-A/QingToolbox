using System.Collections.ObjectModel;
using System.Net;

namespace QingToolbox.Modules.QingTransfer;

internal static class QingTransferMetadata
{
    internal const string ServiceType = "_qingtransfer._tcp";
    internal const string WindowsServiceType = "_qingtransfer._tcp.local";
    internal const string AndroidServiceType = "_qingtransfer._tcp.";
    internal const string ProtocolVersion = "1";
    private const int MaxServiceNameLength = 255;
    private const int MaxFieldLength = 128;
    private const int MaxDisplayNameLength = 64;

    public static IReadOnlyDictionary<string, string> Create(string platform, string displayName) =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v"] = ProtocolVersion,
            ["pf"] = platform,
            ["name"] = displayName,
            ["cap"] = "file",
        });

    public static QingTransferPeer? Parse(
        string? serviceName,
        IReadOnlyDictionary<string, string?> fields,
        string? hostName,
        int port,
        IEnumerable<IPAddress>? addresses = null,
        DateTimeOffset? lastSeen = null)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return null;
        var normalizedServiceName = serviceName.Trim().TrimEnd('.');
        if (normalizedServiceName.Length > MaxServiceNameLength) return null;
        if (!normalizedServiceName.EndsWith("._qingtransfer._tcp.local", StringComparison.OrdinalIgnoreCase)) return null;
        if (!TryField(fields, "v", out var version) || version != ProtocolVersion) return null;
        if (!TryField(fields, "pf", out var platform) ||
            (platform != "windows" && platform != "android")) return null;
        if (!TryField(fields, "name", out var name) || !IsSafeDisplayName(name)) return null;
        if (!TryField(fields, "cap", out var capabilityText)) return null;
        var capabilities = capabilityText
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(value => value.Length <= MaxFieldLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!capabilities.Contains("file", StringComparer.OrdinalIgnoreCase)) return null;
        if (port is < 0 or > ushort.MaxValue) return null;

        var safeAddresses = (addresses ?? []).Distinct().Take(8).ToArray();
        return new QingTransferPeer(
            normalizedServiceName,
            name,
            platform,
            version,
            capabilities,
            safeAddresses,
            port,
            Online: true,
            lastSeen);
    }

    private static bool TryField(
        IReadOnlyDictionary<string, string?> fields,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!fields.TryGetValue(key, out var candidate) || string.IsNullOrWhiteSpace(candidate)) return false;
        candidate = candidate.Trim();
        if (candidate.Length > MaxFieldLength || candidate.Any(char.IsControl)) return false;
        value = candidate;
        return true;
    }

    private static bool IsSafeDisplayName(string value) =>
        value.Length is > 0 and <= MaxDisplayNameLength && !value.Any(char.IsControl);
}
