using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QingToolbox.Core.Updates;

internal enum ModuleIdentityFailure { None, Invalid, Reserved }

internal readonly record struct SecureDirectoryIdentity(ulong VolumeSerialNumber, string FileId)
{
    public override string ToString() => $"{VolumeSerialNumber:x16}:{FileId}";
}

internal readonly record struct SecureTreeEntry(string FullPath, string RelativePath, bool IsDirectory,
    bool IsReparsePoint, SecureDirectoryIdentity? DirectoryIdentity);

internal static class SecureModuleIdentity
{
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "CLOCK$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public static ModuleIdentityFailure Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Normalize(NormalizationForm.FormC) ||
            value != value.ToLowerInvariant() || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ') ||
            value.Any(ch => char.IsControl(ch) || ch is '/' or '\\' or ':') ||
            !value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')) return ModuleIdentityFailure.Invalid;
        if (value.StartsWith(".qing-", StringComparison.Ordinal) || Devices.Contains(value) ||
            value.Split('.').Any(Devices.Contains)) return ModuleIdentityFailure.Reserved;
        return ModuleIdentityFailure.None;
    }
}

internal static class SecureWindowsFileSystem
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ReadAttributes = 0x80;
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint Normal = 0x80;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparse = 0x00200000;
    private const int FileAttributeTagInfo = 9;
    private const int FileIdInfoClass = 18;

    public static string ValidateAbsoluteNonRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Absolute path required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Volume root forbidden.");
        return full;
    }

    public static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string SafeCombine(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new IOException("Rooted relative path rejected.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(root, path) || path.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Path escaped root.");
        return path;
    }

    public static void CreateOrdinaryDirectoryTree(string path)
    {
        var missing = new Stack<string>(); var current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            if (File.Exists(current)) throw new IOException("Directory path collides with file.");
            missing.Push(current); current = Path.GetDirectoryName(current) ?? throw new IOException("Invalid directory root.");
        }
        EnsureNoReparseAncestors(current);
        while (missing.TryPop(out var item)) { Directory.CreateDirectory(item); EnsureOrdinaryDirectory(item); }
    }

    public static void EnsureNoReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (Directory.Exists(current)) EnsureOrdinaryDirectory(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    public static void EnsureOrdinaryDirectory(string path)
    {
        using var handle = OpenDirectory(path);
        if (handle.IsInvalid) throw Win32IOException("Directory open");
        if (IsReparse(handle)) throw new IOException("Reparse directory rejected.");
    }

    public static string PhysicalDirectory(string path)
    {
        using var handle = OpenDirectory(path);
        if (handle.IsInvalid) throw Win32IOException("Directory open");
        if (IsReparse(handle)) throw new IOException("Reparse directory rejected.");
        return FinalPath(handle);
    }

    public static SecureDirectoryIdentity DirectoryIdentity(string path)
    {
        using var handle = OpenDirectory(path);
        if (handle.IsInvalid) throw Win32IOException("Directory identity open");
        if (IsReparse(handle)) throw new IOException("Reparse directory rejected.");
        return DirectoryIdentity(handle);
    }

    public static IReadOnlyList<SecureTreeEntry> WalkTreeNoFollow(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var physicalRoot = PhysicalDirectory(fullRoot);
        var rootIdentity = DirectoryIdentity(fullRoot);
        var pending = new Queue<(string Path, SecureDirectoryIdentity Identity)>();
        var result = new List<SecureTreeEntry>();
        pending.Enqueue((fullRoot, rootIdentity));
        while (pending.TryDequeue(out var current))
        {
            if (DirectoryIdentity(current.Path) != current.Identity) throw new IOException("Directory changed during traversal.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
            {
                if (!IsWithin(physicalRoot, Path.GetFullPath(entry))) throw new IOException("Tree entry escaped root.");
                var attributes = File.GetAttributes(entry);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isReparse = (attributes & FileAttributes.ReparsePoint) != 0;
                SecureDirectoryIdentity? identity = null;
                if (isDirectory && !isReparse)
                {
                    var physical = PhysicalDirectory(entry);
                    if (!IsWithin(physicalRoot, physical)) throw new IOException("Tree directory escaped root.");
                    identity = DirectoryIdentity(entry);
                    pending.Enqueue((entry, identity.Value));
                }
                result.Add(new(entry, Path.GetRelativePath(fullRoot, entry), isDirectory, isReparse, identity));
            }
            if (DirectoryIdentity(current.Path) != current.Identity) throw new IOException("Directory changed during traversal.");
        }
        if (DirectoryIdentity(fullRoot) != rootIdentity) throw new IOException("Tree root changed during traversal.");
        return result;
    }

    public static SafeFileHandle OpenStableRead(string path, string physicalRoot)
    {
        EnsureNoReparseAncestors(Path.GetDirectoryName(path)!);
        var handle = CreateFile(path, GenericRead, ShareRead, IntPtr.Zero, OpenExisting, Normal | OpenReparse, IntPtr.Zero);
        if (handle.IsInvalid) throw Win32IOException("Stable file open");
        try
        {
            if (IsReparse(handle) || !IsWithin(physicalRoot, FinalPath(handle))) throw new IOException("File escaped attested root.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    public static async Task<CrashRecoverableFileLock> AcquireLockAsync(string path,
        string expectedPhysicalLockRoot, SecureDirectoryIdentity expectedLockRootIdentity, CancellationToken token)
    {
        var parent = Path.GetDirectoryName(path)!;
        EnsureNoReparseAncestors(parent);
        if (DirectoryIdentity(parent) != expectedLockRootIdentity ||
            !PhysicalDirectory(parent).Equals(expectedPhysicalLockRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Lock root identity changed.");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var handle = CreateFile(path, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenAlways,
                Normal | OpenReparse, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                try
                {
                    if (IsReparse(handle) || !IsWithin(expectedPhysicalLockRoot, FinalPath(handle)) ||
                        DirectoryIdentity(parent) != expectedLockRootIdentity)
                        throw new IOException("Lock escaped physical root.");
                    var recovered = RandomAccess.GetLength(handle) != 0;
                    RandomAccess.SetLength(handle, 0);
                    RandomAccess.Write(handle, Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)), 0);
                    RandomAccess.FlushToDisk(handle);
                    return new(handle, recovered);
                }
                catch { handle.Dispose(); throw; }
            }
            var error = Marshal.GetLastPInvokeError(); handle.Dispose();
            if (error is ErrorSharingViolation or ErrorLockViolation) { await Task.Delay(40, token); continue; }
            if (error == ErrorAccessDenied) throw new UnauthorizedAccessException();
            throw new IOException($"Lock open failed with Win32 error {error}.");
        }
    }

    public static void WriteOwnerMarker(string directory, string name, Guid transactionId)
    {
        EnsureOrdinaryDirectory(directory);
        var path = SafeCombine(directory, name); var bytes = Encoding.UTF8.GetBytes(transactionId.ToString("D"));
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(bytes); stream.Flush(true);
        }
        catch (IOException) when (File.Exists(path))
        {
            if (!HasOwnerMarker(directory, name, transactionId)) throw;
        }
    }

    public static bool HasOwnerMarker(string directory, string name, Guid transactionId)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            var physical = PhysicalDirectory(directory); var path = SafeCombine(directory, name);
            using var handle = OpenStableRead(path, physical);
            var length = RandomAccess.GetLength(handle); if (length != 36) return false;
            Span<byte> bytes = stackalloc byte[36]; if (RandomAccess.Read(handle, bytes, 0) != 36) return false;
            return bytes[0] != 0xEF && Encoding.UTF8.GetString(bytes) == transactionId.ToString("D");
        }
        catch { return false; }
    }

    public static void DeleteOwnerMarker(string directory, string name, Guid transactionId)
    {
        if (!HasOwnerMarker(directory, name, transactionId)) throw new IOException("Owner marker rejected.");
        File.Delete(SafeCombine(directory, name));
    }

    public static void DeleteOwnedTree(string directory, string markerName, Guid transactionId, string physicalAllowedRoot)
    {
        if (!Directory.Exists(directory)) return;
        if (!HasOwnerMarker(directory, markerName, transactionId)) throw new IOException("Tree ownership rejected.");
        var physical = PhysicalDirectory(directory);
        if (!IsWithin(physicalAllowedRoot, physical)) throw new IOException("Owned tree escaped root.");
        DeleteTreeCore(directory, physical);
    }

    private static void DeleteTreeCore(string directory, string physicalRoot)
    {
        var rootIdentity = DirectoryIdentity(directory);
        var entries = WalkTreeNoFollow(directory)
            .OrderByDescending(entry => entry.RelativePath.Count(ch => ch is '/' or '\\'))
            .ThenByDescending(entry => entry.RelativePath.Length)
            .ToArray();
        foreach (var entry in entries)
        {
            if (entry.IsReparsePoint)
            {
                if (entry.IsDirectory) Directory.Delete(entry.FullPath, false); else File.Delete(entry.FullPath);
                continue;
            }
            if (entry.IsDirectory) Directory.Delete(entry.FullPath, false); else File.Delete(entry.FullPath);
        }
        if (DirectoryIdentity(directory) != rootIdentity || !IsWithin(physicalRoot, PhysicalDirectory(directory)))
            throw new IOException("Delete root identity changed.");
        Directory.Delete(directory, false);
    }

    public static bool IsReparsePath(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    public static string HashIdentity(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass,
        out FileAttributeTagInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool GetFileIdInformationByHandle(SafeFileHandle file, int infoClass,
        out FileIdInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    private static SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template) =>
        CreateFileNative(ToExtendedPath(path), access, share, security, creation, flags, template);

    // Keep Win32 handle-based validation working when CI or user profile paths exceed MAX_PATH.
    private static string ToExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\?\\", StringComparison.Ordinal)) return full;
        return full.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + full[2..]
            : "\\\\?\\" + full;
    }
    private static SafeFileHandle OpenDirectory(string path) => CreateFile(path, ReadAttributes, ShareRead | ShareWrite,
        IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparse, IntPtr.Zero);
    private static bool IsReparse(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var info,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>())) throw Win32IOException("Handle attribute query");
        return (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0;
    }
    private static SecureDirectoryIdentity DirectoryIdentity(SafeFileHandle handle)
    {
        if (!GetFileIdInformationByHandle(handle, FileIdInfoClass, out FileIdInformation info,
                (uint)Marshal.SizeOf<FileIdInformation>())) throw Win32IOException("Directory file ID query");
        return new(info.VolumeSerialNumber,
            $"{info.FileIdHigh:x16}{info.FileIdLow:x16}");
    }
    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512); var length = GetFinalPathNameByHandle(handle, buffer, 512, 0);
        if (length == 0) throw Win32IOException("Final path query");
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1)); length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) throw Win32IOException("Final path query");
        }
        var value = buffer.ToString();
        if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) value = "\\\\" + value[8..];
        else if (value.StartsWith("\\\\?\\", StringComparison.Ordinal)) value = value[4..];
        return Path.GetFullPath(value);
    }
    private static IOException Win32IOException(string action) => new($"{action} failed with Win32 error {Marshal.GetLastPInvokeError()}.");
    [StructLayout(LayoutKind.Sequential)] private struct FileAttributeTagInformation { public uint FileAttributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }
}

internal sealed class CrashRecoverableFileLock : IAsyncDisposable
{
    private readonly SafeFileHandle _handle;
    public CrashRecoverableFileLock(SafeFileHandle handle, bool recovered)
    { _handle = handle; Recovered = recovered; }
    public bool Recovered { get; }
    public SafeFileHandle Handle => _handle;
    public ValueTask DisposeAsync()
    {
        try { RandomAccess.SetLength(_handle, 0); RandomAccess.FlushToDisk(_handle); } catch { }
        _handle.Dispose(); return ValueTask.CompletedTask;
    }
}
