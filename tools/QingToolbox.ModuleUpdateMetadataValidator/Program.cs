using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.SequenceEqual(["--self-test"])) return SelfTest.Run();
            if (args.Length == 2 && args[0] == "--modules-root")
            {
                var errors = MetadataValidator.Validate(Path.GetFullPath(args[1]));
                foreach (var error in errors) Console.Error.WriteLine(error);
                if (errors.Count > 0) return 1;
                Console.WriteLine("Module update metadata validation passed.");
                return 0;
            }
            Console.Error.WriteLine("Usage: --self-test | --modules-root <path>");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}

internal static class MetadataValidator
{
    private static readonly Regex ModuleIdPattern = new("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    public static List<string> Validate(string modulesRoot)
    {
        var errors = new List<string>();
        if (!Directory.Exists(modulesRoot)) { errors.Add($"{modulesRoot}: modules root does not exist."); return errors; }
        var manifests = new Dictionary<string, (string Directory, string Id)>(StringComparer.Ordinal);
        foreach (var directory in Directory.GetDirectories(modulesRoot).Order(StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, "module.json");
            if (!File.Exists(manifestPath)) continue;
            var document = Parse(manifestPath, errors);
            if (document is null) continue;
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { Error(errors, manifestPath, "$", "module.json must be an object."); continue; }
                if (!TryString(root, "id", manifestPath, "$", errors, out var id) || string.IsNullOrWhiteSpace(id)) continue;
                if (!ModuleIdPattern.IsMatch(id)) Error(errors, manifestPath, "$.id", "invalid canonical moduleId.");
                if (!manifests.TryAdd(id, (directory, id))) Error(errors, manifestPath, "$.id", $"duplicate moduleId '{id}'.");
            }
        }

        var indexPath = Path.Combine(modulesRoot, "index.json");
        var index = Parse(indexPath, errors);
        if (index is null) return errors;
        using (index)
        {
            var root = index.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { Error(errors, indexPath, "$", "index must be an object."); return errors; }
            ExactFields(root, ["schemaVersion", "sourceId", "modules"], indexPath, "$", errors);
            RequireInteger(root, "schemaVersion", 1, indexPath, "$", errors);
            RequireString(root, "sourceId", "qingtoolbox-official", indexPath, "$", errors);
            if (!root.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Object)
            { Error(errors, indexPath, "$.modules", "must be an object."); return errors; }

            var indexedIds = new HashSet<string>(StringComparer.Ordinal);
            var updatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = modules.EnumerateObject().Select(x => x.Name).ToArray();
            if (!names.SequenceEqual(names.Order(StringComparer.Ordinal))) Error(errors, indexPath, "$.modules", "moduleIds must be sorted using Ordinal order.");
            foreach (var item in modules.EnumerateObject())
            {
                var location = $"$.modules.{item.Name}";
                if (!ModuleIdPattern.IsMatch(item.Name)) Error(errors, indexPath, location, "invalid canonical moduleId.");
                indexedIds.Add(item.Name);
                if (item.Value.ValueKind != JsonValueKind.Object) { Error(errors, indexPath, location, "must be an object."); continue; }
                ExactFields(item.Value, ["updateManifest"], indexPath, location, errors);
                if (!TryString(item.Value, "updateManifest", indexPath, location, errors, out var relative)) continue;
                if (!IsSafeRelativePath(relative)) { Error(errors, indexPath, location + ".updateManifest", "must be a safe '/' repository-relative path."); continue; }
                var fullPath = Path.GetFullPath(Path.Combine(modulesRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                var prefix = Path.GetFullPath(modulesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { Error(errors, indexPath, location, "path escapes modules root."); continue; }
                if (!updatePaths.Add(fullPath)) Error(errors, indexPath, location, "duplicate updateManifest target.");
                if (!File.Exists(fullPath)) { Error(errors, indexPath, location, "updateManifest does not exist."); continue; }
                var expectedDirectory = Path.GetDirectoryName(fullPath)!;
                if (!Path.GetFileName(fullPath).Equals("update.json", StringComparison.Ordinal) ||
                    !File.Exists(Path.Combine(expectedDirectory, "module.json")))
                    Error(errors, indexPath, location, "must point to update.json in a module directory root.");
                ValidateUpdate(fullPath, item.Name, expectedDirectory, errors);
            }
            foreach (var id in manifests.Keys.Except(indexedIds, StringComparer.Ordinal)) Error(errors, indexPath, "$.modules", $"missing official module '{id}'.");
            foreach (var id in indexedIds.Except(manifests.Keys, StringComparer.Ordinal)) Error(errors, indexPath, "$.modules", $"indexes nonexistent module '{id}'.");
        }
        return errors;
    }

    private static void ValidateUpdate(string path, string indexedId, string directory, List<string> errors)
    {
        var document = Parse(path, errors); if (document is null) return;
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { Error(errors, path, "$", "update manifest must be an object."); return; }
            ExactFields(root, ["schemaVersion", "moduleId", "publisher", "releases"], path, "$", errors);
            RequireInteger(root, "schemaVersion", 1, path, "$", errors);
            if (TryString(root, "moduleId", path, "$", errors, out var moduleId))
            {
                if (!moduleId.Equals(indexedId, StringComparison.Ordinal)) Error(errors, path, "$.moduleId", "does not match index moduleId.");
                var moduleDocument = Parse(Path.Combine(directory, "module.json"), errors);
                if (moduleDocument is not null) using (moduleDocument)
                    if (!TryString(moduleDocument.RootElement, "id", path, "module.json", errors, out var manifestId) || !moduleId.Equals(manifestId, StringComparison.Ordinal))
                        Error(errors, path, "$.moduleId", "does not match module.json id.");
            }
            RequireString(root, "publisher", "QingMo-A", path, "$", errors);
            if (!root.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            { Error(errors, path, "$.releases", "must be an array."); return; }
            var versions = new HashSet<string>(StringComparer.Ordinal);
            SemVersion? previous = null; var index = 0;
            foreach (var release in releases.EnumerateArray())
            {
                var location = $"$.releases[{index++}]";
                if (release.ValueKind != JsonValueKind.Object) { Error(errors, path, location, "must be an object."); continue; }
                ExactFields(release, ["version", "channel", "moduleApiVersion", "minimumHostVersion", "maximumHostVersionExclusive", "publishedAt", "package", "releaseNotes"], path, location, errors);
                SemVersion? version = ReadSemVer(release, "version", path, location, errors);
                if (version is not null)
                {
                    if (!versions.Add(version.Original)) Error(errors, path, location + ".version", "duplicate release version.");
                    if (previous is not null && previous.CompareTo(version) < 0) Error(errors, path, location + ".version", "releases must be sorted highest SemVer first.");
                    previous = version;
                }
                if (TryString(release, "channel", path, location, errors, out var channel))
                {
                    if (channel is not ("preview" or "stable")) Error(errors, path, location + ".channel", "must be preview or stable.");
                    if (channel == "stable" && version?.IsPrerelease == true) Error(errors, path, location + ".channel", "stable channel cannot contain a prerelease version.");
                }
                if (!TryString(release, "moduleApiVersion", path, location, errors, out var api) || string.IsNullOrWhiteSpace(api)) Error(errors, path, location + ".moduleApiVersion", "must not be empty.");
                var minimum = ReadSemVer(release, "minimumHostVersion", path, location, errors);
                SemVersion? maximum = null;
                if (release.TryGetProperty("maximumHostVersionExclusive", out var maximumElement) && maximumElement.ValueKind != JsonValueKind.Null)
                    maximum = ReadSemVer(release, "maximumHostVersionExclusive", path, location, errors);
                if (minimum is not null && maximum is not null && maximum.CompareTo(minimum) <= 0) Error(errors, path, location + ".maximumHostVersionExclusive", "must be greater than minimumHostVersion.");
                if (!TryString(release, "publishedAt", path, location, errors, out var published) || !published.EndsWith('Z') ||
                    !DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var publishedAt) || publishedAt.Offset != TimeSpan.Zero)
                    Error(errors, path, location + ".publishedAt", "must be UTC ISO 8601 ending in Z.");
                ValidatePackage(release, path, location, errors);
                ValidateNotes(release, path, location, errors);
            }
        }
    }

    private static void ValidatePackage(JsonElement release, string path, string location, List<string> errors)
    {
        if (!release.TryGetProperty("package", out var package) || package.ValueKind != JsonValueKind.Object) { Error(errors, path, location + ".package", "must be an object."); return; }
        ExactFields(package, ["fileName", "url", "size", "sha256"], path, location + ".package", errors);
        var hasName = TryString(package, "fileName", path, location + ".package", errors, out var fileName);
        if (hasName && (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".qmod", StringComparison.OrdinalIgnoreCase) || fileName.Contains('/') || fileName.Contains('\\')))
            Error(errors, path, location + ".package.fileName", "must be a plain .qmod file name.");
        if (TryString(package, "url", path, location + ".package", errors, out var url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com") Error(errors, path, location + ".package.url", "must use HTTPS github.com.");
            else
            {
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length != 6 || segments[0] != "QingMo-A" || segments[1] != "QingToolbox" || segments[2] != "releases" || segments[3] != "download" ||
                    segments[4].Equals("latest", StringComparison.OrdinalIgnoreCase) || Uri.UnescapeDataString(segments[5]) != fileName || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                    Error(errors, path, location + ".package.url", "must identify an immutable QingMo-A/QingToolbox Release Asset and match fileName.");
            }
        }
        if (!package.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes) || bytes <= 0) Error(errors, path, location + ".package.size", "must be a positive integer.");
        if (!TryString(package, "sha256", path, location + ".package", errors, out var hash) || !Regex.IsMatch(hash, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)) Error(errors, path, location + ".package.sha256", "must be 64 hexadecimal characters.");
    }

    private static void ValidateNotes(JsonElement release, string path, string location, List<string> errors)
    {
        if (!release.TryGetProperty("releaseNotes", out var notes) || notes.ValueKind != JsonValueKind.Object) { Error(errors, path, location + ".releaseNotes", "must be an object."); return; }
        ExactFields(notes, ["zh-CN", "en-US"], path, location + ".releaseNotes", errors);
        foreach (var culture in new[] { "zh-CN", "en-US" })
            if (!TryString(notes, culture, path, location + ".releaseNotes", errors, out var text) || string.IsNullOrWhiteSpace(text)) Error(errors, path, location + $".releaseNotes.{culture}", "must not be empty.");
    }

    private static JsonDocument? Parse(string path, List<string> errors)
    {
        if (!File.Exists(path)) { Error(errors, path, "$", "file does not exist."); return null; }
        try
        {
            var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                Error(errors, path, "$", "UTF-8 BOM is not allowed.");
            var document = JsonDocument.Parse(bytes, JsonOptions);
            FindDuplicates(document.RootElement, path, "$", errors);
            return document;
        }
        catch (JsonException exception) { Error(errors, path, $"line {exception.LineNumber}, byte {exception.BytePositionInLine}", exception.Message); return null; }
    }

    private static void FindDuplicates(JsonElement element, string path, string location, List<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) Error(errors, path, location, $"duplicate JSON property '{property.Name}'."); FindDuplicates(property.Value, path, location + "." + property.Name, errors); }
        }
        else if (element.ValueKind == JsonValueKind.Array) { var i = 0; foreach (var item in element.EnumerateArray()) FindDuplicates(item, path, $"{location}[{i++}]", errors); }
    }

    private static bool IsSafeRelativePath(string path) => !string.IsNullOrWhiteSpace(path) && !path.Contains('\\') && !path.Contains('?') && !path.Contains('#') && !Path.IsPathRooted(path) &&
        !Regex.IsMatch(path, "^[A-Za-z]:", RegexOptions.CultureInvariant) && path.Split('/').All(x => x.Length > 0 && x is not ("." or ".."));
    private static void ExactFields(JsonElement element, string[] allowed, string path, string location, List<string> errors)
    { var actual = element.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal); foreach (var field in actual.Except(allowed, StringComparer.Ordinal)) Error(errors, path, location + "." + field, "unknown field."); foreach (var field in allowed.Except(actual, StringComparer.Ordinal)) Error(errors, path, location + "." + field, "required field is missing."); }
    private static bool TryString(JsonElement element, string name, string path, string location, List<string> errors, out string value)
    { value = ""; if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) { Error(errors, path, location + "." + name, "must be a string."); return false; } value = property.GetString()!; return true; }
    private static void RequireString(JsonElement element, string name, string expected, string path, string location, List<string> errors)
    { if (TryString(element, name, path, location, errors, out var value) && value != expected) Error(errors, path, location + "." + name, $"must equal '{expected}'."); }
    private static void RequireInteger(JsonElement element, string name, int expected, string path, string location, List<string> errors)
    { if (!element.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number != expected) Error(errors, path, location + "." + name, $"must be integer {expected}."); }
    private static SemVersion? ReadSemVer(JsonElement element, string name, string path, string location, List<string> errors)
    { if (!TryString(element, name, path, location, errors, out var text)) return null; if (!SemVersion.TryParse(text, out var version)) { Error(errors, path, location + "." + name, "must be SemVer 2.0."); return null; } return version; }
    private static void Error(List<string> errors, string path, string location, string message) => errors.Add($"{path} {location}: {message}");
}

internal sealed class SemVersion : IComparable<SemVersion>
{
    private static readonly Regex Pattern = new("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant);
    private readonly string[] _prerelease;
    private SemVersion(string original, BigInteger major, BigInteger minor, BigInteger patch, string[] prerelease) { Original = original; Major = major; Minor = minor; Patch = patch; _prerelease = prerelease; }
    public string Original { get; } public BigInteger Major { get; } public BigInteger Minor { get; } public BigInteger Patch { get; } public bool IsPrerelease => _prerelease.Length > 0;
    public static bool TryParse(string text, out SemVersion? result)
    {
        result = null; var match = Pattern.Match(text); if (!match.Success || !BigInteger.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var major) || !BigInteger.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var minor) || !BigInteger.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var patch)) return false;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(x => x.All(char.IsAsciiDigit) && x.Length > 1 && x[0] == '0')) return false;
        result = new(text, major, minor, patch, prerelease); return true;
    }
    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1; var core = Major.CompareTo(other.Major); if (core == 0) core = Minor.CompareTo(other.Minor); if (core == 0) core = Patch.CompareTo(other.Patch); if (core != 0) return core;
        if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        for (var i = 0; i < Math.Max(_prerelease.Length, other._prerelease.Length); i++)
        { if (i == _prerelease.Length) return -1; if (i == other._prerelease.Length) return 1; var left = _prerelease[i]; var right = other._prerelease[i]; var ln = left.All(char.IsAsciiDigit); var rn = right.All(char.IsAsciiDigit); int comparison; if (ln && rn) comparison = left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.Compare(left, right, StringComparison.Ordinal); else if (ln != rn) comparison = ln ? -1 : 1; else comparison = string.Compare(left, right, StringComparison.Ordinal); if (comparison != 0) return comparison; }
        return 0;
    }
}

internal static class SelfTest
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"QingToolbox-update-validator-{Guid.NewGuid():N}");
        try
        {
            WriteFourModuleFixture(root); RequireValid(root, "four-module empty releases");
            var preview = "[" + Release("0.1.0-alpha.1", "preview", "0.2.0-alpha") + "]"; WriteFixture(root, preview); RequireValid(root, "preview release");
            var stable = "[" + Release("1.0.0", "stable", "2.0.0") + "]"; WriteFixture(root, stable); RequireValid(root, "stable release");
            if (!SemVersion.TryParse("1.2.3+build.5", out var build) || !SemVersion.TryParse("1.2.3+build.6", out var other) || build!.CompareTo(other) != 0) throw new Exception("Self-test failed: build metadata comparison.");
            if (!SemVersion.TryParse("1.0.0", out var final) || !SemVersion.TryParse("1.0.0-alpha", out var alpha) || final!.CompareTo(alpha) <= 0) throw new Exception("Self-test failed: SemVer ordering.");

            var invalidCases = new Dictionary<string, Func<string, string>>(StringComparer.Ordinal)
            {
                ["duplicate JSON field"] = x => x.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"schemaVersion\": 1,"),
                ["unknown top-level field"] = x => x.Replace("\"publisher\": \"QingMo-A\",", "\"publisher\": \"QingMo-A\", \"typo\": true,"),
                ["wrong schema"] = x => x.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"),
                ["module mismatch"] = x => x.Replace("qing.test", "qing.other"),
                ["invalid SemVer"] = x => x.Replace("0.1.0-alpha.1", "01.1.0"),
                ["v SemVer"] = x => x.Replace("0.1.0-alpha.1", "v0.1.0"),
                ["stable prerelease"] = x => x.Replace("\"channel\":\"preview\"", "\"channel\":\"stable\""),
                ["invalid maximum"] = x => x.Replace("0.2.0-alpha", "0.1.0"),
                ["non UTC date"] = x => x.Replace("2026-07-20T08:00:00Z", "2026-07-20T08:00:00+08:00"),
                ["HTTP URL"] = x => x.Replace("https://github.com", "http://github.com"),
                ["raw URL"] = x => x.Replace("github.com/QingMo-A/QingToolbox/releases/download/v1", "raw.githubusercontent.com/QingMo-A/QingToolbox/modules"),
                ["latest URL"] = x => x.Replace("download/v1", "download/latest"),
                ["URL filename"] = x => x.Replace("/QingToolbox.Test-0.1.0.qmod", "/other.qmod"),
                ["non qmod"] = x => x.Replace("QingToolbox.Test-0.1.0.qmod", "QingToolbox.Test.zip"),
                ["zero size"] = x => x.Replace("\"size\":123", "\"size\":0"),
                ["negative size"] = x => x.Replace("\"size\":123", "\"size\":-1"),
                ["bad hash"] = x => x.Replace(new string('A', 64), "BAD"),
                ["missing Chinese"] = x => x.Replace("\"zh-CN\":", "\"fr-FR\":"),
                ["missing English"] = x => x.Replace("\"en-US\":", "\"fr-FR\":")
            };
            foreach (var test in invalidCases) { WriteFixture(root, preview, updateTransform: test.Value); RequireInvalid(root, test.Key); }
            WriteFixture(root, "[]"); Directory.CreateDirectory(Path.Combine(root, "Duplicate")); File.WriteAllText(Path.Combine(root, "Duplicate", "module.json"), "{\"id\":\"qing.test\"}"); RequireInvalid(root, "duplicate moduleId");
            WriteFixture(root, "[" + Release("0.1.0-alpha.1", "preview", "0.2.0-alpha") + "," + Release("0.1.0-alpha.1", "preview", "0.2.0-alpha") + "]"); RequireInvalid(root, "duplicate release");
            WriteFixture(root, "[" + Release("0.1.0-alpha", "preview", "0.2.0-alpha") + "," + Release("1.0.0", "preview", "2.0.0") + "]"); RequireInvalid(root, "release order");
            WriteFixture(root, "[]", indexTransform: x => x.Replace("Test/update.json", "../Test/update.json")); RequireInvalid(root, "path traversal");
            WriteFixture(root, "[]", indexTransform: x => x.Replace("Test/update.json", "C:/Test/update.json")); RequireInvalid(root, "absolute path");
            WriteFixture(root, "[]", indexTransform: x => x.Replace("Test/update.json", "Test\\update.json")); RequireInvalid(root, "backslash path");
            WriteFixture(root, "[]", indexTransform: x => x.Replace("\"qing.test\":", "\"qing.extra\": {\"updateManifest\":\"Test/update.json\"},\"qing.test\":")); RequireInvalid(root, "extra module");
            WriteFixture(root, "[]", indexTransform: x => x.Replace("\"qing.test\": { \"updateManifest\": \"Test/update.json\" }", "")); RequireInvalid(root, "missing module");
            WriteFixture(root, "[]", indexTransform: x => x.Replace(
                "\"qing.test\": { \"updateManifest\": \"Test/update.json\" }",
                "\"qing.other\":{\"updateManifest\":\"Test/update.json\"},\"qing.test\": { \"updateManifest\": \"Test/update.json\" }")); RequireInvalid(root, "duplicate update path");
            Console.WriteLine("Module update metadata validator self-test passed."); return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Release(string version, string channel, string maximum) => JsonSerializer.Serialize(new
    {
        version,
        channel,
        moduleApiVersion = "experimental-0.1",
        minimumHostVersion = "0.1.0",
        maximumHostVersionExclusive = maximum,
        publishedAt = "2026-07-20T08:00:00Z",
        package = new
        {
            fileName = "QingToolbox.Test-0.1.0.qmod",
            url = "https://github.com/QingMo-A/QingToolbox/releases/download/v1/QingToolbox.Test-0.1.0.qmod",
            size = 123,
            sha256 = new string('A', 64)
        },
        releaseNotes = new Dictionary<string, string> { ["zh-CN"] = "更新", ["en-US"] = "Update" }
    });
    private static void WriteFixture(string root, string releases, Func<string, string>? indexTransform = null, Func<string, string>? updateTransform = null)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true); Directory.CreateDirectory(Path.Combine(root, "Test"));
        File.WriteAllText(Path.Combine(root, "Test", "module.json"), "{\"id\":\"qing.test\"}");
        var index = "{\"schemaVersion\":1,\"sourceId\":\"qingtoolbox-official\",\"modules\":{\"qing.test\": { \"updateManifest\": \"Test/update.json\" }}}";
        var update = "{\"schemaVersion\": 1,\"moduleId\":\"qing.test\",\"publisher\": \"QingMo-A\",\"releases\":" + releases + "}";
        File.WriteAllText(Path.Combine(root, "index.json"), indexTransform?.Invoke(index) ?? index);
        File.WriteAllText(Path.Combine(root, "Test", "update.json"), updateTransform?.Invoke(update) ?? update);
    }
    private static void WriteFourModuleFixture(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true); Directory.CreateDirectory(root);
        var modules = new[] { ("Alpha", "qing.alpha"), ("Beta", "qing.beta"), ("Gamma", "qing.gamma"), ("Omega", "qing.omega") };
        foreach (var (directory, id) in modules)
        {
            Directory.CreateDirectory(Path.Combine(root, directory));
            File.WriteAllText(Path.Combine(root, directory, "module.json"), JsonSerializer.Serialize(new { id }));
            File.WriteAllText(Path.Combine(root, directory, "update.json"), JsonSerializer.Serialize(new { schemaVersion = 1, moduleId = id, publisher = "QingMo-A", releases = Array.Empty<object>() }));
        }
        var mappings = string.Join(',', modules.Select(x => $"\"{x.Item2}\":{{\"updateManifest\":\"{x.Item1}/update.json\"}}"));
        File.WriteAllText(Path.Combine(root, "index.json"), $"{{\"schemaVersion\":1,\"sourceId\":\"qingtoolbox-official\",\"modules\":{{{mappings}}}}}");
    }
    private static void RequireValid(string root, string name) { var errors = MetadataValidator.Validate(root); if (errors.Count != 0) throw new Exception($"Self-test valid case failed ({name}): {string.Join(" | ", errors)}"); }
    private static void RequireInvalid(string root, string name) { if (MetadataValidator.Validate(root).Count == 0) throw new Exception($"Self-test invalid case was accepted: {name}"); }
}
