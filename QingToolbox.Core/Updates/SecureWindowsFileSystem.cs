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
internal sealed record SecureTreeSnapshotEntry(string FullPath, string RelativePath, bool IsDirectory,
    bool IsReparsePoint, SecureDirectoryIdentity ObjectIdentity, long Length, string Sha256);
internal sealed record SecureTreeSnapshot(SecureDirectoryIdentity RootIdentity,
    IReadOnlyList<SecureTreeSnapshotEntry> Entries);

internal enum SecureDirectoryRenameStage
{
    DestinationParentHandleAttested,
    SourceHandleAttested,
    BeforeHandleRename,
    AfterHandleRename
}

internal sealed record SecureTreeLeaseLimits(int MaximumFiles = 2048, int MaximumDirectories = 2048,
    int MaximumCapturedFileBytes = 64 * 1024)
{
    public void Validate()
    {
        if (MaximumFiles <= 0 || MaximumDirectories <= 0 || MaximumCapturedFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(SecureTreeLeaseLimits));
    }
}

internal static class SecureModuleIdentity
{
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
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

    internal static bool IsDeviceName(string value) => Devices.Contains(value);
}

internal static class SecureWindowsFileSystem
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x80;
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint CreateNew = 1;
    private const uint Normal = 0x80;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparse = 0x00200000;
    private const int FileAttributeTagInfo = 9;
    private const int FileIdInfoClass = 18;
    private const int FileRenameInformationClass = 10;
    private const int DefaultTreeMaximumFiles = 2048;
    private const int DefaultTreeMaximumDirectories = 2048;

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

    public static SecureDirectoryLease AcquireDirectoryLease(string path, string expectedPhysicalRoot,
        SecureDirectoryIdentity expectedIdentity)
    {
        var handle = CreateFile(path, GenericRead | ReadAttributes, ShareRead | ShareWrite,
            IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparse, IntPtr.Zero);
        if (handle.IsInvalid) throw Win32IOException("Directory lease open");
        try
        {
            if (IsReparse(handle) || DirectoryIdentity(handle) != expectedIdentity ||
                !IsWithin(expectedPhysicalRoot, FinalPath(handle))) throw new IOException("Directory lease rejected.");
            return new(handle, expectedPhysicalRoot, expectedIdentity);
        }
        catch { handle.Dispose(); throw; }
    }

    public static void RenameDirectoryByIdentity(string sourcePath, SecureDirectoryIdentity expectedSourceIdentity,
        string destinationPath, string expectedPhysicalAllowedRoot,
        SecureDirectoryIdentity expectedDestinationParentIdentity, bool overwrite = false,
        Action<SecureDirectoryRenameStage>? testHook = null)
    {
        if (overwrite) throw new NotSupportedException("Owned directory rename never overwrites destinations.");
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        var destinationParent = Path.GetDirectoryName(destination) ?? throw new IOException("Rename destination has no parent.");
        var destinationLeaf = ValidateSafeLeafName(Path.GetFileName(destination));
        using var parentLease = AcquireDirectoryLease(destinationParent, expectedPhysicalAllowedRoot,
            expectedDestinationParentIdentity);
        testHook?.Invoke(SecureDirectoryRenameStage.DestinationParentHandleAttested);
        using var sourceHandle = CreateFile(source, DeleteAccess | ReadAttributes, ShareRead | ShareWrite,
            IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparse, IntPtr.Zero);
        if (sourceHandle.IsInvalid) throw Win32IOException("Rename source open");
        if (IsReparse(sourceHandle) || DirectoryIdentity(sourceHandle) != expectedSourceIdentity ||
            !IsWithin(expectedPhysicalAllowedRoot, FinalPath(sourceHandle)))
            throw new IOException("Rename source identity rejected.");
        testHook?.Invoke(SecureDirectoryRenameStage.SourceHandleAttested);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Rename destination exists.");
        parentLease.Verify();
        testHook?.Invoke(SecureDirectoryRenameStage.BeforeHandleRename);
        SetRenameInformation(sourceHandle, parentLease, destinationLeaf, replaceIfExists: false);
        testHook?.Invoke(SecureDirectoryRenameStage.AfterHandleRename);
        parentLease.Verify();
        if (DirectoryIdentity(sourceHandle) != expectedSourceIdentity ||
            !Path.GetFileName(FinalPath(sourceHandle)).Equals(destinationLeaf, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Handle rename postcondition failed.");
    }

    public static void RenameLeasedDirectory(SecureTreeLease sourceLease, string sourcePath, string destinationPath,
        string expectedPhysicalAllowedRoot, SecureDirectoryIdentity expectedDestinationParentIdentity,
        Action<SecureDirectoryRenameStage>? testHook = null)
    {
        ArgumentNullException.ThrowIfNull(sourceLease);
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        var destinationParent = Path.GetDirectoryName(destination) ?? throw new IOException("Rename destination has no parent.");
        var destinationLeaf = ValidateSafeLeafName(Path.GetFileName(destination));
        using var parentLease = AcquireDirectoryLease(destinationParent, expectedPhysicalAllowedRoot,
            expectedDestinationParentIdentity);
        testHook?.Invoke(SecureDirectoryRenameStage.DestinationParentHandleAttested);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Rename destination exists.");
        sourceLease.UseRootHandle(sourceHandle =>
        {
            testHook?.Invoke(SecureDirectoryRenameStage.SourceHandleAttested);
            parentLease.Verify();
            sourceLease.PrepareForRename(source);
            testHook?.Invoke(SecureDirectoryRenameStage.BeforeHandleRename);
            SetRenameInformation(sourceHandle, parentLease, destinationLeaf, replaceIfExists: false);
            testHook?.Invoke(SecureDirectoryRenameStage.AfterHandleRename);
        });
        parentLease.Verify();
        sourceLease.VerifyAtPath(destination);
    }

    public static void RenameFileByIdentity(string sourcePath, SecureDirectoryIdentity expectedSourceIdentity,
        SecureDirectoryLease destinationParentLease, string destinationLeaf, string expectedPhysicalAllowedRoot,
        bool replaceIfExists, Action<SecureDirectoryRenameStage>? testHook = null)
    {
        ArgumentNullException.ThrowIfNull(destinationParentLease);
        var source = Path.GetFullPath(sourcePath);
        var leaf = ValidateSafeLeafName(destinationLeaf);
        destinationParentLease.Verify();
        testHook?.Invoke(SecureDirectoryRenameStage.DestinationParentHandleAttested);
        using var sourceHandle = CreateFile(source, DeleteAccess | ReadAttributes, ShareRead | ShareWrite,
            IntPtr.Zero, OpenExisting, Normal | OpenReparse, IntPtr.Zero);
        if (sourceHandle.IsInvalid) throw Win32IOException("Rename source file open");
        if (IsReparse(sourceHandle) || DirectoryIdentity(sourceHandle) != expectedSourceIdentity ||
            !IsWithin(expectedPhysicalAllowedRoot, FinalPath(sourceHandle)))
            throw new IOException("Rename source file identity rejected.");
        testHook?.Invoke(SecureDirectoryRenameStage.SourceHandleAttested);
        destinationParentLease.Verify();
        testHook?.Invoke(SecureDirectoryRenameStage.BeforeHandleRename);
        SetRenameInformation(sourceHandle, destinationParentLease, leaf, replaceIfExists);
        testHook?.Invoke(SecureDirectoryRenameStage.AfterHandleRename);
        destinationParentLease.Verify();
        if (DirectoryIdentity(sourceHandle) != expectedSourceIdentity ||
            !Path.GetFileName(FinalPath(sourceHandle)).Equals(leaf, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Handle file rename postcondition failed.");
    }

    public static void WriteAndRenameFile(string tempPath, byte[] bytes,
        SecureDirectoryLease destinationParentLease, string destinationLeaf,
        string expectedPhysicalAllowedRoot, bool replaceIfExists, Action? tempWritten = null,
        Action<SecureDirectoryRenameStage>? renameHook = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(destinationParentLease);
        var temp = Path.GetFullPath(tempPath);
        var leaf = ValidateSafeLeafName(destinationLeaf);
        destinationParentLease.Verify();
        using var handle = CreateFile(temp, GenericRead | GenericWrite | DeleteAccess, ShareRead,
            IntPtr.Zero, CreateNew, Normal | OpenReparse, IntPtr.Zero);
        if (handle.IsInvalid) throw Win32IOException("Journal temp create");
        var renamed = false;
        try
        {
            if (IsReparse(handle) || !IsWithin(expectedPhysicalAllowedRoot, FinalPath(handle)))
                throw new IOException("Journal temp escaped namespace.");
            var identity = DirectoryIdentity(handle);
            RandomAccess.Write(handle, bytes, 0);
            RandomAccess.FlushToDisk(handle);
            if (RandomAccess.GetLength(handle) != bytes.LongLength)
                throw new IOException("Journal temp length changed.");
            var verified = new byte[bytes.Length];
            if (RandomAccess.Read(handle, verified, 0) != verified.Length ||
                !CryptographicOperations.FixedTimeEquals(bytes, verified))
                throw new IOException("Journal temp content changed.");
            tempWritten?.Invoke();
            destinationParentLease.Verify();
            renameHook?.Invoke(SecureDirectoryRenameStage.DestinationParentHandleAttested);
            renameHook?.Invoke(SecureDirectoryRenameStage.SourceHandleAttested);
            renameHook?.Invoke(SecureDirectoryRenameStage.BeforeHandleRename);
            SetRenameInformation(handle, destinationParentLease, leaf, replaceIfExists);
            renamed = true;
            renameHook?.Invoke(SecureDirectoryRenameStage.AfterHandleRename);
            destinationParentLease.Verify();
            if (DirectoryIdentity(handle) != identity ||
                !Path.GetFileName(FinalPath(handle)).Equals(leaf, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Journal handle rename postcondition failed.");
        }
        finally
        {
            if (!renamed) TryDeleteByHandle(handle);
        }
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

    public static SecureTreeSnapshot CaptureStableTreeSnapshot(string root, Action? betweenPasses = null)
    {
        using var lease = AcquireStableTreeLease(root, betweenPasses);
        return lease.Snapshot;
    }

    public static SecureTreeLease AcquireStableTreeLease(string root, Action? betweenPasses = null,
        Action? afterSecondPass = null, SecureTreeLeaseLimits? limits = null)
    {
        limits ??= new(DefaultTreeMaximumFiles, DefaultTreeMaximumDirectories);
        limits.Validate();
        using var first = AcquireTreeLeaseOnce(root, limits, rootRenameAccess: false);
        betweenPasses?.Invoke();
        var second = AcquireTreeLeaseOnce(root, limits, rootRenameAccess: true);
        try
        {
            if (!TreeSnapshotsEquivalent(first.Snapshot, second.Snapshot))
                throw new IOException("Tree changed during stable snapshot.");
            afterSecondPass?.Invoke();
            second.VerifyAtPath(root);
            return second;
        }
        catch { second.Dispose(); throw; }
    }

    internal static SecureTreeLease AcquireTreeLeaseOnce(string root, SecureTreeLeaseLimits limits,
        bool rootRenameAccess)
    {
        var fullRoot = Path.GetFullPath(root);
        var physicalRoot = PhysicalDirectory(fullRoot);
        var handles = new List<SecureTreeLeasedHandle>();
        SafeFileHandle? rootHandle = null;
        try
        {
            rootHandle = CreateFile(fullRoot, (rootRenameAccess ? DeleteAccess : 0) | ReadAttributes,
                ShareRead | ShareWrite,
                IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparse, IntPtr.Zero);
            if (rootHandle.IsInvalid) throw Win32IOException("Tree root lease open");
            if (IsReparse(rootHandle) || !IsWithin(physicalRoot, FinalPath(rootHandle)))
                throw new IOException("Tree root lease rejected.");
            var rootIdentity = DirectoryIdentity(rootHandle);
            var pending = new Queue<string>();
            var entries = new List<SecureTreeSnapshotEntry>();
            var captured = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var files = 0;
            var directories = 0;
            pending.Enqueue(fullRoot);
            while (pending.TryDequeue(out var current))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(current)
                             .OrderBy(item => Path.GetFileName(item), StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(fullRoot, path).Replace('\\', '/');
                    var attributes = File.GetAttributes(path);
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var isReparse = (attributes & FileAttributes.ReparsePoint) != 0;
                    if (isReparse)
                    {
                        entries.Add(new(path, relative, isDirectory, true, default, 0, string.Empty));
                        continue;
                    }
                    if (isDirectory)
                    {
                        if (++directories > limits.MaximumDirectories)
                            throw new IOException("Tree directory handle limit exceeded.");
                        var handle = CreateFile(path, ReadAttributes, ShareRead | ShareWrite,
                            IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparse, IntPtr.Zero);
                        if (handle.IsInvalid) throw Win32IOException("Tree directory lease open");
                        try
                        {
                            if (IsReparse(handle) || !IsWithin(physicalRoot, FinalPath(handle)))
                                throw new IOException("Tree directory lease escaped root.");
                            var identity = DirectoryIdentity(handle);
                            handles.Add(new(relative, true, handle, identity, 0, string.Empty));
                            entries.Add(new(path, relative, true, false, identity, 0, string.Empty));
                            pending.Enqueue(path);
                        }
                        catch { handle.Dispose(); throw; }
                        continue;
                    }

                    if (++files > limits.MaximumFiles) throw new IOException("Tree file handle limit exceeded.");
                    var fileHandle = CreateFile(path, GenericRead, ShareRead, IntPtr.Zero, OpenExisting,
                        Normal | OpenReparse, IntPtr.Zero);
                    if (fileHandle.IsInvalid) throw Win32IOException("Tree file lease open");
                    try
                    {
                        if (IsReparse(fileHandle) || !IsWithin(physicalRoot, FinalPath(fileHandle)))
                            throw new IOException("Tree file lease escaped root.");
                        var identity = DirectoryIdentity(fileHandle);
                        var capture = relative is "module.json" or ".qing-transaction-owner";
                        var (length, hash, bytes) = ReadStableFile(fileHandle, capture,
                            limits.MaximumCapturedFileBytes);
                        handles.Add(new(relative, false, fileHandle, identity, length, hash));
                        entries.Add(new(path, relative, false, false, identity, length, hash));
                        if (bytes is not null) captured.Add(relative, bytes);
                    }
                    catch { fileHandle.Dispose(); throw; }
                }
            }
            var snapshot = new SecureTreeSnapshot(rootIdentity,
                entries.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray());
            return new(rootHandle, fullRoot, snapshot, handles, captured, limits);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            rootHandle?.Dispose();
            throw;
        }
    }

    private static (long Length, string Sha256, byte[]? CapturedBytes) ReadStableFile(
        SafeFileHandle handle, bool capture, int maximumCapturedBytes)
    {
        var length = RandomAccess.GetLength(handle);
        if (length < 0 || capture && length > maximumCapturedBytes)
            throw new IOException("Captured tree file exceeds the safe limit.");
        byte[]? captured = capture ? new byte[checked((int)length)] : null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            var requested = (int)Math.Min(buffer.Length, length - offset);
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
            if (read <= 0) throw new IOException("Tree file ended during lease acquisition.");
            hash.AppendData(buffer, 0, read);
            if (captured is not null) Buffer.BlockCopy(buffer, 0, captured, checked((int)offset), read);
            offset += read;
        }
        if (RandomAccess.GetLength(handle) != length) throw new IOException("Tree file changed during lease acquisition.");
        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), captured);
    }

    internal static bool TreeSnapshotsEquivalent(SecureTreeSnapshot left, SecureTreeSnapshot right)
    {
        if (left.RootIdentity != right.RootIdentity || left.Entries.Count != right.Entries.Count) return false;
        for (var index = 0; index < left.Entries.Count; index++)
        {
            var x = left.Entries[index];
            var y = right.Entries[index];
            if (!x.RelativePath.Equals(y.RelativePath, StringComparison.Ordinal) || x.IsDirectory != y.IsDirectory ||
                x.IsReparsePoint != y.IsReparsePoint || x.ObjectIdentity != y.ObjectIdentity ||
                x.Length != y.Length || !x.Sha256.Equals(y.Sha256, StringComparison.Ordinal)) return false;
        }
        return true;
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
        string expectedPhysicalLockRoot, SecureDirectoryIdentity expectedLockRootIdentity, CancellationToken token,
        Action? parentLeaseAcquired = null)
    {
        var parent = Path.GetDirectoryName(path)!;
        EnsureNoReparseAncestors(parent);
        if (DirectoryIdentity(parent) != expectedLockRootIdentity ||
            !PhysicalDirectory(parent).Equals(expectedPhysicalLockRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Lock root identity changed.");
        using var parentLease = AcquireDirectoryLease(parent, expectedPhysicalLockRoot, expectedLockRootIdentity);
        parentLeaseAcquired?.Invoke();
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
                    parentLease.Verify();
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
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock ioStatusBlock,
        IntPtr information, uint size, int informationClass);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref FileDispositionInformation information, uint size);

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
    internal static SecureDirectoryIdentity DirectoryIdentity(SafeFileHandle handle)
    {
        if (!GetFileIdInformationByHandle(handle, FileIdInfoClass, out FileIdInformation info,
                (uint)Marshal.SizeOf<FileIdInformation>())) throw Win32IOException("Directory file ID query");
        return new(info.VolumeSerialNumber,
            $"{info.FileIdHigh:x16}{info.FileIdLow:x16}");
    }
    internal static string FinalPath(SafeFileHandle handle)
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
    internal static string ValidateSafeLeafName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Length > 255 ||
            value != value.Normalize(NormalizationForm.FormC) || value.EndsWith('.') || value.EndsWith(' ') ||
            value.Any(ch => char.IsControl(ch) || ch is '/' or '\\' or ':' || Path.GetInvalidFileNameChars().Contains(ch)))
            throw new IOException("Unsafe rename leaf rejected.");
        var deviceStem = value.Split('.', 2)[0];
        if (SecureModuleIdentity.IsDeviceName(deviceStem)) throw new IOException("Reserved rename leaf rejected.");
        return value;
    }

    private static void SetRenameInformation(SafeFileHandle sourceHandle, SecureDirectoryLease destinationParentLease,
        string destinationLeaf, bool replaceIfExists)
    {
        var name = Encoding.Unicode.GetBytes(ValidateSafeLeafName(destinationLeaf));
        var replaceOffset = checked((int)Marshal.OffsetOf<FileRenameInformationHeader>(
            nameof(FileRenameInformationHeader.ReplaceIfExists)));
        var rootOffset = checked((int)Marshal.OffsetOf<FileRenameInformationHeader>(
            nameof(FileRenameInformationHeader.RootDirectory)));
        var lengthOffset = checked((int)Marshal.OffsetOf<FileRenameInformationHeader>(
            nameof(FileRenameInformationHeader.FileNameLength)));
        var nameOffset = checked(lengthOffset + sizeof(uint));
        ValidateRenameLayout(replaceOffset, rootOffset, lengthOffset, nameOffset);
        var size = checked(nameOffset + name.Length);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteByte(buffer, replaceOffset, replaceIfExists ? (byte)1 : (byte)0);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            destinationParentLease.UseHandle(parentHandle =>
            {
                Marshal.WriteIntPtr(buffer, rootOffset, parentHandle);
                var status = NtSetInformationFile(sourceHandle, out _, buffer, (uint)size,
                    FileRenameInformationClass);
                if (status < 0)
                    throw new IOException($"Parent-handle-relative rename failed with NTSTATUS 0x{status:x8} " +
                                          $"(Win32 error {RtlNtStatusToDosError(status)}).");
            });
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ValidateRenameLayout(int replaceOffset, int rootOffset, int lengthOffset, int nameOffset)
    {
        var valid = IntPtr.Size == 8
            ? replaceOffset == 0 && rootOffset == 8 && lengthOffset == 16 && nameOffset == 20
            : IntPtr.Size == 4 && replaceOffset == 0 && rootOffset == 4 && lengthOffset == 8 && nameOffset == 12;
        if (!valid) throw new PlatformNotSupportedException("Unexpected FILE_RENAME_INFO ABI layout.");
    }

    private static void TryDeleteByHandle(SafeFileHandle handle)
    {
        try
        {
            var disposition = new FileDispositionInformation { DeleteFile = true };
            _ = SetFileInformationByHandle(handle, 4, ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>());
        }
        catch { }
    }

    internal static (int ReplaceIfExists, int RootDirectory, int FileNameLength, int FileName) RenameLayoutForTest()
    {
        var replace = checked((int)Marshal.OffsetOf<FileRenameInformationHeader>(nameof(FileRenameInformationHeader.ReplaceIfExists)));
        var root = checked((int)Marshal.OffsetOf<FileRenameInformationHeader>(nameof(FileRenameInformationHeader.RootDirectory)));
        var length = checked((int)Marshal.OffsetOf<FileRenameInformationHeader>(nameof(FileRenameInformationHeader.FileNameLength)));
        var name = checked(length + sizeof(uint));
        ValidateRenameLayout(replace, root, length, name);
        return (replace, root, length, name);
    }
    internal static ((int ReplaceIfExists, int RootDirectory, int FileNameLength, int FileName) X86,
        (int ReplaceIfExists, int RootDirectory, int FileNameLength, int FileName) X64)
        FixedArchitectureRenameLayoutsForTest()
    {
        var x86Length = checked((int)Marshal.OffsetOf<FileRenameInformationHeader32>(
            nameof(FileRenameInformationHeader32.FileNameLength)));
        var x86 = (
            checked((int)Marshal.OffsetOf<FileRenameInformationHeader32>(
                nameof(FileRenameInformationHeader32.ReplaceIfExists))),
            checked((int)Marshal.OffsetOf<FileRenameInformationHeader32>(
                nameof(FileRenameInformationHeader32.RootDirectory))),
            x86Length,
            x86Length + sizeof(uint));
        var x64Length = checked((int)Marshal.OffsetOf<FileRenameInformationHeader64>(
            nameof(FileRenameInformationHeader64.FileNameLength)));
        var x64 = (
            checked((int)Marshal.OffsetOf<FileRenameInformationHeader64>(
                nameof(FileRenameInformationHeader64.ReplaceIfExists))),
            checked((int)Marshal.OffsetOf<FileRenameInformationHeader64>(
                nameof(FileRenameInformationHeader64.RootDirectory))),
            x64Length,
            x64Length + sizeof(uint));
        if (x86 != (0, 4, 8, 12) || x64 != (0, 8, 16, 20))
            throw new PlatformNotSupportedException("Unexpected fixed FILE_RENAME_INFORMATION ABI layout.");
        return (x86, x64);
    }
    private static IOException Win32IOException(string action) => new($"{action} failed with Win32 error {Marshal.GetLastPInvokeError()}.");
    [StructLayout(LayoutKind.Sequential)] private struct FileAttributeTagInformation { public uint FileAttributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInformationHeader
    {
        public byte ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInformationHeader32
    {
        public byte ReplaceIfExists;
        public int RootDirectory;
        public uint FileNameLength;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInformationHeader64
    {
        public byte ReplaceIfExists;
        public long RootDirectory;
        public uint FileNameLength;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile;
    }
}

internal sealed class SecureDirectoryLease : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly string _physicalRoot;
    private readonly SecureDirectoryIdentity _identity;
    internal SecureDirectoryLease(SafeFileHandle handle, string physicalRoot, SecureDirectoryIdentity identity)
    { _handle = handle; _physicalRoot = physicalRoot; _identity = identity; }
    public void Verify()
    {
        if (_handle.IsInvalid || _handle.IsClosed || SecureWindowsFileSystem.DirectoryIdentity(_handle) != _identity ||
            !SecureWindowsFileSystem.IsWithin(_physicalRoot, SecureWindowsFileSystem.FinalPath(_handle)))
            throw new IOException("Directory lease identity changed.");
    }
    internal void UseHandle(Action<IntPtr> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Verify();
        var added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            action(_handle.DangerousGetHandle());
        }
        finally
        {
            if (added) _handle.DangerousRelease();
        }
    }
    public void Dispose() => _handle.Dispose();
}

internal sealed class SecureTreeLease : IDisposable
{
    private readonly SafeFileHandle _rootHandle;
    private readonly IReadOnlyList<SecureTreeLeasedHandle> _handles;
    private readonly IReadOnlyDictionary<string, byte[]> _capturedFiles;
    private readonly SecureTreeLeaseLimits _limits;
    private int _disposed;
    private int _contentHandlesReleased;

    internal SecureTreeLease(SafeFileHandle rootHandle, string rootPath, SecureTreeSnapshot snapshot,
        IReadOnlyList<SecureTreeLeasedHandle> handles,
        IReadOnlyDictionary<string, byte[]> capturedFiles, SecureTreeLeaseLimits limits)
    {
        _rootHandle = rootHandle;
        RootPath = rootPath;
        Snapshot = snapshot;
        _handles = handles;
        _capturedFiles = capturedFiles;
        _limits = limits;
    }

    public string RootPath { get; }
    public SecureTreeSnapshot Snapshot { get; }

    public bool TryGetVerifiedFileBytes(string relativePath, out byte[] bytes)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_capturedFiles.TryGetValue(relativePath.Replace('\\', '/'), out var stored))
        {
            bytes = stored.ToArray();
            return true;
        }
        bytes = [];
        return false;
    }

    public bool ContainsOrdinaryFile(string relativePath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var normalized = relativePath.Replace('\\', '/');
        return Snapshot.Entries.Any(entry => !entry.IsDirectory && !entry.IsReparsePoint &&
            entry.RelativePath.Equals(normalized, StringComparison.Ordinal));
    }

    public void VerifyAtPath(string path)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var full = Path.GetFullPath(path);
        if (_rootHandle.IsInvalid || _rootHandle.IsClosed ||
            SecureWindowsFileSystem.DirectoryIdentity(_rootHandle) != Snapshot.RootIdentity ||
            !SecureWindowsFileSystem.IsWithin(full, SecureWindowsFileSystem.FinalPath(_rootHandle)) ||
            !SecureWindowsFileSystem.FinalPath(_rootHandle).Equals(full, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Tree root lease identity changed.");
        if (Volatile.Read(ref _contentHandlesReleased) == 0)
            foreach (var leased in _handles) leased.Verify(full);
        using var current = SecureWindowsFileSystem.AcquireTreeLeaseOnce(full, _limits, rootRenameAccess: false);
        if (!SecureWindowsFileSystem.TreeSnapshotsEquivalent(Snapshot, current.Snapshot))
            throw new IOException("Tree changed while its lease was held.");
    }

    internal void PrepareForRename(string path)
    {
        VerifyAtPath(path);
        if (Interlocked.Exchange(ref _contentHandlesReleased, 1) != 0) return;
        foreach (var handle in _handles.Reverse()) handle.Dispose();
    }

    internal void UseRootHandle(Action<SafeFileHandle> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(action);
        action(_rootHandle);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (Interlocked.Exchange(ref _contentHandlesReleased, 1) == 0)
            foreach (var handle in _handles.Reverse()) handle.Dispose();
        _rootHandle.Dispose();
    }
}

internal sealed class SecureTreeLeasedHandle : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly SecureDirectoryIdentity _identity;
    private readonly long _length;
    private readonly string _sha256;
    private readonly bool _isDirectory;

    public SecureTreeLeasedHandle(string relativePath, bool isDirectory, SafeFileHandle handle,
        SecureDirectoryIdentity identity, long length, string sha256)
    {
        RelativePath = relativePath;
        _isDirectory = isDirectory;
        _handle = handle;
        _identity = identity;
        _length = length;
        _sha256 = sha256;
    }

    public string RelativePath { get; }

    public void Verify(string physicalRoot)
    {
        if (_handle.IsInvalid || _handle.IsClosed ||
            SecureWindowsFileSystem.DirectoryIdentity(_handle) != _identity ||
            !SecureWindowsFileSystem.IsWithin(physicalRoot, SecureWindowsFileSystem.FinalPath(_handle)))
            throw new IOException("Tree entry lease identity changed.");
        if (_isDirectory) return;
        if (RandomAccess.GetLength(_handle) != _length) throw new IOException("Tree file length changed.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < _length)
        {
            var count = (int)Math.Min(buffer.Length, _length - offset);
            var read = RandomAccess.Read(_handle, buffer.AsSpan(0, count), offset);
            if (read <= 0) throw new IOException("Tree file ended while lease was held.");
            hash.AppendData(buffer, 0, read);
            offset += read;
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(_sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Tree file content changed while lease was held.");
    }

    public void Dispose() => _handle.Dispose();
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
