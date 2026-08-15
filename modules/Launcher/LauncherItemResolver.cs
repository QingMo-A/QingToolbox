using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace QingToolbox.Modules.Launcher;

public static class LauncherItemResolver
{
    public static bool TryResolve(string path, out ResolvedLauncherItem? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(path)) return false;
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        if (!File.Exists(fullPath)) return false;

        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            item = CreateExecutable(fullPath, null, null, null);
            return item is not null;
        }
        if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        return TryResolveShortcut(fullPath, out item);
    }

    public static IReadOnlyList<(string Path, ResolvedLauncherItem? Item)> ResolveMany(IEnumerable<string> paths)
    {
        var results = new List<(string, ResolvedLauncherItem?)>();
        foreach (var path in paths.Take(16))
        {
            var item = TryResolve(path, out var resolved) ? resolved : null;
            results.Add((Path.GetFileName(path), item));
        }
        return results;
    }

    private static ResolvedLauncherItem? CreateExecutable(
        string target,
        string? displayName,
        string? arguments,
        string? workingDirectory,
        string? iconSourcePath = null)
    {
        if (!File.Exists(target) || !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var directory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(target) ?? string.Empty
            : NormalizeWorkingDirectory(workingDirectory!, Path.GetDirectoryName(target));
        var name = string.IsNullOrWhiteSpace(displayName) ? ReadDisplayName(target) : displayName.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(target);
        return new(name, target, arguments ?? string.Empty, directory, iconSourcePath ?? target);
    }

    private static bool TryResolveShortcut(string shortcutPath, out ResolvedLauncherItem? item)
    {
        item = null;
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            if (shortcut is null) return false;
            var shortcutType = shortcut.GetType();
            var target = ReadProperty(shortcutType, shortcut, "TargetPath");
            if (string.IsNullOrWhiteSpace(target)) return false;
            var targetPath = Path.GetFullPath(target);
            var shortcutDirectory = Path.GetDirectoryName(shortcutPath) ?? string.Empty;
            if (!Path.IsPathRooted(targetPath)) targetPath = Path.GetFullPath(Path.Combine(shortcutDirectory, targetPath));
            var arguments = ReadProperty(shortcutType, shortcut, "Arguments") ?? string.Empty;
            var workingDirectory = ReadProperty(shortcutType, shortcut, "WorkingDirectory");
            var iconLocation = ReadProperty(shortcutType, shortcut, "IconLocation");
            var iconPath = ParseIconPath(iconLocation, shortcutDirectory);
            var displayName = Path.GetFileNameWithoutExtension(shortcutPath);
            item = CreateExecutable(targetPath, displayName, arguments,
                string.IsNullOrWhiteSpace(workingDirectory) ? shortcutDirectory : workingDirectory,
                iconPath);
            return item is not null;
        }
        catch (COMException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string? ReadProperty(Type type, object instance, string name) =>
        type.InvokeMember(name, BindingFlags.GetProperty, null, instance, null) as string;

    private static string? ParseIconPath(string? iconLocation, string shortcutDirectory)
    {
        if (string.IsNullOrWhiteSpace(iconLocation)) return null;
        var comma = iconLocation.LastIndexOf(',');
        var path = (comma >= 0 ? iconLocation[..comma] : iconLocation).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!Path.IsPathRooted(path)) path = Path.GetFullPath(Path.Combine(shortcutDirectory, path));
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static string NormalizeWorkingDirectory(string value, string? fallback)
    {
        try
        {
            var path = Path.IsPathRooted(value) ? value : Path.Combine(fallback ?? string.Empty, value);
            return Path.GetFullPath(path);
        }
        catch (ArgumentException) { return fallback ?? string.Empty; }
        catch (NotSupportedException) { return fallback ?? string.Empty; }
    }

    private static string ReadDisplayName(string target)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(target);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
        }
        catch (FileNotFoundException) { }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return Path.GetFileNameWithoutExtension(target);
    }
}

internal static class LauncherIconCache
{
    private const int CacheSize = 256;

    public static bool IsCurrentKey(string? iconKey) =>
        !string.IsNullOrWhiteSpace(iconKey) && iconKey.EndsWith("-v2.png", StringComparison.OrdinalIgnoreCase);

    public static string? TryCache(string? sourcePath, string iconsDirectory, string itemId)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
        var destination = Path.Combine(iconsDirectory, itemId + "-v2.png");
        SafeIconHandle? handle = null;
        try
        {
            Directory.CreateDirectory(iconsDirectory);
            var handles = new[] { IntPtr.Zero };
            var iconIds = new uint[1];
            if (PrivateExtractIcons(sourcePath!, 0, CacheSize, CacheSize, handles, iconIds, 1, 0) == 0 || handles[0] == IntPtr.Zero)
                return null;
            handle = new SafeIconHandle(handles[0]);
            using var icon = System.Drawing.Icon.FromHandle(handle.DangerousGetHandle());
            using var bitmap = icon.ToBitmap();
            bitmap.Save(destination, System.Drawing.Imaging.ImageFormat.Png);
            return itemId + "-v2.png";
        }
        catch (ArgumentException) { return null; }
        catch (ExternalException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally { handle?.Dispose(); }
    }

    public static void DeleteBestEffort(string? iconKey, string iconsDirectory)
    {
        if (string.IsNullOrWhiteSpace(iconKey) || Path.GetFileName(iconKey) != iconKey) return;
        try { File.Delete(Path.Combine(iconsDirectory, iconKey)); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(string file, int index, int cxIcon, int cyIcon,
        IntPtr[] icons, uint[] iconIds, uint count, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private sealed class SafeIconHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeIconHandle() : this(IntPtr.Zero) { }
        public SafeIconHandle(IntPtr value) : this(value, true) { }
        private SafeIconHandle(IntPtr value, bool ownsHandle) : base(ownsHandle) => SetHandle(value);
        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }
}
