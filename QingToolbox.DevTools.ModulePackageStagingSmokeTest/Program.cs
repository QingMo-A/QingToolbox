using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using QingToolbox.Core.Updates;

if (args.Length > 0)
{
    await StagingWorker.RunAsync(args);
    return;
}

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

static class StagingWorker
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 8 || args[0] is not ("--worker-stage" or "--worker-hold"))
            throw new ArgumentException("Invalid staging worker arguments.");
        var mode = args[0]; var root = SafePath(args[1]); var package = SafePath(args[2]);
        var moduleId = args[3]; var version = args[4]; var gate = SafePath(args[5]);
        var signal = SafePath(args[6]); var resultPath = SafePath(args[7]);
        foreach (var path in new[] { package, gate, signal, resultPath })
            if (!IsWithin(root, path)) throw new UnauthorizedAccessException("Worker path escaped its temporary root.");
        if (mode == "--worker-stage") await File.WriteAllTextAsync(signal, "ready");
        await WaitForFileAsync(gate, TimeSpan.FromSeconds(30));
        var input = CreateInput(package, moduleId, version);
        QmodStagingTestHooks? hooks = mode == "--worker-hold"
            ? new(PublicationLockAcquired: async (_, token) =>
            {
                await File.WriteAllTextAsync(signal, "lock-acquired", token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }) : null;
        var events = new List<QmodStagingLogEvent>();
        await using var service = new QmodPackageStagingService(Path.Combine(root, "Staging"), TimeProvider.System,
            "ModuleTest", Path.Combine(root, "UserModules"), null, events.Add, 2, hooks);
        var result = await service.StageAsync(input);
        await File.WriteAllTextAsync(resultPath,
            $"success={result.Succeeded};reused={result.Reused};failure={result.FailureCode};" +
            $"crashRecovery={events.Any(item => item.EventName == "Publication lock recovered after process exit")}");
    }

    private static QmodStagingInput CreateInput(string package, string moduleId, string version)
    {
        var bytes = File.ReadAllBytes(package); var fileName = Path.GetFileName(package);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var verified = new VerifiedModulePackage(moduleId, SemanticVersion.Parse(version), fileName, package,
            bytes.LongLength, sha, DateTimeOffset.UtcNow);
        var identity = new ModulePackageDownloadIdentity(moduleId, "0.1.0", version, fileName,
            $"https://github.com/QingMo-A/QingToolbox/releases/download/v{version}/{fileName}", bytes.LongLength, sha);
        return new(verified, identity, ModuleUpdateIdentity.ModuleApiVersion, "qingtoolbox-official");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path)) await Task.Delay(25, cancellation.Token);
    }

    private static string SafePath(string value)
    {
        var path = Path.GetFullPath(value);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!IsWithin(temp, path) || !path.Contains("QingToolbox-staging-smoke-", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Worker accepts only its generated test root.");
        return path;
    }

    private static bool IsWithin(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
            await PackageSourceReparseAsync(root);
            await BombLimitsAsync(root);
            await ManifestFailuresAsync(root);
            await IdentityAndConcurrencyAsync(root);
            await CrossProcessPublicationAsync(root);
            await FailedPublicationCommitAsync(root);
            await ServiceDisposalAsync(root);
            await ExistingStagingTamperingAsync(root);
            await LockCancellationAsync(root);
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
        Assert(result.Succeeded && !result.Reused && result.FailureCode == QmodStagingFailureCode.None,
            $"valid package staged ({result.FailureCode})");
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
        await Expect(root, "package-inside-user-modules", QmodStagingFailureCode.PackageChanged, input =>
        {
            var directory = Path.Combine(Path.GetDirectoryName(input.VerifiedPackage.FilePath)!, "UserModules");
            Directory.CreateDirectory(directory); var path = Path.Combine(directory, input.ReleaseIdentity.FileName);
            File.Copy(input.VerifiedPackage.FilePath, path);
            return input with { VerifiedPackage = input.VerifiedPackage with { FilePath = path } };
        });
        await Expect(root, "package-inside-staging", QmodStagingFailureCode.PackageChanged, input =>
        {
            var directory = Path.Combine(Path.GetDirectoryName(input.VerifiedPackage.FilePath)!, "Staging", "source");
            Directory.CreateDirectory(directory); var path = Path.Combine(directory, input.ReleaseIdentity.FileName);
            File.Copy(input.VerifiedPackage.FilePath, path);
            return input with { VerifiedPackage = input.VerifiedPackage with { FilePath = path } };
        });
        AssertConstructorRejected(Path.Combine(root, "same-roots"), same: true, nestedUser: false, nestedStaging: false);
        AssertConstructorRejected(Path.Combine(root, "nested-user-root"), same: false, nestedUser: true, nestedStaging: false);
        AssertConstructorRejected(Path.Combine(root, "nested-staging-root"), same: false, nestedUser: false, nestedStaging: true);
        try
        {
            _ = new QmodPackageStagingService(Path.Combine(root, "unsupported-environment", "Staging"),
                TimeProvider.System, "Unknown");
            throw new Exception("unsupported environment accepted");
        }
        catch (QmodStagingConfigurationException exception)
        {
            Assert(exception.FailureCode == QmodStagingConfigurationFailureCode.UnsupportedEnvironment,
                "unsupported environment reports the precise configuration failure");
        }
    }

    private static void AssertConstructorRejected(string root, bool same, bool nestedUser, bool nestedStaging)
    {
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "Staging");
        var user = same ? staging : nestedUser ? Path.Combine(staging, "UserModules") :
            nestedStaging ? Path.Combine(root, "UserModules", "Staging") : Path.Combine(root, "UserModules");
        if (nestedStaging) staging = Path.Combine(user, "NestedStaging");
        try
        {
            _ = new QmodPackageStagingService(staging, TimeProvider.System, "ModuleTest", user);
            throw new Exception("overlapping roots accepted");
        }
        catch (QmodStagingConfigurationException exception)
        {
            Assert(exception.FailureCode == QmodStagingConfigurationFailureCode.OverlappingRoots,
                "overlapping roots report the precise configuration failure");
        }
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
        _ = CreatePackage(caseRoot);
        try
        {
            await using var service = Service(caseRoot);
            throw new Exception("filesystem staging-root reparse accepted");
        }
        catch (QmodStagingConfigurationException exception)
        {
            Assert(exception.FailureCode == QmodStagingConfigurationFailureCode.UnsafeStagingRoot,
                "staging root reparse reports UnsafeStagingRoot");
        }
        Assert(await File.ReadAllTextAsync(sentinel) == "keep", "filesystem reparse target untouched");
        Directory.Delete(staging, false);
    }

    private static async Task PackageSourceReparseAsync(string root)
    {
        var caseRoot = Case(root, "package-source-reparse");
        var target = Path.Combine(caseRoot, "physical-source"); var link = Path.Combine(caseRoot, "linked-source");
        Directory.CreateDirectory(target); var input = CreatePackage(target);
        var sentinel = Path.Combine(target, "must-survive.txt"); await File.WriteAllTextAsync(sentinel, "keep");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine("Package parent reparse checks skipped because symbolic-link creation is unavailable.");
            return;
        }
        var linkedInput = input with { VerifiedPackage = input.VerifiedPackage with
        { FilePath = Path.Combine(link, input.ReleaseIdentity.FileName) } };
        await using (var service = Service(caseRoot))
            Assert((await service.StageAsync(linkedInput)).FailureCode == QmodStagingFailureCode.PackageChanged,
                "package parent reparse rejected");
        var userModules = Path.Combine(caseRoot, "UserModules"); Directory.CreateDirectory(userModules);
        var userPackage = Path.Combine(userModules, "user-source.qmod"); File.Copy(input.VerifiedPackage.FilePath, userPackage);
        var userLink = Path.Combine(caseRoot, "linked-user-modules"); Directory.CreateSymbolicLink(userLink, userModules);
        var userInput = RepathInput(input, Path.Combine(userLink, "user-source.qmod"), "user-source.qmod");
        await using (var service = Service(caseRoot))
            Assert((await service.StageAsync(userInput)).FailureCode == QmodStagingFailureCode.PackageChanged,
                "junction-style parent into UserModules rejected");
        var stagingSource = Path.Combine(caseRoot, "Staging", "source"); Directory.CreateDirectory(stagingSource);
        var stagingPackage = Path.Combine(stagingSource, "staging-source.qmod"); File.Copy(input.VerifiedPackage.FilePath, stagingPackage);
        var stagingLink = Path.Combine(caseRoot, "linked-staging"); Directory.CreateSymbolicLink(stagingLink, stagingSource);
        var stagingInput = RepathInput(input, Path.Combine(stagingLink, "staging-source.qmod"), "staging-source.qmod");
        await using (var service = Service(caseRoot))
            Assert((await service.StageAsync(stagingInput)).FailureCode == QmodStagingFailureCode.PackageChanged,
                "junction-style parent into Staging rejected");
        var fileLink = Path.Combine(caseRoot, "linked-file.qmod");
        try
        {
            File.CreateSymbolicLink(fileLink, input.VerifiedPackage.FilePath);
            var fileInput = input with
            {
                VerifiedPackage = input.VerifiedPackage with { FilePath = fileLink, FileName = "linked-file.qmod" },
                ReleaseIdentity = input.ReleaseIdentity with
                {
                    FileName = "linked-file.qmod",
                    OfficialUrl = input.ReleaseIdentity.OfficialUrl.Replace(input.ReleaseIdentity.FileName, "linked-file.qmod")
                }
            };
            await using var service = Service(caseRoot);
            Assert((await service.StageAsync(fileInput)).FailureCode == QmodStagingFailureCode.PackageChanged,
                "package file symlink rejected");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
        Assert(await File.ReadAllTextAsync(sentinel) == "keep", "external package reparse target remains untouched");
        Directory.Delete(link, false);
        Directory.Delete(userLink, false); Directory.Delete(stagingLink, false);
    }

    private static QmodStagingInput RepathInput(QmodStagingInput input, string path, string fileName) => input with
    {
        VerifiedPackage = input.VerifiedPackage with { FilePath = path, FileName = fileName },
        ReleaseIdentity = input.ReleaseIdentity with
        {
            FileName = fileName,
            OfficialUrl = input.ReleaseIdentity.OfficialUrl.Replace(input.ReleaseIdentity.FileName, fileName)
        }
    };

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
            Assert((await service.StageAsync(input)).FailureCode == QmodStagingFailureCode.VerifiedStagingInvalid,
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
            var firstConflict = await service.StageAsync(firstInput);
            Assert(firstConflict.Succeeded, $"first conflict candidate staged ({firstConflict.FailureCode})");
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
            Assert((await service.StageAsync(input)).FailureCode == QmodStagingFailureCode.StagingMetadataInvalid, "incomplete final directory rejected");
    }

    private static async Task IdentityAndConcurrencyAsync(string root)
    {
        var sharedRoot = Case(root, "complete-identity-sharing");
        var sharedInput = CreatePackage(sharedRoot);
        var sharedEvents = new List<QmodStagingLogEvent>();
        await using (var service = Service(sharedRoot, log: item => { lock (sharedEvents) sharedEvents.Add(item); }))
        {
            var results = await Task.WhenAll(service.StageAsync(sharedInput), service.StageAsync(sharedInput), service.StageAsync(sharedInput));
            Assert(results.All(result => result.Succeeded), "three complete-identity callers succeed");
            Assert(sharedEvents.Count(item => item.EventName == "Verified directory published") == 1,
                "complete identity publishes exactly once");
            Assert(sharedEvents.Count(item => item.EventName == "Shared operation joined") == 2,
                "complete identity joins one operation");
        }

        var identityRoot = Case(root, "same-sha-distinct-identity");
        var correct = CreatePackage(identityRoot);
        var wrongModule = correct with
        {
            VerifiedPackage = correct.VerifiedPackage with { ModuleId = "qing.other-probe" },
            ReleaseIdentity = correct.ReleaseIdentity with { ModuleId = "qing.other-probe" }
        };
        var wrongVersion = correct with
        {
            VerifiedPackage = correct.VerifiedPackage with { Version = SemanticVersion.Parse("0.2.1") },
            ReleaseIdentity = correct.ReleaseIdentity with { TargetVersion = "0.2.1" }
        };
        var wrongFileName = correct with { ReleaseIdentity = correct.ReleaseIdentity with { FileName = "other.qmod" } };
        var wrongSize = correct with { ReleaseIdentity = correct.ReleaseIdentity with { ExpectedSize = correct.ReleaseIdentity.ExpectedSize + 1 } };
        var wrongApi = correct with { ModuleApiVersion = "999.0" };
        var wrongSource = correct with { SourceIdentity = "untrusted-source" };
        var copiedPath = Path.Combine(identityRoot, "copied.qmod");
        File.Copy(correct.VerifiedPackage.FilePath, copiedPath);
        using (var stream = new FileStream(copiedPath, FileMode.Open, FileAccess.Write, FileShare.None)) stream.WriteByte(0);
        var wrongPath = correct with { VerifiedPackage = correct.VerifiedPackage with { FilePath = copiedPath } };
        await using (var service = Service(identityRoot))
        {
            var correctTask = service.StageAsync(correct);
            var failures = await Task.WhenAll(service.StageAsync(wrongModule), service.StageAsync(wrongVersion),
                service.StageAsync(wrongFileName), service.StageAsync(wrongSize), service.StageAsync(wrongApi),
                service.StageAsync(wrongSource), service.StageAsync(wrongPath));
            Assert((await correctTask).Succeeded, "correct complete identity succeeds");
            Assert(failures[0].FailureCode == QmodStagingFailureCode.ModuleIdentityMismatch, "different module identity is independently checked");
            Assert(failures[1].FailureCode == QmodStagingFailureCode.VersionMismatch, "different version identity is independently checked");
            Assert(failures.Skip(2).Take(4).All(result => result.FailureCode == QmodStagingFailureCode.PackageChanged),
                "different static trust fields fail before sharing");
            Assert(failures[6].FailureCode == QmodStagingFailureCode.PackageHashMismatch,
                "different package path receives its own TOCTOU hash check");
        }
        await using (var differentEnvironment = Service(identityRoot, environment: "Development"))
            Assert((await differentEnvironment.StageAsync(correct)).FailureCode == QmodStagingFailureCode.StagingMetadataInvalid,
                "service-bound environment cannot reuse other environment metadata");

        var releaseRoot = Case(root, "official-release-identity");
        var releaseInput = CreatePackage(releaseRoot);
        var otherRelease = releaseInput with { ReleaseIdentity = releaseInput.ReleaseIdentity with
        { OfficialUrl = releaseInput.ReleaseIdentity.OfficialUrl.Replace($"/v{Version}/", $"/v{Version}-alternate/") } };
        var releaseEvents = new List<QmodStagingLogEvent>();
        await using (var service = Service(releaseRoot, log: item => { lock (releaseEvents) releaseEvents.Add(item); }))
        {
            var releaseResults = await Task.WhenAll(service.StageAsync(releaseInput), service.StageAsync(otherRelease));
            Assert(releaseResults.Count(result => result.Succeeded) == 1 &&
                   releaseResults.Count(result => result.FailureCode == QmodStagingFailureCode.StagingMetadataInvalid) == 1,
                "distinct official Release identities cannot reuse each other's staging metadata");
            Assert(releaseEvents.All(item => item.EventName != "Shared operation joined"),
                "OfficialUrl identity prevents in-flight sharing");
        }

        var conflictRoot = Case(root, "cross-service-sha-conflict");
        var first = CreatePackage(conflictRoot, entries => ReplaceResource(entries, "i18n/en-US.json", [10]), "first.qmod");
        var second = CreatePackage(conflictRoot, entries => ReplaceResource(entries, "i18n/en-US.json", [20]), "second.qmod");
        await using var firstService = Service(conflictRoot);
        await using var secondService = Service(conflictRoot);
        var conflictResults = await Task.WhenAll(firstService.StageAsync(first), secondService.StageAsync(second));
        Assert(conflictResults.Count(result => result.Succeeded) == 1 &&
               conflictResults.Count(result => result.FailureCode == QmodStagingFailureCode.StagingConflict) == 1,
            "two services deterministically publish only one SHA");
        var versionRoot = Path.Combine(conflictRoot, "Staging", "Verified", ModuleId, Version);
        Assert(Directory.EnumerateDirectories(versionRoot).Count() == 1, "cross-service conflict leaves one SHA directory");
        AssertNoPartial(conflictRoot, "cross-service conflict");
        AssertNoLocks(conflictRoot, "cross-service conflict");

        var parallelRoot = Case(root, "different-modules-parallel");
        using var reached = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);
        void Barrier(QmodStagingLogEvent item)
        {
            if (item.EventName != "Archive metadata accepted") return;
            reached.Signal();
            if (reached.CurrentCount == 0) release.Set();
            Assert(release.Wait(TimeSpan.FromSeconds(10)), "different modules reach extraction concurrently");
        }
        var moduleA = CreatePackage(parallelRoot, fileName: "a.qmod", moduleId: "qing.parallel-a");
        var moduleB = CreatePackage(parallelRoot, fileName: "b.qmod", moduleId: "qing.parallel-b");
        await using (var service = Service(parallelRoot, log: Barrier))
        {
            var results = await Task.WhenAll(service.StageAsync(moduleA), service.StageAsync(moduleB));
            Assert(results.All(result => result.Succeeded),
                "different modules remain parallel (" + string.Join(",", results.Select(result => result.FailureCode)) + ")");
        }

        var isolatedA = Case(root, "gate-root-a"); var isolatedB = Case(root, "gate-root-b");
        var isolatedInputA = CreatePackage(isolatedA); var isolatedInputB = CreatePackage(isolatedB);
        using var isolatedReached = new CountdownEvent(2); using var isolatedRelease = new ManualResetEventSlim(false);
        async Task IsolatedBarrier(QmodStagingInput _, CancellationToken token)
        {
            isolatedReached.Signal(); if (isolatedReached.CurrentCount == 0) isolatedRelease.Set();
            await Task.Run(() => isolatedRelease.Wait(token), token);
        }
        var isolatedHooks = new QmodStagingTestHooks(PublicationLockAcquired: IsolatedBarrier);
        await using var isolatedServiceA = Service(isolatedA, environment: "ModuleTest", hooks: isolatedHooks);
        await using var isolatedServiceB = Service(isolatedB, environment: "Development", hooks: isolatedHooks);
        var isolatedResults = await Task.WhenAll(isolatedServiceA.StageAsync(isolatedInputA), isolatedServiceB.StageAsync(isolatedInputB));
        Assert(isolatedResults.All(result => result.Succeeded), "different roots and environments do not share local publication gates");
    }

    private static async Task ExistingStagingTamperingAsync(string root)
    {
        await TamperMetadata(root, "metadata-schema", node => node["schemaVersion"] = 99);
        await TamperMetadata(root, "metadata-module", node => node["moduleId"] = "qing.other");
        await TamperMetadata(root, "metadata-version", node => node["version"] = "9.9.9");
        await TamperMetadata(root, "metadata-api", node => node["moduleApiVersion"] = "wrong");
        await TamperMetadata(root, "metadata-package-hash", node => node["packageSha256"] = new string('f', 64));
        await TamperMetadata(root, "metadata-size", node => node["packageSize"] = 1);
        await TamperMetadata(root, "metadata-source", node => node["sourceIdentity"] = "wrong");
        await TamperMetadata(root, "metadata-environment", node => node["environmentIdentity"] = "Development");
        await TamperMetadata(root, "metadata-release-identity", node => node["officialReleaseIdentityHash"] = new string('0', 64));
        await TamperMetadata(root, "metadata-count", node => node["fileCount"] = 99);
        await TamperMetadata(root, "metadata-total", node => node["totalUncompressedBytes"] = 1);
        await TamperMetadata(root, "metadata-file-sha", node => node["files"]![0]!["sha256"] = new string('0', 64));
        await TamperMetadata(root, "metadata-file-size", node => node["files"]![0]!["size"] = 1);
        await TamperMetadata(root, "metadata-file-unknown", node => node["files"]![0]!["unknown"] = true);
        await TamperMetadata(root, "metadata-file-invalid-sha", node => node["files"]![0]!["sha256"] = "bad");
        await TamperMetadata(root, "metadata-empty-source", node => node["sourceIdentity"] = "");
        await TamperMetadata(root, "metadata-wrong-type", node => node["fileCount"] = "four");
        await TamperMetadata(root, "metadata-unsafe-path", node => node["files"]![0]!["relativePath"] = "a/../b");
        await TamperMetadata(root, "metadata-case-collision", node =>
            node["files"]![1]!["relativePath"] = node["files"]![0]!["relativePath"]!.GetValue<string>().ToUpperInvariant());
        await TamperMetadata(root, "metadata-unknown", node => node["unknown"] = true);
        await TamperMetadata(root, "metadata-invalid-time", node => node["stagedAtUtc"] = "not-a-time");
        await TamperMetadata(root, "metadata-duplicate-path", node =>
            node["files"]!.AsArray().Add(node["files"]![0]!.DeepClone()));
        await TamperRawMetadata(root, "metadata-duplicate-property", text => text.Replace("{", "{\"schemaVersion\":1,", StringComparison.Ordinal));
        await TamperRawMetadata(root, "metadata-file-duplicate-property", text => text.Replace("\"size\":", "\"size\":1,\"size\":", StringComparison.Ordinal));
        await TamperRawMetadata(root, "metadata-bom", text => "\uFEFF" + text);
        await TamperRawMetadata(root, "metadata-trailing-garbage", text => text + "x");
        await TamperRawMetadata(root, "metadata-trailing-comma", text => text[..^1] + ",}");
        await TamperRawMetadata(root, "metadata-comment", text => "/*invalid*/" + text);

        await TamperTree(root, "tree-extra-file", (final, _) => File.WriteAllText(Path.Combine(final, "extra.txt"), "x"));
        await TamperTree(root, "tree-extra-directory", (final, _) => Directory.CreateDirectory(Path.Combine(final, "empty")));
        await TamperTree(root, "tree-delete-file", (final, _) => File.Delete(Path.Combine(final, "payload.dll")));
        await TamperTree(root, "tree-replace-file", (final, _) => File.WriteAllText(Path.Combine(final, "payload.dll"), "changed"));
        await TamperTree(root, "tree-directory-to-file", (final, _) =>
        {
            Directory.Delete(Path.Combine(final, "i18n"), true);
            File.WriteAllText(Path.Combine(final, "i18n"), "not-a-directory");
        });
        await TamperTree(root, "tree-file-to-directory", (final, _) =>
        {
            File.Delete(Path.Combine(final, "payload.dll"));
            Directory.CreateDirectory(Path.Combine(final, "payload.dll"));
        });
        await ExistingTreeReparseAsync(root);
    }

    private static async Task CrossProcessPublicationAsync(string root)
    {
        var conflictRoot = Case(root, "real-process-conflict");
        var first = CreatePackage(conflictRoot, entries => ReplaceResource(entries, "i18n/en-US.json", [71]), "first.qmod");
        var second = CreatePackage(conflictRoot, entries => ReplaceResource(entries, "i18n/en-US.json", [72]), "second.qmod");
        var start = Path.Combine(conflictRoot, "start.signal");
        var firstResult = Path.Combine(conflictRoot, "first.result");
        var secondResult = Path.Combine(conflictRoot, "second.result");
        using var firstWorker = StartWorker("--worker-stage", conflictRoot, first.VerifiedPackage.FilePath,
            ModuleId, Version, start, Path.Combine(conflictRoot, "first.signal"), firstResult);
        using var secondWorker = StartWorker("--worker-stage", conflictRoot, second.VerifiedPackage.FilePath,
            ModuleId, Version, start, Path.Combine(conflictRoot, "second.signal"), secondResult);
        await WaitForFileAsync(Path.Combine(conflictRoot, "first.signal"), TimeSpan.FromSeconds(20));
        await WaitForFileAsync(Path.Combine(conflictRoot, "second.signal"), TimeSpan.FromSeconds(20));
        await File.WriteAllTextAsync(start, "go");
        await WaitForExitAsync(firstWorker, TimeSpan.FromSeconds(30));
        await WaitForExitAsync(secondWorker, TimeSpan.FromSeconds(30));
        Assert(firstWorker.ExitCode == 0 && secondWorker.ExitCode == 0, "independent staging workers exit normally");
        var results = new[] { await File.ReadAllTextAsync(firstResult), await File.ReadAllTextAsync(secondResult) };
        Assert(results.Count(value => value.Contains("success=True", StringComparison.Ordinal)) == 1 &&
               results.Count(value => value.Contains("failure=StagingConflict", StringComparison.Ordinal)) == 1,
            "independent processes publish exactly one SHA");
        var versionRoot = Path.Combine(conflictRoot, "Staging", "Verified", ModuleId, Version);
        Assert(Directory.EnumerateDirectories(versionRoot).Count() == 1, "process conflict leaves one SHA directory");
        var winner = results[0].Contains("success=True", StringComparison.Ordinal) ? first : second;
        await using (var reuse = Service(conflictRoot))
            Assert((await reuse.StageAsync(winner)).Reused, "cross-process winner remains strictly reusable");
        AssertNoPartial(conflictRoot, "process conflict"); AssertNoLocks(conflictRoot, "process conflict");

        var crashRoot = Case(root, "process-crash-recovery"); var crashInput = CreatePackage(crashRoot);
        var crashGate = Path.Combine(crashRoot, "start.signal"); await File.WriteAllTextAsync(crashGate, "go");
        var held = Path.Combine(crashRoot, "held.signal");
        using (var crashing = StartWorker("--worker-hold", crashRoot, crashInput.VerifiedPackage.FilePath,
            ModuleId, Version, crashGate, held, Path.Combine(crashRoot, "crash.result")))
        {
            await WaitForFileAsync(held, TimeSpan.FromSeconds(20));
            crashing.Kill(entireProcessTree: true);
            await WaitForExitAsync(crashing, TimeSpan.FromSeconds(20));
        }
        var recoveryResult = Path.Combine(crashRoot, "recovery.result");
        using (var recovery = StartWorker("--worker-stage", crashRoot, crashInput.VerifiedPackage.FilePath,
            ModuleId, Version, crashGate, Path.Combine(crashRoot, "recovery.signal"), recoveryResult))
        {
            await WaitForExitAsync(recovery, TimeSpan.FromSeconds(30));
            var recovered = await File.ReadAllTextAsync(recoveryResult);
            Assert(recovery.ExitCode == 0 && recovered.Contains("success=True", StringComparison.Ordinal) &&
                   recovered.Contains("crashRecovery=True", StringComparison.Ordinal),
                "new process recovers lock after holder crash");
        }
        AssertNoLocks(crashRoot, "crash recovery"); AssertNoPartial(crashRoot, "crash recovery");

        var cancellationRoot = Case(root, "process-lock-cancellation"); var cancellationInput = CreatePackage(cancellationRoot);
        var cancellationGate = Path.Combine(cancellationRoot, "start.signal"); await File.WriteAllTextAsync(cancellationGate, "go");
        var cancellationHeld = Path.Combine(cancellationRoot, "held.signal");
        using var holder = StartWorker("--worker-hold", cancellationRoot, cancellationInput.VerifiedPackage.FilePath,
            ModuleId, Version, cancellationGate, cancellationHeld, Path.Combine(cancellationRoot, "holder.result"));
        await WaitForFileAsync(cancellationHeld, TimeSpan.FromSeconds(20));
        await using (var waiter = Service(cancellationRoot, maximumParallelism: 1))
        {
            var blocked = waiter.StageAsync(cancellationInput);
            await Task.Delay(100);
            var otherModule = CreatePackage(cancellationRoot, fileName: "other.qmod", moduleId: "qing.capacity-probe");
            using var capacityTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert((await waiter.StageAsync(otherModule, capacityTimeout.Token)).Succeeded,
                "lock waiter does not consume unrelated extraction capacity");
            waiter.Cancel(cancellationInput);
            Assert((await blocked).FailureCode == QmodStagingFailureCode.Cancelled,
                "current process cancels while independent process retains lock");
        }
        Assert(!holder.HasExited, "cancelled waiter does not release worker ownership");
        holder.Kill(entireProcessTree: true); await WaitForExitAsync(holder, TimeSpan.FromSeconds(20));
        await using (var afterCancellation = Service(cancellationRoot))
            Assert((await afterCancellation.StageAsync(cancellationInput)).Succeeded,
                "next transaction acquires lock after holder exits");
        AssertNoLocks(cancellationRoot, "process cancellation");
    }

    private static async Task FailedPublicationCommitAsync(string root)
    {
        var caseRoot = Case(root, "failed-candidate-attestation"); var input = CreatePackage(caseRoot);
        var userModules = Path.Combine(caseRoot, "UserModules"); Directory.CreateDirectory(userModules);
        var sentinel = Path.Combine(userModules, "keep.txt"); await File.WriteAllTextAsync(sentinel, "keep");
        var events = new List<QmodStagingLogEvent>();
        var hooks = new QmodStagingTestHooks(CandidateAttestationStarting: async (_, candidate, token) =>
            await File.WriteAllTextAsync(Path.Combine(candidate, QmodPackageStagingService.StagingMetadataName),
                "{\"schemaVersion\":999}", token));
        await using (var service = Service(caseRoot, log: events.Add, hooks: hooks))
        {
            var result = await service.StageAsync(input);
            Assert(result.FailureCode == QmodStagingFailureCode.StagingMetadataInvalid,
                "candidate attestation failure is structured");
            Assert(events.All(item => item.EventName != "Verified directory published"),
                "failed candidate never logs successful publication");
        }
        var final = Path.Combine(caseRoot, "Staging", "Verified", ModuleId, Version, input.ReleaseIdentity.Sha256);
        Assert(!Directory.Exists(final), "candidate failure never reaches the Verified namespace");
        AssertNoPartial(caseRoot, "candidate attestation failure");
        await using (var retry = Service(caseRoot)) Assert((await retry.StageAsync(input)).Succeeded,
            "valid retry succeeds after candidate failure");
        Assert(await File.ReadAllTextAsync(sentinel) == "keep", "candidate failure never modifies UserModules");

        var moveRoot = Case(root, "failed-publication-move"); var moveInput = CreatePackage(moveRoot);
        var moveHooks = new QmodStagingTestHooks(PublicationMove: (_, _) =>
            Task.FromException(new IOException("Injected atomic move failure.")));
        await using (var service = Service(moveRoot, hooks: moveHooks))
            Assert((await service.StageAsync(moveInput)).FailureCode == QmodStagingFailureCode.IoFailure,
                "ordinary move IO failure is not misreported as a conflict");
        Assert(!Directory.Exists(Path.Combine(moveRoot, "Staging", "Verified", ModuleId, Version,
            moveInput.ReleaseIdentity.Sha256)), "failed move produces no final directory");
        AssertNoPartial(moveRoot, "move failure");

        var cleanupRoot = Case(root, "lock-marker-cleanup"); var cleanupInput = CreatePackage(cleanupRoot);
        var cleanupEvents = new List<QmodStagingLogEvent>();
        var cleanupHooks = new QmodStagingTestHooks(PublicationLockMarkerCleanup: () =>
            throw new IOException("Injected marker cleanup failure."));
        await using (var service = Service(cleanupRoot, log: cleanupEvents.Add, hooks: cleanupHooks))
            Assert((await service.StageAsync(cleanupInput)).Succeeded,
                "marker cleanup failure cannot rewrite committed success");
        Assert(cleanupEvents.Count(item => item.EventName == "Verified directory published") == 1,
            "committed publication is logged exactly once");
        Assert(cleanupEvents.Any(item => item.EventName == "Publication lock marker cleanup failed"),
            "marker cleanup failure emits a safe warning");
        AssertNoLocks(cleanupRoot, "marker cleanup failure", requireCleanMarker: false);
        await using (var reuse = Service(cleanupRoot))
        {
            var reused = await reuse.StageAsync(cleanupInput);
            Assert(reused.Succeeded && reused.Reused,
                "new service reacquires the released lock and strictly reuses final");
        }
        AssertNoPartial(cleanupRoot, "marker cleanup failure");

        var cancelRoot = Case(root, "post-commit-cancellation"); var cancelInput = CreatePackage(cancelRoot);
        using var callerCancellation = new CancellationTokenSource();
        var cancelHooks = new QmodStagingTestHooks(PublicationMoveCompleted: _ =>
        {
            callerCancellation.Cancel();
            return Task.CompletedTask;
        });
        await using (var service = Service(cancelRoot, hooks: cancelHooks))
            Assert((await service.StageAsync(cancelInput, callerCancellation.Token)).Succeeded,
                "caller cancellation after atomic move cannot rewrite committed success");

        var disposeRoot = Case(root, "post-commit-dispose"); var disposeInput = CreatePackage(disposeRoot);
        QmodPackageStagingService? disposeService = null; Task? disposal = null;
        var disposeHooks = new QmodStagingTestHooks(PublicationMoveCompleted: _ =>
        {
            disposal = disposeService!.DisposeAsync().AsTask();
            return Task.CompletedTask;
        });
        disposeService = Service(disposeRoot, hooks: disposeHooks);
        Assert((await disposeService.StageAsync(disposeInput)).Succeeded,
            "service disposal after atomic move cannot rewrite committed success");
        await (disposal ?? throw new Exception("post-commit disposal was not started"));

        var diagnosticRoot = Case(root, "post-commit-diagnostic"); var diagnosticInput = CreatePackage(diagnosticRoot);
        var diagnosticHooks = new QmodStagingTestHooks(PublicationMoveCompleted: _ =>
            Task.FromException(new IOException("Injected post-commit diagnostic failure.")));
        await using (var service = Service(diagnosticRoot,
            log: item => { if (item.EventName == "Verified directory published") throw new IOException(); },
            hooks: diagnosticHooks))
            Assert((await service.StageAsync(diagnosticInput)).Succeeded,
                "post-commit hooks and logging cannot rewrite committed success");
    }

    private static async Task ServiceDisposalAsync(string root)
    {
        var caseRoot = Case(root, "service-disposal"); var input = CreatePackage(caseRoot);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new QmodStagingTestHooks(PublicationLockAcquired: async (_, token) =>
        {
            reached.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var service = Service(caseRoot, hooks: hooks);
        var operation = service.StageAsync(input);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var firstDispose = service.DisposeAsync().AsTask();
        var secondDispose = service.DisposeAsync().AsTask();
        await Task.WhenAll(firstDispose, secondDispose);
        Assert((await operation).FailureCode == QmodStagingFailureCode.Cancelled,
            "dispose cancels and awaits existing staging operation");
        try { await service.StageAsync(input); throw new Exception("disposed service accepted StageAsync"); }
        catch (ObjectDisposedException) { }
        service.Cancel(input);
        AssertNoPartial(caseRoot, "service disposal"); AssertNoLocks(caseRoot, "service disposal");
    }

    private static Process StartWorker(string mode, string root, string package, string moduleId, string version,
        string gate, string signal, string result)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Worker executable unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(StagingSmoke).Assembly.Location);
        foreach (var argument in new[] { mode, root, package, moduleId, version, gate, signal, result })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start staging worker.");
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cancellation.Token);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path)) await Task.Delay(25, cancellation.Token);
    }

    private static async Task ExistingTreeReparseAsync(string root)
    {
        var caseRoot = Case(root, "existing-tree-reparse"); var input = CreatePackage(caseRoot);
        await using var service = Service(caseRoot);
        var first = await service.StageAsync(input);
        Assert(first.Succeeded, "existing tree reparse setup staged");
        var external = Path.Combine(caseRoot, "external"); Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "must-survive.txt"); await File.WriteAllTextAsync(sentinel, "keep");
        var nested = Path.Combine(first.StagingDirectory!, "i18n"); Directory.Delete(nested, true);
        try { Directory.CreateSymbolicLink(nested, external); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Directory.CreateDirectory(nested);
            Console.WriteLine("Existing staging reparse check skipped because symbolic-link creation is unavailable.");
            return;
        }
        var result = await service.StageAsync(input);
        Assert(result.FailureCode == QmodStagingFailureCode.VerifiedStagingInvalid, "existing tree reparse rejected");
        Assert(await File.ReadAllTextAsync(sentinel) == "keep", "existing tree reparse target is not read or deleted");
        Directory.Delete(nested, false);
    }

    private static async Task LockCancellationAsync(string root)
    {
        var caseRoot = Case(root, "lock-cancellation");
        var holderInput = CreatePackage(caseRoot, entries => ReplaceResource(entries, "i18n/en-US.json", [31]), "holder.qmod");
        var waiterInput = CreatePackage(caseRoot, entries => ReplaceResource(entries, "i18n/en-US.json", [32]), "waiter.qmod");
        using var holderReached = new ManualResetEventSlim(false);
        using var releaseHolder = new ManualResetEventSlim(false);
        void Hold(QmodStagingLogEvent item)
        {
            if (item.EventName != "Archive metadata accepted") return;
            holderReached.Set();
            Assert(releaseHolder.Wait(TimeSpan.FromSeconds(15)), "holder released before lock test timeout");
        }
        await using var holder = Service(caseRoot, log: Hold);
        await using var waiter = Service(caseRoot);
        var holderTask = holder.StageAsync(holderInput);
        Assert(holderReached.Wait(TimeSpan.FromSeconds(10)), "holder acquires publication transaction");
        using (var caller = new CancellationTokenSource())
        {
            var cancelledWait = waiter.StageAsync(waiterInput, caller.Token);
            caller.Cancel();
            try { await cancelledWait; throw new Exception("publication-lock caller cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        var survivingWait = waiter.StageAsync(waiterInput);
        releaseHolder.Set();
        var holderResult = await holderTask;
        var waiterResult = await survivingWait;
        Assert(holderResult.Succeeded && waiterResult.FailureCode == QmodStagingFailureCode.StagingConflict,
            $"caller cancellation leaves shared lock transaction intact ({holderResult.FailureCode}/{waiterResult.FailureCode})");
        AssertNoPartial(caseRoot, "caller lock cancellation");
        AssertNoLocks(caseRoot, "caller lock cancellation");

        var explicitRoot = Case(root, "explicit-lock-cancellation");
        holderInput = CreatePackage(explicitRoot, entries => ReplaceResource(entries, "i18n/en-US.json", [41]), "holder.qmod");
        waiterInput = CreatePackage(explicitRoot, entries => ReplaceResource(entries, "i18n/en-US.json", [42]), "waiter.qmod");
        using var explicitReached = new ManualResetEventSlim(false);
        using var explicitRelease = new ManualResetEventSlim(false);
        void ExplicitHold(QmodStagingLogEvent item)
        {
            if (item.EventName != "Archive metadata accepted") return;
            explicitReached.Set(); explicitRelease.Wait(TimeSpan.FromSeconds(15));
        }
        await using var explicitHolder = Service(explicitRoot, log: ExplicitHold);
        await using var explicitWaiter = Service(explicitRoot);
        var explicitHolderTask = explicitHolder.StageAsync(holderInput);
        Assert(explicitReached.Wait(TimeSpan.FromSeconds(10)), "explicit cancellation holder acquired");
        var explicitlyCancelled = explicitWaiter.StageAsync(waiterInput);
        explicitWaiter.Cancel(waiterInput);
        Assert((await explicitlyCancelled).FailureCode == QmodStagingFailureCode.Cancelled,
            "full operation identity cancels transaction waiting for publication lock");
        explicitRelease.Set(); Assert((await explicitHolderTask).Succeeded, "other module transaction survives cancellation");
        AssertNoPartial(explicitRoot, "explicit lock cancellation");
        AssertNoLocks(explicitRoot, "explicit lock cancellation");

        var externalLockRoot = Case(root, "external-lock-cancellation");
        var externalInput = CreatePackage(externalLockRoot);
        await using (var blockedService = Service(externalLockRoot))
        {
            var externalPath = blockedService.GetPublicationLockPathForTest(ModuleId, Version);
            Directory.CreateDirectory(Path.GetDirectoryName(externalPath)!);
            using (var externalHandle = new FileStream(externalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var blocked = blockedService.StageAsync(externalInput);
                await Task.Delay(100);
                blockedService.Cancel(externalInput);
                Assert((await blocked).FailureCode == QmodStagingFailureCode.Cancelled,
                    "cross-process file lock wait observes transaction cancellation");
            }
            Assert((await blockedService.StageAsync(externalInput)).Succeeded,
                "cross-process file lock is acquirable after cancelled waiter and owner release");
        }
        AssertNoLocks(externalLockRoot, "external lock cancellation");
    }

    private static async Task TamperMetadata(string root, string name, Action<JsonObject> mutate)
    {
        await TamperExisting(root, name, (final, metadata) =>
        {
            var node = JsonNode.Parse(File.ReadAllText(metadata))!.AsObject();
            mutate(node); File.WriteAllText(metadata, node.ToJsonString());
        }, QmodStagingFailureCode.StagingMetadataInvalid);
    }

    private static Task TamperRawMetadata(string root, string name, Func<string, string> mutate) =>
        TamperExisting(root, name, (_, metadata) => File.WriteAllText(metadata, mutate(File.ReadAllText(metadata))),
            QmodStagingFailureCode.StagingMetadataInvalid);

    private static Task TamperTree(string root, string name, Action<string, string> mutate) =>
        TamperExisting(root, name, mutate, QmodStagingFailureCode.VerifiedStagingInvalid);

    private static async Task TamperExisting(string root, string name, Action<string, string> mutate,
        QmodStagingFailureCode expected)
    {
        var caseRoot = Case(root, name); var input = CreatePackage(caseRoot);
        await using var service = Service(caseRoot);
        var first = await service.StageAsync(input);
        Assert(first.Succeeded, $"{name} setup staged ({first.FailureCode})");
        var metadata = Path.Combine(first.StagingDirectory!, QmodPackageStagingService.StagingMetadataName);
        mutate(first.StagingDirectory!, metadata);
        var result = await service.StageAsync(input);
        Assert(result.FailureCode == expected, $"{name}: expected {expected}, got {result.FailureCode}");
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
        Action<QmodStagingLogEvent>? log = null, string environment = "ModuleTest", int maximumParallelism = 2,
        QmodStagingTestHooks? hooks = null) =>
        new(Path.Combine(root, "Staging"), TimeProvider.System, environment,
            Path.Combine(root, "UserModules"), limits, log, maximumParallelism, hooks);

    private static QmodStagingInput CreatePackage(string root, Func<List<EntrySpec>, List<EntrySpec>>? mutate = null,
        string fileName = "probe.qmod", string moduleId = ModuleId, string version = Version)
    {
        Directory.CreateDirectory(root);
        var entries = new List<EntrySpec>
        {
            new("qmod.json", QmodJson(moduleId: moduleId, version: version)),
            new("module.json", ModuleJson(moduleId: moduleId, version: version)),
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
        var semanticVersion = SemanticVersion.Parse(version);
        var verified = new VerifiedModulePackage(moduleId, semanticVersion, fileName, path, bytes.LongLength, hash, DateTimeOffset.UtcNow);
        var identity = new ModulePackageDownloadIdentity(moduleId, "0.1.0", version, fileName,
            $"https://github.com/QingMo-A/QingToolbox/releases/download/v{version}/{fileName}", bytes.LongLength, hash);
        return new(verified, identity, ModuleUpdateIdentity.ModuleApiVersion, "qingtoolbox-official");
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
    private static void AssertNoLocks(string root, string name, bool requireCleanMarker = true)
    {
        var locks = Path.Combine(root, "Staging", "Locks");
        if (!Directory.Exists(locks)) return;
        foreach (var path in Directory.EnumerateFiles(locks, "*.lock", SearchOption.AllDirectories))
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                if (requireCleanMarker) Assert(stream.Length == 0, name + " leaves no active lock owner marker");
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
