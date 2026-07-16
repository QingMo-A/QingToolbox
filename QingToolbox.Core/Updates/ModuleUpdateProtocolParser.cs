using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QingToolbox.Core.Updates;

public sealed class ModuleUpdateProtocolException(string message) : Exception(message);

public static class ModuleUpdateProtocolParser
{
    private static readonly Regex ModuleId = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+$", RegexOptions.CultureInvariant);
    public static OfficialModuleIndex ParseIndex(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > 256 * 1024) throw Error("Index exceeds 256 KiB.");
        using var document = Parse(payload);
        var root = document.RootElement; Object(root, "index"); Fields(root, "schemaVersion", "sourceId", "modules");
        if (Int(root, "schemaVersion") != 1) throw Error("Unsupported index schemaVersion.");
        if (Text(root, "sourceId") != "qingtoolbox-official") throw Error("Invalid sourceId.");
        var modules = Required(root, "modules"); Object(modules, "modules");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in modules.EnumerateObject())
        {
            if (!ModuleId.IsMatch(item.Name)) throw Error($"Invalid moduleId: {item.Name}");
            Object(item.Value, item.Name); Fields(item.Value, "updateManifest");
            var path = Text(item.Value, "updateManifest"); ValidatePath(path);
            if (!targets.Add(path)) throw Error("Duplicate updateManifest target.");
            result.Add(item.Name, path);
        }
        return new(result);
    }

    public static ModuleUpdateManifest ParseUpdate(ReadOnlyMemory<byte> payload, string expectedModuleId)
    {
        if (payload.Length > 128 * 1024) throw Error("Update manifest exceeds 128 KiB.");
        using var document = Parse(payload);
        var root = document.RootElement; Object(root, "update"); Fields(root, "schemaVersion", "moduleId", "publisher", "releases");
        if (Int(root, "schemaVersion") != 1) throw Error("Unsupported update schemaVersion.");
        var id = Text(root, "moduleId"); if (id != expectedModuleId) throw Error("update moduleId mismatch.");
        if (Text(root, "publisher") != "QingMo-A") throw Error("Invalid publisher.");
        var releasesElement = Required(root, "releases");
        if (releasesElement.ValueKind != JsonValueKind.Array) throw Error("releases must be an array.");
        var releases = new List<ModuleUpdateRelease>(); var versions = new HashSet<string>(StringComparer.Ordinal);
        SemanticVersion? previous = null;
        foreach (var item in releasesElement.EnumerateArray())
        {
            Object(item, "release"); Fields(item, "version", "channel", "moduleApiVersion", "minimumHostVersion",
                "maximumHostVersionExclusive", "publishedAt", "package", "releaseNotes");
            var versionText = Text(item, "version");
            if (!SemanticVersion.TryParse(versionText, out var version)) throw Error("Invalid release SemVer.");
            if (!versions.Add(versionText)) throw Error("Duplicate release version.");
            if (previous is not null && previous.CompareTo(version) <= 0) throw Error("Releases must be sorted descending.");
            previous = version;
            var channelText = Text(item, "channel");
            var channel = channelText switch { "preview" => ModuleUpdateChannel.Preview, "stable" => ModuleUpdateChannel.Stable, _ => throw Error("Invalid channel.") };
            if (channel == ModuleUpdateChannel.Stable && version!.IsPrerelease) throw Error("Stable channel cannot contain prerelease.");
            var api = Text(item, "moduleApiVersion"); if (string.IsNullOrWhiteSpace(api)) throw Error("Empty moduleApiVersion.");
            if (!SemanticVersion.TryParse(Text(item, "minimumHostVersion"), out var minimum)) throw Error("Invalid minimumHostVersion.");
            SemanticVersion? maximum = null;
            if (item.TryGetProperty("maximumHostVersionExclusive", out var maxElement))
            {
                if (maxElement.ValueKind != JsonValueKind.String || !SemanticVersion.TryParse(maxElement.GetString(), out maximum)) throw Error("Invalid maximumHostVersionExclusive.");
                if (maximum!.CompareTo(minimum) <= 0) throw Error("Maximum host version must exceed minimum.");
            }
            var timestamp = Text(item, "publishedAt");
            if (!timestamp.EndsWith('Z') || !DateTimeOffset.TryParse(timestamp, out var published) || published.Offset != TimeSpan.Zero) throw Error("publishedAt must be UTC with Z.");
            var packageElement = Required(item, "package"); Object(packageElement, "package"); Fields(packageElement, "fileName", "url", "size", "sha256");
            var fileName = Text(packageElement, "fileName");
            if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".qmod", StringComparison.OrdinalIgnoreCase)) throw Error("Invalid qmod fileName.");
            var urlText = Text(packageElement, "url");
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
                !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !url.AbsolutePath.StartsWith("/QingMo-A/QingToolbox/releases/download/", StringComparison.Ordinal) ||
                url.AbsolutePath.Contains("/latest/", StringComparison.OrdinalIgnoreCase) || url.Query.Length != 0 || url.Fragment.Length != 0 ||
                Uri.UnescapeDataString(url.Segments[^1]) != fileName) throw Error("Invalid immutable package URL.");
            var size = Long(packageElement, "size"); if (size <= 0) throw Error("Package size must be positive.");
            var sha = Text(packageElement, "sha256"); if (!Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$")) throw Error("Invalid SHA256.");
            var notesElement = Required(item, "releaseNotes"); Object(notesElement, "releaseNotes"); Fields(notesElement, "zh-CN", "en-US");
            var notes = new Dictionary<string, string>(StringComparer.Ordinal) { ["zh-CN"] = Text(notesElement, "zh-CN"), ["en-US"] = Text(notesElement, "en-US") };
            if (notes.Values.Any(string.IsNullOrWhiteSpace)) throw Error("Release notes cannot be empty.");
            releases.Add(new(version!, channel, api, minimum!, maximum, published, new(fileName, url, size, sha), notes));
        }
        return new(id, "QingMo-A", releases);
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> payload)
    {
        if (payload.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) throw Error("UTF-8 BOM is not allowed.");
        try
        {
            var reader = new Utf8JsonReader(payload.Span, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var stack = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) stack.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) stack.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !stack.Peek().Add(reader.GetString()!)) throw Error($"Duplicate JSON property: {reader.GetString()}");
            }
            return JsonDocument.Parse(payload, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (ModuleUpdateProtocolException) { throw; }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException) { throw Error("Invalid strict JSON."); }
    }
    private static void ValidatePath(string path)
    {
        if (path.Contains('\\') || path.Contains('?') || path.Contains('#') || path.StartsWith('/') || Uri.TryCreate(path, UriKind.Absolute, out _) ||
            path.Split('/').Any(x => x is "" or "." or "..") || !path.EndsWith("/update.json", StringComparison.Ordinal)) throw Error("Unsafe updateManifest path.");
    }
    private static void Fields(JsonElement element, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!set.Contains(property.Name)) throw Error($"Unknown field: {property.Name}");
        foreach (var name in allowed.Where(x => x != "maximumHostVersionExclusive")) if (!element.TryGetProperty(name, out _)) throw Error($"Missing field: {name}");
    }
    private static JsonElement Required(JsonElement e, string n) => e.TryGetProperty(n, out var value) ? value : throw Error($"Missing {n}.");
    private static string Text(JsonElement e, string n) { var v = Required(e, n); return v.ValueKind == JsonValueKind.String ? v.GetString()! : throw Error($"{n} must be string."); }
    private static int Int(JsonElement e, string n) { var v = Required(e, n); return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : throw Error($"{n} must be integer."); }
    private static long Long(JsonElement e, string n) { var v = Required(e, n); return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : throw Error($"{n} must be integer."); }
    private static void Object(JsonElement e, string n) { if (e.ValueKind != JsonValueKind.Object) throw Error($"{n} must be object."); }
    private static ModuleUpdateProtocolException Error(string message) => new(message);
}
