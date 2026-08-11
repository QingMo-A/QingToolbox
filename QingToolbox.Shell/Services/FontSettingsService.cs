using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using QingToolbox.Core.Settings;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.Services;

/// <summary>
/// Owns the small, environment-scoped font catalog used by the native shell and
/// WebUI.  Only catalog ids cross the bridge; imported files remain under the
/// host-controlled LocalRoot and are addressed by a content hash.
/// </summary>
public sealed class FontSettingsService(
    ApplicationPaths paths,
    UserSettingsService settingsService,
    Func<IReadOnlyList<string>>? systemFontEnumerator = null)
{
    public const long MaximumFontBytes = 32L * 1024 * 1024;
    public const string ResourcePathPrefix = "/user-fonts/";
    public const string ResourceUrlPrefix = "https://app.qingtoolbox.local/user-fonts/";
    public const string WebFontFamilyAlias = "QingToolbox User Font";
    private const int SystemFontCacheSchemaVersion = 1;
    private const long MaximumSystemFontCacheBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ttf", ".otf", ".ttc" };
    private readonly object _sync = new();
    private readonly Func<IReadOnlyList<string>> _systemFontEnumerator =
        systemFontEnumerator ?? EnumerateSystemFontFamilies;
    private IReadOnlyList<string>? _systemFontFamilies;
    private bool _systemFontCatalogInitialized;
    private bool _systemFontRefreshBusy;
    private FontCatalogItem _current = FontCatalogItem.Default;
    private FontFamily _currentFontFamily = new("Segoe UI Variable");
    public event EventHandler? Changed;

    /// <summary>True while an explicit user-requested system font refresh is running.</summary>
    public bool IsRefreshingSystemFonts
    {
        get { lock (_sync) return _systemFontRefreshBusy; }
    }

    public FontCatalogItem Current
    {
        get { lock (_sync) return _current; }
    }

    public FontFamily CurrentFontFamily
    {
        get { lock (_sync) return _currentFontFamily; }
    }

    public async Task InitializeAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var catalog = BuildCatalog();
        var selected = Resolve(settings.FontId, settings.FontSource, settings.FontFamilyName, catalog);
        var shouldNormalize = !string.Equals(settings.FontId, selected.Id, StringComparison.Ordinal) ||
            !string.Equals(FontPreferenceSources.Normalize(settings.FontSource), selected.Source, StringComparison.Ordinal) ||
            !string.Equals(FontPreferenceIds.NormalizeFamilyName(settings.FontFamilyName), selected.FamilyName, StringComparison.Ordinal);
        Apply(selected);
        if (shouldNormalize)
        {
            try
            {
                await settingsService.UpdateAsync(current => ApplyToSettings(current, selected), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A broken settings directory should not prevent the shell from starting;
                // the in-memory selection is still safe and already fell back if needed.
            }
        }
    }

    public FontCatalogSnapshot CreateSnapshot() =>
        new(Current, BuildCatalog());

    /// <summary>
    /// Re-enumerates installed system fonts only when explicitly requested by the
    /// user. The imported-font catalog is intentionally left untouched.
    /// </summary>
    public async Task<FontCatalogSnapshot> RefreshSystemFontsAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_systemFontRefreshBusy)
                return CreateSnapshot();
            _systemFontRefreshBusy = true;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var families = await Task.Run(_systemFontEnumerator, cancellationToken).ConfigureAwait(false);
            var normalized = NormalizeSystemFamilies(families);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _systemFontFamilies = normalized;
                _systemFontCatalogInitialized = true;
                TryWriteSystemFontCache(normalized);
            }
            return new(Current, BuildCatalog());
        }
        finally
        {
            lock (_sync) _systemFontRefreshBusy = false;
        }
    }

    public async Task<FontSelectionResult> SetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256)
            return FontSelectionResult.Invalid;

        var catalog = BuildCatalog();
        var selected = catalog.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (selected is null) return FontSelectionResult.Invalid;
        try
        {
            await settingsService.UpdateAsync(settings => ApplyToSettings(settings, selected), cancellationToken)
                .ConfigureAwait(false);
            Apply(selected);
            return new(FontSelectionDisposition.Succeeded, selected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return FontSelectionResult.Failed; }
    }

    public async Task<FontImportResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? sourcePath;
        try { sourcePath = PickFontFile(); }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or
            IOException or SecurityException)
        { return FontImportResult.Failed; }
        if (sourcePath is null) return FontImportResult.Cancelled;

        return await ImportFromPathAsync(sourcePath, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FontImportResult> ImportFromPathAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        FontCatalogItem imported;
        try { imported = await ImportFileAsync(sourcePath, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or NotSupportedException or SecurityException)
        { return FontImportResult.Invalid; }

        try
        {
            await settingsService.UpdateAsync(settings => ApplyToSettings(settings, imported), cancellationToken)
                .ConfigureAwait(false);
            Apply(imported);
            return new(FontImportDisposition.Imported, imported);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return FontImportResult.Failed; }
    }

    /// <summary>Resolves the fixed same-origin Web route without accepting arbitrary paths.</summary>
    public bool TryOpenWebResource(string requestPath, out Stream? content, out string contentType)
    {
        content = null;
        contentType = "application/octet-stream";
        try
        {
            var normalized = Uri.UnescapeDataString(requestPath ?? string.Empty);
            if (!normalized.StartsWith(ResourcePathPrefix, StringComparison.Ordinal) ||
                normalized.Length <= ResourcePathPrefix.Length)
                return false;
            var routeName = normalized[ResourcePathPrefix.Length..];
            if (routeName.Contains('/') || routeName.Contains('\\')) return false;
            var extension = Path.GetExtension(routeName).ToLowerInvariant();
            if (!SupportedExtensions.Contains(extension)) return false;
            var hash = routeName[..^extension.Length];
            if (!FontPreferenceIds.IsSha256(hash)) return false;
            var candidate = Path.Combine(paths.ImportedFontsDirectory, $"font-{hash.ToLowerInvariant()}{extension}");
            if (!IsSafeImportedFile(candidate)) return false;
            var bytes = ReadAndVerifyFont(candidate, hash, MaximumFontBytes);
            contentType = extension switch
            {
                ".ttf" => "font/ttf",
                ".otf" => "font/otf",
                ".ttc" => "font/collection",
                _ => "application/octet-stream"
            };
            content = new MemoryStream(bytes, writable: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or SecurityException)
        { content?.Dispose(); content = null; return false; }
    }

    private async Task<FontCatalogItem> ImportFileAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        EnsureOrdinaryPath(source, expectDirectory: false);
        if (!SupportedExtensions.Contains(Path.GetExtension(source)))
            throw new InvalidDataException("Unsupported font file type.");
        var sourceInfo = new FileInfo(source);
        if (sourceInfo.Length <= 0 || sourceInfo.Length > MaximumFontBytes)
            throw new InvalidDataException("Font file size is outside the supported limit.");

        EnsureSafeImportedDirectory();
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var temporary = Path.Combine(paths.ImportedFontsDirectory, $".font-{Guid.NewGuid():N}.tmp");
        string hash;
        var moved = false;
        var createdDestination = false;
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaximumFontBytes) throw new InvalidDataException("Font file is too large.");
                digest.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            // Windows cannot atomically move the managed copy while the exclusive
            // output handle is still open. Dispose both file handles before the
            // destination existence check and move; the using declarations safely
            // tolerate their later idempotent disposal.
            output.Dispose();
            input.Dispose();
            var destination = Path.Combine(paths.ImportedFontsDirectory, $"font-{hash}{extension}");
            EnsureSafeImportedDirectory();
            if (File.Exists(destination))
            {
                EnsureOrdinaryPath(destination, expectDirectory: false);
                var existing = ReadAndVerifyFont(destination, hash, MaximumFontBytes);
                if (existing.Length == 0) throw new InvalidDataException("Imported font is empty.");
                File.Delete(temporary);
            }
            else
            {
                File.Move(temporary, destination, overwrite: false);
                moved = true;
                createdDestination = true;
            }

            var familyName = ReadFamilyName(destination);
            if (familyName is null)
            {
                // Do not retain bytes that WPF cannot parse as a font.
                if (createdDestination && File.Exists(destination)) File.Delete(destination);
                throw new InvalidDataException("The selected file is not a readable font.");
            }
            return new(FontPreferenceIds.Imported(hash), FontPreferenceSources.Imported,
                familyName, familyName, ResourceUrl(hash, extension), extension, destination);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
        finally
        {
            if (!moved)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }

    private IReadOnlyList<FontCatalogItem> BuildCatalog()
    {
        var options = new List<FontCatalogItem> { FontCatalogItem.Default };
        foreach (var name in GetSystemFontFamilies())
        {
            var id = FontPreferenceIds.System(name);
            if (options.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal))) continue;
            options.Add(new(id, FontPreferenceSources.System, name, name, null, null));
        }

        try
        {
            EnsureSafeImportedDirectory();
            foreach (var file in Directory.EnumerateFiles(paths.ImportedFontsDirectory))
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                var name = Path.GetFileNameWithoutExtension(file);
                if (!SupportedExtensions.Contains(extension) || !name.StartsWith("font-", StringComparison.OrdinalIgnoreCase)) continue;
                var hash = name[5..];
                if (!FontPreferenceIds.IsSha256(hash) || !IsSafeImportedFile(file)) continue;
                try
                {
                    var bytes = ReadAndVerifyFont(file, hash, MaximumFontBytes);
                    if (bytes.Length == 0) continue;
                    var family = ReadFamilyName(file);
                    if (family is null) continue;
                    options.Add(new(FontPreferenceIds.Imported(hash), FontPreferenceSources.Imported,
                        family, family, ResourceUrl(hash, extension), extension, Path.GetFullPath(file)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or ArgumentException or NotSupportedException or SecurityException)
                { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or SecurityException)
        { }
        return new ReadOnlyCollection<FontCatalogItem>(options
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList());
    }

    private IReadOnlyList<string> GetSystemFontFamilies()
    {
        lock (_sync)
        {
            if (_systemFontCatalogInitialized)
                return _systemFontFamilies ?? Array.Empty<string>();

            // A cache hit is the normal path for every process after the first.
            // Missing or invalid data is safe: enumerate once and replace it with
            // a validated versioned document. No exceptions escape into the UI.
            var cached = TryReadSystemFontCache();
            var families = cached ?? NormalizeSystemFamilies(SafelyEnumerateSystemFonts());
            _systemFontFamilies = families;
            _systemFontCatalogInitialized = true;
            if (cached is null) TryWriteSystemFontCache(families);
            return families;
        }
    }

    private IReadOnlyList<string> SafelyEnumerateSystemFonts()
    {
        try { return _systemFontEnumerator() ?? Array.Empty<string>(); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
            ArgumentException or NotSupportedException)
        { return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> EnumerateSystemFontFamilies() =>
        Fonts.SystemFontFamilies
            .Select(family => family.Source)
            .ToArray();

    private static IReadOnlyList<string> NormalizeSystemFamilies(IEnumerable<string>? families)
    {
        if (families is null) return Array.Empty<string>();
        return families
            .Select(FontPreferenceIds.NormalizeFamilyName)
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<string>? TryReadSystemFontCache()
    {
        try
        {
            var path = paths.FontCatalogCachePath;
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumSystemFontCacheBytes) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.SequentialScan);
            var document = JsonSerializer.Deserialize<SystemFontCacheDocument>(stream, CacheJsonOptions);
            if (document is null || document.SchemaVersion != SystemFontCacheSchemaVersion ||
                document.Families is null) return null;
            var normalized = NormalizeSystemFamilies(document.Families);
            // Reject malformed entries instead of silently turning a corrupt cache
            // into a seemingly valid but incomplete catalog.
            if (normalized.Count != document.Families.Length) return null;
            return normalized;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or NotSupportedException or ArgumentException or SecurityException)
        { return null; }
    }

    private void TryWriteSystemFontCache(IReadOnlyList<string> families)
    {
        var temporary = string.Empty;
        try
        {
            var directory = paths.FontCacheDirectory;
            Directory.CreateDirectory(directory);
            temporary = paths.FontCatalogCachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            var document = new SystemFontCacheDocument(SystemFontCacheSchemaVersion, families.ToArray());
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, document, CacheJsonOptions);
                stream.Flush(flushToDisk: true);
            }

            // Replace/Move keeps readers from observing a partially written JSON
            // document. The fallback is needed on filesystems without Replace.
            if (File.Exists(paths.FontCatalogCachePath))
            {
                try { File.Replace(temporary, paths.FontCatalogCachePath, null); }
                catch (PlatformNotSupportedException) { File.Move(temporary, paths.FontCatalogCachePath, true); }
            }
            else File.Move(temporary, paths.FontCatalogCachePath);
            temporary = string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            SecurityException or ArgumentException or NotSupportedException)
        { /* A read-only cache is harmless; keep the in-memory catalog. */ }
        finally
        {
            if (temporary.Length > 0)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    private sealed record SystemFontCacheDocument(int SchemaVersion, string[] Families);

    private FontCatalogItem Resolve(string? id, string? source, string? familyName, IReadOnlyList<FontCatalogItem> catalog)
    {
        var normalizedSource = FontPreferenceSources.Normalize(source);
        var normalizedId = FontPreferenceIds.Normalize(id, normalizedSource);
        var match = catalog.FirstOrDefault(item => string.Equals(item.Id, normalizedId, StringComparison.Ordinal));
        return match ?? FontCatalogItem.Default;
    }

    private static void ApplyToSettings(UserSettings settings, FontCatalogItem selected)
    {
        settings.FontId = selected.Id;
        settings.FontSource = selected.Source;
        settings.FontFamilyName = selected.FamilyName;
    }

    private void Apply(FontCatalogItem selected)
    {
        var family = ToWpfFamily(selected) ?? new FontFamily("Segoe UI Variable");
        lock (_sync) { _current = selected; _currentFontFamily = family; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static FontFamily? ToWpfFamily(FontCatalogItem selected)
    {
        try
        {
            if (selected.Source == FontPreferenceSources.System && selected.FamilyName is not null)
                return new FontFamily(selected.FamilyName);
            if (selected.Source == FontPreferenceSources.Imported && selected.FilePath is not null && selected.FamilyName is not null)
            {
                var directory = Path.GetDirectoryName(selected.FilePath);
                if (directory is null) return null;
                var baseUri = new Uri(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, UriKind.Absolute);
                return new FontFamily(baseUri, $"./#{selected.FamilyName}");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or FileFormatException)
        { }
        return selected.Id == FontPreferenceIds.Default ? new FontFamily("Segoe UI Variable") : null;
    }

    private string? ReadFamilyName(string path)
    {
        try
        {
            EnsureOrdinaryPath(path, expectDirectory: false);
            var glyph = new GlyphTypeface(new Uri(path, UriKind.Absolute));
            var preferred = glyph.FamilyNames.FirstOrDefault(pair =>
                string.Equals(pair.Key.IetfLanguageTag, "en-US", StringComparison.OrdinalIgnoreCase));
            return FontPreferenceIds.NormalizeFamilyName(preferred.Value ?? glyph.FamilyNames.Values.FirstOrDefault());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or FileFormatException or SecurityException)
        { return null; }
    }

    private static byte[] ReadAndVerifyFont(string path, string expectedHash, long maximumBytes)
    {
        EnsureOrdinaryPath(path, expectDirectory: false);
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > maximumBytes) throw new InvalidDataException("Font size is invalid.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var bytes = new byte[checked((int)info.Length)];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count <= 0) throw new EndOfStreamException();
            read += count;
        }
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Imported font hash does not match its catalog id.");
        EnsureOrdinaryPath(path, expectDirectory: false);
        return bytes;
    }

    private bool IsSafeImportedFile(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.ImportedFontsDirectory));
        var prefix = root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(full) && File.Exists(full) && IsOrdinaryTree(full, root);
    }

    private void EnsureSafeImportedDirectory()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.ImportedFontsDirectory));
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.LocalRoot));
        var prefix = boundary + Path.DirectorySeparatorChar;
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Font directory escaped its environment boundary.");
        var pending = new Stack<DirectoryInfo>();
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop().FullName;
            if (File.Exists(current)) throw new IOException("Font directory segment is a file.");
            if (!Directory.Exists(current)) Directory.CreateDirectory(current);
            EnsureOrdinaryPath(current, expectDirectory: true);
            if (string.Equals(Path.TrimEndingDirectorySeparator(current), root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static bool IsOrdinaryTree(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var current = new FileInfo(path).Directory; current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void EnsureOrdinaryPath(string path, bool expectDirectory)
    {
        if (expectDirectory ? !Directory.Exists(path) : !File.Exists(path)) throw new IOException("Font path is unavailable.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Font path cannot be a reparse point.");
        if (!expectDirectory && !IsOrdinaryTree(path, Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty))
            throw new IOException("Font path contains a reparse point.");
    }

    private static string? PickFontFile()
    {
        string? selected = null;
        void Show()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import font",
                Filter = "Font files (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc",
                CheckFileExists = true,
                Multiselect = false,
                ValidateNames = true
            };
            if (dialog.ShowDialog() == true) selected = dialog.FileName;
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.Invoke(Show);
        else Show();
        return selected;
    }

    private static string ResourceUrl(string hash, string extension) =>
        ResourceUrlPrefix + hash.ToLowerInvariant() + extension.ToLowerInvariant();

    public sealed record FontCatalogSnapshot(FontCatalogItem Current, IReadOnlyList<FontCatalogItem> Options);
    public sealed record FontCatalogItem(string Id, string Source, string DisplayName, string? FamilyName,
        string? ResourceUrl, string? Extension, string? FilePath = null)
    {
        public static FontCatalogItem Default { get; } = new(FontPreferenceIds.Default, FontPreferenceSources.Default,
            "Default", null, null, null);
    }

    public enum FontSelectionDisposition { Succeeded, Invalid, Failed }
    public sealed record FontSelectionResult(FontSelectionDisposition Disposition, FontCatalogItem? Selection = null)
    {
        public static FontSelectionResult Invalid { get; } = new(FontSelectionDisposition.Invalid);
        public static FontSelectionResult Failed { get; } = new(FontSelectionDisposition.Failed);
    }

    public enum FontImportDisposition { Imported, Cancelled, Invalid, Failed }
    public sealed record FontImportResult(FontImportDisposition Disposition, FontCatalogItem? Selection = null)
    {
        public static FontImportResult Cancelled { get; } = new(FontImportDisposition.Cancelled);
        public static FontImportResult Invalid { get; } = new(FontImportDisposition.Invalid);
        public static FontImportResult Failed { get; } = new(FontImportDisposition.Failed);
    }
}
