using System.Globalization;

namespace QingToolbox.Modules.QingTransfer;

/// <summary>Small, deterministic DNS-SD identity helpers shared by discovery and tests.</summary>
internal static class QingTransferIdentity
{
    public static string CanonicalServiceName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>
    /// Identifies this host's own Windows registrations, including the DNS-SD
    /// conflict suffix generated when two local processes advertise together.
    /// A similarly named but unrelated host (for example QINGMO-LAPTOP) is not
    /// filtered because only the canonical local label and numeric conflict
    /// variants are accepted.
    /// </summary>
    public static bool IsLocalWindowsRegistration(QingTransferPeer peer, string friendlyName)
    {
        if (!string.Equals(peer.Platform, "windows", StringComparison.OrdinalIgnoreCase)) return false;
        var canonical = CanonicalServiceName(peer.ServiceName);
        var suffix = $".{QingTransferMetadata.ServiceType}.local";
        if (!canonical.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var label = canonical[..^suffix.Length];
        var baseLabel = SanitizeDnsLabel(friendlyName).ToLowerInvariant();
        if (string.Equals(label, baseLabel, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = $"{baseLabel} (";
        if (!label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !label.EndsWith(")", StringComparison.Ordinal)) return false;
        return int.TryParse(label[prefix.Length..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var conflict) && conflict > 0;
    }

    private static string SanitizeDnsLabel(string value)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in value)
            if (char.IsLetterOrDigit(c) || c == '-') builder.Append(c);
        return builder.Length == 0 ? "qingtoolbox" : builder.ToString()[..Math.Min(builder.Length, 63)];
    }
}
