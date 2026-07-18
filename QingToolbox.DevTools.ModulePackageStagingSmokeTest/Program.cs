using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using QingToolbox.Core.Updates;

try
{
    await StagingSmoke.RunAsync();
    WriteDiagnostic("success", "None");
}
catch (Exception exception)
{
    WriteDiagnostic("failed", exception.Message);
    throw;
}

static void WriteDiagnostic(string status, string summary)
{
    var path = Environment.GetEnvironmentVariable("QMOD_STAGING_DIAGNOSTIC_PATH");
    if (string.IsNullOrWhiteSpace(path)) return;
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var safe = summary.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        safe = safe.Replace(Path.GetTempPath(), "%TEMP%", StringComparison.OrdinalIgnoreCase);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) safe = safe.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(path, $"status={status}\nfailure={safe}\narchives=runtime-only\npartialCleanup=checked-per-case\n");
    }
    catch { }
}

static class StagingSmoke
{
    private const string ModuleId = "qing.staging-probe";
    private const string Version = "0.2.0";
    private static readonly byte[] ProbeAssembly = File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory, "QingToolbox.DevTools.StagingPayloadProbe.dll"));

    public static async Task RunAsync()
    {
        await InTemp(async root =>
        {
            var sentinel = Path.Combine(root, "payload-loaded.sentinel");
            Environment.SetEnvironmentVariable("QINGTOOLBOX_STAGING_PROBE_SENTINEL", sentinel);
            Assert(!IsProbeLoaded(), "probe assembly starts unloaded");
            await ValidAndIsolationAsync(root, sentinel);
            await PackageBindingAsync(root);
            await PathAttacksAsync(root);
            await EntryTypesAsync(root);
            await FilesystemReparseAsync(root);
            await BombLimitsAsync(root);
            await ManifestFailuresAsync(root);
            await TransactionsAsync(root);
            Assert(!IsProbeLoaded() && !File.Exists(sentinel), "staging never loads or executes payload DLL");
        });
        Console.WriteLine("Module package staging smoke test passed: hostile archives, identity binding, atomic publication, isolation and no DLL execution.");
    }

    private static async Task ValidAndIsolationAsync(string root, string sentinel)
    {
        var caseRoot = Case(root, "valid");
        var userModules = Path.Combine(caseRoot, "UserModules");
        Directory.CreateDirectory(userModules);
        var userSentinel = Path.Combine(userModules, "unchanged.txt");
        await File.WriteAllTextAsync(userSentinel, "keep");
        var input = CreatePackage(caseRoot);
        var events = new List<QmodStagingLogEvent>();
        await using var service = Service(caseRoot, log: events.Add);
        var result = await service.StageAsync(input);
        Assert(result.Succeeded && !result.Reused && result.FailureCode == QmodStagingFailureCode.None, "valid package staged");
        Assert(result.StagingDirectory is not null && Directory.Exists(result.StagingDirectory), "verified directory published");
        Assert(File.Exists(Path.Combine(result.StagingDirectory!, QmodPackageStagingService.StagingMetadataName)), "host metadata generated");
        Assert(!Directory.EnumerateDirectories(Path.Combine(caseRoot, "Staging", "Incoming")).Any(), "no partial after success");
        Assert(await File.ReadAllTextAsync(userSentinel) == "keep" && Directory.EnumerateFileSystemEntries(userModules).Count() == 1,
            "UserModulesDirectory unchanged");
        Assert(!File.Exists(sentinel) && !IsProbeLoaded(), "valid staging does not execute DLL");
        Assert(events.Any(item => item.EventName == "Verified directory published"), "structured staging events emitted");
        var metadataText = await File.ReadAllTextAsync(Path.Combine(result.StagingDirectory!, QmodPackageStagingService.StagingMetadataName));
        Assert(!metadataText.Contains(caseRoot, StringComparison.OrdinalIgnoreCase), "metadata excludes private absolute paths");
    }

    private static async Task PackageBindingAsync(string root)
    {
        await Expect(root, "missing-package", QmodStagingFailureCode.PackageMissing, input =>
        {
            File.Delete(input.VerifiedPackage.FilePath); return input;
        });
        await Expect(root, "size-changed", QmodStagingFailureCode.PackageSizeMismatch, input =>
        {
            File.AppendAllText(input.VerifiedPackage.FilePath, "x"); return input;
        });
        await Expect(root, "hash-changed", QmodStagingFailureCode.PackageHashMismatch, input =>
        {
            using var stream = new FileStream(input.VerifiedPackage.FilePath, FileMode.Open, FileAccess.ReadWrite);
            var value = stream.ReadByte(); stream.Position = 0; stream.WriteByte((byte)(value ^ 0xFF)); return input;
        });
        await Expect(root, "release-binding", QmodStagingFailureCode.PackageChanged,
            input => input with { ReleaseIdentity = input.ReleaseIdentity with { ExpectedSize = input.ReleaseIdentity.ExpectedSize + 1 } });
    }

    private static async Task PathAttacksAsync(string root)
    {
        foreach (var (name, path, code) in new[]
        {
            ("parent-forward", "../evil.txt", QmodStagingFailureCode.UnsafeEntryPath),
            ("parent-back", "..\\evil.txt", QmodStagingFailureCode.UnsafeEntryPath),
            ("absolute", "/evil.txt", QmodStagingFailureCode.UnsafeEntryPath),
            ("drive", "C:\\evil.txt", QmodStagingFailureCode.UnsafeEntryPath),
            ("unc", "\\\\server\\share\\evil.txt", QmodStagingFailureCode.UnsafeEntryPath),
            ("ads", "payload.dll:stream", QmodStagingFailureCode.UnsafeEntryPath),
            ("empty-segment", "folder//evil.txt", QmodStagingFailureCode.UnsafeEntryPath),
            ("trailing-dot", "folder./evil.txt", QmodStagingFailureCode.UnsafeEntryPath),
            ("trailing-space", "folder /evil.txt", QmodStagingFailureCode.UnsafeEntryPath),
            ("con", "con.txt", QmodStagingFailureCode.UnsafeEntryPath),
            ("prn", "PRN.json", QmodStagingFailureCode.UnsafeEntryPath),
            ("aux", "folder/AUX.dll", QmodStagingFailureCode.UnsafeEntryPath),
            ("nul", "NUL", QmodStagingFailureCode.UnsafeEntryPath),
            ("com", "COM9.bin", QmodStagingFailureCode.UnsafeEntryPath),
            ("lpt", "folder/LPT1.dll", QmodStagingFailureCode.UnsafeEntryPath)
        })
            await ExpectEntries(root, name, code, entries => ReplaceResource(entries, path));

        await ExpectEntries(root, "case-collision", QmodStagingFailureCode.PathCollision,
            entries => entries.Append(new("I18N/EN-US.JSON", [1])).ToList());
        await ExpectEntries(root, "unicode-collision", QmodStagingFailureCode.PathCollision,
            entries => entries.Append(new("i18n/e\u0301.json", [1])).Append(new("i18n/é.json", [2])).ToList());
        await ExpectEntries(root, "file-directory-collision", QmodStagingFailureCode.PathCollision,
            entries => entries.Append(new("collision", [1])).Append(new("collision/child.txt", [2])).ToList());
        await ExpectEntries(root, "leaf-directory-collision", QmodStagingFailureCode.PathCollision,
            entries => entries.Append(new("leaf", [1])).Append(new("leaf/", [], DirectoryAttributes())).ToList());
    }

    private static async Task EntryTypesAsync(string root)
    {
        await ExpectEntries(root, "symlink", QmodStagingFailureCode.UnsupportedEntryType,
            entries => entries.Append(new("link", Encoding.UTF8.GetBytes("payload.dll"), UnixAttributes(0xA000))).ToList());
        await ExpectEntries(root, "reparse", QmodStagingFailureCode.UnsupportedEntryType,
            entries => entries.Append(new("reparse", [1], (int)FileAttributes.ReparsePoint)).ToList());
        foreach (var (name, type) in new[] { ("fifo", 0x1000), ("char-device", 0x2000), ("block-device", 0x6000), ("socket", 0xC000) })
            await ExpectEntries(root, name, QmodStagingFailureCode.UnsupportedEntryType,
                entries => entries.Append(new(name, [1], UnixAttributes(type))).ToList());
    }

    private static async Task FilesystemReparseAsync(string root)
    {
        var caseRoot = Case(root, "filesystem-reparse");
        var target = Path.Combine(caseRoot, "external-target");
        var staging = Path.Combine(caseRoot, "Staging");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "must-survive.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        try { Directory.CreateSymbolicLink(staging, target); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine("Filesystem reparse race check skipped because symbolic-link creation is unavailable.");
            return;
        }
        var input = CreatePackage(caseRoot);
        await using var service = Service(caseRoot);
        var result = await service.StageAsync(input);
        Assert(result.FailureCode == QmodStagingFailureCode.UnsupportedEntryType, "filesystem staging-root reparse rejected");
        Assert(await File.ReadAllTextAsync(sentinel) == "keep", "filesystem reparse target untouched");
        Directory.Delete(staging, false);
    }

    private static async Task BombLimitsAsync(string root)
    {
        await ExpectWithLimits(root, "entry-limit", new(MaximumEntries: 3), QmodStagingFailureCode.EntryLimitExceeded);
        await ExpectWithLimits(root, "single-limit", new(MaximumSingleFileBytes: 8), QmodStagingFailureCode.SingleFileLimitExceeded,
            entries => ReplaceResource(entries, "i18n/en-US.json", new byte[9]));
        await ExpectWithLimits(root, "total-limit", new(MaximumTotalUncompressedBytes: 100), QmodStagingFailureCode.TotalSizeLimitExceeded);
        await ExpectWithLimits(root, "depth-limit", new(MaximumDirectoryDepth: 2), QmodStagingFailureCode.UnsafeEntryPath,
            entries => ReplaceResource(entries, "a/b/c.txt"));
        await ExpectWithLimits(root, "path-limit", new(MaximumRelativePathLength: 12), QmodStagingFailureCode.UnsafeEntryPath);
        await ExpectWithLimits(root, "entry-ratio", new(MaximumEntryCompressionRatio: 1.01), QmodStagingFailureCode.CompressionRatioExceeded,
            entries => ReplaceResource(entries, "i18n/en-US.json", new byte[4096]));
        await ExpectWithLimits(root, "overall-ratio", new(MaximumEntryCompressionRatio: 10000, MaximumOverallCompressionRatio: 1.01),
            QmodStagingFailureCode.CompressionRatioExceeded,
            entries => ReplaceResource(entries, "i18n/en-US.json", new byte[4096]));
    }

    private static async Task ManifestFailuresAsync(string root)
    {
        await ExpectEntries(root, "qmod-missing", QmodStagingFailureCode.ManifestMissing,
            entries => entries.Where(entry => entry.Name != "qmod.json").ToList());
        await ExpectEntries(root, "module-missing", QmodStagingFailureCode.ManifestMissing,
            entries => entries.Where(entry => entry.Name != "module.json").ToList());
        await ExpectEntries(root, "qmod-duplicate", QmodStagingFailureCode.ManifestDuplicate,
            entries => entries.Append(entries.Single(entry => entry.Name == "qmod.json") with { Name = "QMOD.JSON" }).ToList());
        await ExpectEntries(root, "module-duplicate", QmodStagingFailureCode.ManifestDuplicate,
            entries => entries.Append(entries.Single(entry => entry.Name == "module.json") with { Name = "MODULE.JSON" }).ToList());
        await ExpectEntries(root, "invalid-json", QmodStagingFailureCode.ManifestInvalid,
            entries => Replace(entries, "qmod.json", Encoding.UTF8.GetBytes("{")));
        await ExpectEntries(root, "bom-json", QmodStagingFailureCode.ManifestInvalid,
            entries => Replace(entries, "qmod.json", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(QmodJson()).ToArray()));
        await ExpectEntries(root, "trailing-json", QmodStagingFailureCode.ManifestInvalid,
            entries => Replace(entries, "qmod.json", QmodJson().Concat(Encoding.UTF8.GetBytes("x")).ToArray()));
        await ExpectEntries(root, "duplicate-property", QmodStagingFailureCode.ManifestInvalid,
            entries => Replace(entries, "qmod.json", QmodJson(extra: ",\"moduleId\":\"other\"")));
        await ExpectEntries(root, "nested-duplicate-property", QmodStagingFailureCode.ManifestInvalid,
            entries => Replace(entries, "module.json", Encoding.UTF8.GetBytes(
                $"{{\"id\":\"{ModuleId}\",\"version\":\"{Version}\",\"entry\":\"payload.dll\",\"localization\":{{\"basePath\":\"i18n\",\"basePath\":\"other\"}}}}")));
        await ExpectEntries(root, "unknown-schema", QmodStagingFailureCode.ManifestInvalid,
            entries => Replace(entries, "qmod.json", QmodJson(schema: 2)));
        await ExpectEntries(root, "unknown-qmod-property", QmodStagingFailureCode.ManifestInvalid,
            entries => Replace(entries, "qmod.json", QmodJson(extra: ",\"future\":true")));
        await ExpectEntries(root, "identity-mismatch", QmodStagingFailureCode.ModuleIdentityMismatch,
            entries => Replace(entries, "qmod.json", QmodJson(moduleId: "qing.other")));
        await ExpectEntries(root, "version-mismatch", QmodStagingFailureCode.VersionMismatch,
            entries => Replace(entries, "module.json", ModuleJson(version: "9.0.0")));
        await ExpectEntries(root, "api-mismatch", QmodStagingFailureCode.ModuleApiIncompatible,
            entries => Replace(entries, "qmod.json", QmodJson(api: "experimental-9")));
        await ExpectEntries(root, "missing-entry-dll", QmodStagingFailureCode.ManifestInvalid,
            entries => Replace(entries, "module.json", ModuleJson(entry: "missing.dll")));
        await ExpectEntries(root, "nested-wrapper", QmodStagingFailureCode.ManifestMissing,
            entries => entries.Select(entry => entry with { Name = "wrapper/" + entry.Name }).ToList());
        await ExpectWithLimits(root, "manifest-size", new(MaximumManifestBytes: 8),
            QmodStagingFailureCode.ManifestInvalid);
    }

    private static async Task TransactionsAsync(string root)
    {
        var reuseRoot = Case(root, "reuse");
        var input = CreatePackage(reuseRoot);
        await using (var service = Service(reuseRoot))
        {
            var first = await service.StageAsync(input);
            var second = await service.StageAsync(input);
            Assert(first.Succeeded && second.Succeeded && second.Reused && first.StagingDirectory == second.StagingDirectory, "same SHA idempotently reused");
            await File.AppendAllTextAsync(Path.Combine(first.StagingDirectory!, "payload.dll"), "tampered");
            Assert((await service.StageAsync(input)).FailureCode == QmodStagingFailureCode.StagingConflict,
                "tampered verified staging is never reused");
        }

        var concurrentRoot = Case(root, "concurrent");
        input = CreatePackage(concurrentRoot);
        await using (var service = Service(concurrentRoot))
        {
            var results = await Task.WhenAll(service.StageAsync(input), service.StageAsync(input), service.StageAsync(input));
            Assert(results.All(result => result.Succeeded) &&
                Directory.EnumerateDirectories(Path.Combine(concurrentRoot, "Staging", "Verified", ModuleId, Version)).Count() == 1,
                "concurrent same package publishes one final directory");
        }

        var conflictRoot = Case(root, "conflict");
        var firstInput = CreatePackage(conflictRoot, entries => ReplaceResource(entries, "i18n/en-US.json", [1]));
        var secondInput = CreatePackage(conflictRoot, entries => ReplaceResource(entries, "i18n/en-US.json", [2]), "second.qmod");
        await using (var service = Service(conflictRoot))
        {
            Assert((await service.StageAsync(firstInput)).Succeeded, "first conflict candidate staged");
            Assert((await service.StageAsync(secondInput)).FailureCode == QmodStagingFailureCode.StagingConflict,
                "same module/version different SHA rejected");
        }

        var cancellationRoot = Case(root, "cancel");
        input = CreatePackage(cancellationRoot, entries => ReplaceResource(entries, "large.bin", RandomNumberGenerator.GetBytes(8 * 1024 * 1024)));
        await using (var service = Service(cancellationRoot))
        {
            var task = service.StageAsync(input);
            service.Cancel(input);
            var result = await task;
            Assert(result.FailureCode == QmodStagingFailureCode.Cancelled, "underlying transaction cancellation reported");
            var incoming = Path.Combine(cancellationRoot, "Staging", "Incoming");
            Assert(!Directory.Exists(incoming) || !Directory.EnumerateDirectories(incoming).Any(), "cancel cleans only its partial");
        }

        var callerCancellationRoot = Case(root, "caller-cancel");
        input = CreatePackage(callerCancellationRoot,
            entries => ReplaceResource(entries, "large.bin", RandomNumberGenerator.GetBytes(8 * 1024 * 1024)));
        await using (var service = Service(callerCancellationRoot))
        {
            using var caller = new CancellationTokenSource();
            var cancelledWait = service.StageAsync(input, caller.Token);
            var survivingWait = service.StageAsync(input);
            caller.Cancel();
            try { await cancelledWait; throw new Exception("caller cancellation was ignored"); }
            catch (OperationCanceledException) { }
            Assert((await survivingWait).Succeeded, "caller cancellation does not destroy shared staging transaction");
        }

        var incompleteRoot = Case(root, "incomplete-final");
        input = CreatePackage(incompleteRoot);
        var final = Path.Combine(incompleteRoot, "Staging", "Verified", ModuleId, Version, input.ReleaseIdentity.Sha256);
        Directory.CreateDirectory(final);
        await using (var service = Service(incompleteRoot))
            Assert((await service.StageAsync(input)).FailureCode == QmodStagingFailureCode.StagingConflict, "incomplete final directory rejected");
    }

    private static async Task Expect(string root, string name, QmodStagingFailureCode expected,
        Func<QmodStagingInput, QmodStagingInput> mutate)
    {
        var caseRoot = Case(root, name); var input = mutate(CreatePackage(caseRoot));
        await using var service = Service(caseRoot);
        var result = await service.StageAsync(input);
        Assert(result.FailureCode == expected, $"{name}: expected {expected}, got {result.FailureCode}");
        AssertNoPartial(caseRoot, name);
    }

    private static Task ExpectEntries(string root, string name, QmodStagingFailureCode expected,
        Func<List<EntrySpec>, List<EntrySpec>> mutate) => Expect(root, name, expected, input => input,
            entries => mutate(entries));

    private static async Task Expect(string root, string name, QmodStagingFailureCode expected,
        Func<QmodStagingInput, QmodStagingInput> inputMutate, Func<List<EntrySpec>, List<EntrySpec>> entryMutate)
    {
        var caseRoot = Case(root, name); var input = inputMutate(CreatePackage(caseRoot, entryMutate));
        await using var service = Service(caseRoot);
        var result = await service.StageAsync(input);
        Assert(result.FailureCode == expected, $"{name}: expected {expected}, got {result.FailureCode}");
        AssertNoPartial(caseRoot, name);
    }

    private static async Task ExpectWithLimits(string root, string name, QmodStagingLimits limits,
        QmodStagingFailureCode expected, Func<List<EntrySpec>, List<EntrySpec>>? mutate = null)
    {
        var caseRoot = Case(root, name); var input = CreatePackage(caseRoot, mutate);
        await using var service = Service(caseRoot, limits);
        var result = await service.StageAsync(input);
        Assert(result.FailureCode == expected, $"{name}: expected {expected}, got {result.FailureCode}");
        AssertNoPartial(caseRoot, name);
    }

    private static QmodPackageStagingService Service(string root, QmodStagingLimits? limits = null,
        Action<QmodStagingLogEvent>? log = null) => new(Path.Combine(root, "Staging"), TimeProvider.System, limits, log);

    private static QmodStagingInput CreatePackage(string root, Func<List<EntrySpec>, List<EntrySpec>>? mutate = null,
        string fileName = "probe.qmod")
    {
        Directory.CreateDirectory(root);
        var entries = new List<EntrySpec>
        {
            new("qmod.json", QmodJson()), new("module.json", ModuleJson()),
            new("payload.dll", ProbeAssembly), new("i18n/en-US.json", Encoding.UTF8.GetBytes("{\"name\":\"Probe\"}"))
        };
        if (mutate is not null) entries = mutate(entries);
        var path = Path.Combine(root, fileName);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var spec in entries)
            {
                var entry = archive.CreateEntry(spec.Name, CompressionLevel.Optimal);
                if (spec.ExternalAttributes is { } attributes) entry.ExternalAttributes = attributes;
                if (!spec.Name.EndsWith('/') && !spec.Name.EndsWith('\\'))
                    using (var stream = entry.Open()) stream.Write(spec.Bytes);
            }
        }
        var bytes = File.ReadAllBytes(path); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var semanticVersion = SemanticVersion.Parse(Version);
        var verified = new VerifiedModulePackage(ModuleId, semanticVersion, fileName, path, bytes.LongLength, hash, DateTimeOffset.UtcNow);
        var identity = new ModulePackageDownloadIdentity(ModuleId, "0.1.0", Version, fileName,
            $"https://github.com/QingMo-A/QingToolbox/releases/download/v{Version}/{fileName}", bytes.LongLength, hash);
        return new(verified, identity, ModuleUpdateIdentity.ModuleApiVersion, "qingtoolbox-official", "ModuleTest");
    }

    private static List<EntrySpec> ReplaceResource(List<EntrySpec> entries, string name, byte[]? bytes = null) =>
        Replace(entries, "i18n/en-US.json", bytes ?? Encoding.UTF8.GetBytes("resource"), name);
    private static List<EntrySpec> Replace(List<EntrySpec> entries, string existing, byte[] bytes, string? newName = null) =>
        entries.Select(entry => entry.Name == existing ? new EntrySpec(newName ?? existing, bytes, entry.ExternalAttributes) : entry).ToList();
    private static byte[] QmodJson(int schema = 1, string moduleId = ModuleId, string version = Version,
        string api = ModuleUpdateIdentity.ModuleApiVersion, string extra = "") => Encoding.UTF8.GetBytes(
            $"{{\"schemaVersion\":{schema},\"moduleId\":\"{moduleId}\",\"version\":\"{version}\",\"moduleApiVersion\":\"{api}\",\"entryManifest\":\"module.json\"{extra}}}");
    private static byte[] ModuleJson(string moduleId = ModuleId, string version = Version, string entry = "payload.dll") => Encoding.UTF8.GetBytes(
        $"{{\"id\":\"{moduleId}\",\"name\":\"Probe\",\"description\":\"Probe\",\"version\":\"{version}\",\"entry\":\"{entry}\",\"runtimeType\":\"InProcess\",\"loadMode\":\"Manual\",\"defaultLanguage\":\"en-US\",\"localization\":{{\"basePath\":\"i18n\",\"resources\":{{\"en-US\":\"i18n/en-US.json\"}}}}}}");
    private static int UnixAttributes(int type) => (type | 0x1A4) << 16;
    private static int DirectoryAttributes() => (int)FileAttributes.Directory;
    private static bool IsProbeLoaded() => AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "QingToolbox.DevTools.StagingPayloadProbe");
    private static string Case(string root, string name) { var path = Path.Combine(root, "case-" + name); Directory.CreateDirectory(path); return path; }
    private static void AssertNoPartial(string root, string name)
    {
        var staging = Path.Combine(root, "Staging");
        Assert(!Directory.Exists(staging) || !Directory.EnumerateDirectories(staging, "*.partial", SearchOption.AllDirectories).Any(), name + " cleans partial");
    }
    private static void Assert(bool condition, string name) { if (!condition) throw new Exception("Assertion failed: " + name); }
    private static async Task InTemp(Func<string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "QingToolbox-staging-smoke-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(root); await action(root); }
        finally { Environment.SetEnvironmentVariable("QINGTOOLBOX_STAGING_PROBE_SENTINEL", null); try { Directory.Delete(root, true); } catch { } }
    }
    private sealed record EntrySpec(string Name, byte[] Bytes, int? ExternalAttributes = null);
}
