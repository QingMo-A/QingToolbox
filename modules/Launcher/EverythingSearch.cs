using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QingToolbox.Modules.Launcher;

public enum EverythingSearchMode
{
    All,
    File,
    Directory,
}

public sealed record EverythingPathResult(string Path, bool IsDirectory);

public interface IEverythingSearchService : IAsyncDisposable
{
    Task<IReadOnlyList<EverythingPathResult>> SearchAsync(
        EverythingSearchMode mode,
        string query,
        CancellationToken cancellationToken = default);
}

internal sealed class EverythingRuntime : IEverythingSearchService
{
    internal const string RuntimeVersion = "1.4.1.1032";
    internal const string CliVersion = "1.1.0.37";
    private const string ServiceInstance = "QingToolboxLauncher";
    private const string ServicePipe = @"\\.\PIPE\QingToolboxLauncherEverythingService";
    private const string ServiceSecurityDescriptor = "D:(A;OICI;GRGW;;;AU)";
    private const string EverythingExecutableSha256 = "F191F756996A14A11E5445FA7103D302EFD510CF2FBF920E6C0C8ED51D512E36";
    private readonly string _runtimeDirectory;
    private readonly string _dataDirectory;
    private readonly string _clientRuntimeDirectory;
    private readonly IReadOnlyList<string>? _indexRoots;
    // Everything 1.4 named IPC identifiers are intentionally short. Long
    // names can be truncated differently by the client and ES.
    private readonly string _instanceName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _ownedProcess;
    private bool _disposed;

    public EverythingRuntime(string moduleDirectory, string dataDirectory, IReadOnlyList<string>? indexRoots = null)
    {
        _runtimeDirectory = Path.Combine(moduleDirectory, "third-party", "Everything");
        _dataDirectory = Path.Combine(dataDirectory, "everything-runtime");
        _clientRuntimeDirectory = Path.Combine(_dataDirectory, "client");
        _instanceName = "QL" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(_dataDirectory).ToUpperInvariant())))[..8];
        _indexRoots = indexRoots;
    }

    public async Task<IReadOnlyList<EverythingPathResult>> SearchAsync(
        EverythingSearchMode mode,
        string query,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            var constrainedQuery = mode switch
            {
                EverythingSearchMode.File => $"file:{query}",
                EverythingSearchMode.Directory => $"folder:{query}",
                _ => query,
            };
            var output = await RunQueryWithRetryAsync([
                "-instance", _instanceName,
                "-n", "20",
                "-timeout", "15000",
                "-utf8-bom",
                "-no-header",
                "-full-path-and-name",
                constrainedQuery,
            ], cancellationToken).ConfigureAwait(false);
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.TrimStart('\uFEFF'))
                .Where(Path.IsPathFullyQualified)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .Select(path => new EverythingPathResult(path, Directory.Exists(path)))
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    internal async Task WarmUpAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await EnsureReadyAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException) { }
        finally { _gate.Release(); }
    }

    private async Task<string> RunQueryWithRetryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await RunCliAsync(arguments, cancellationToken).ConfigureAwait(false); }
            catch (InvalidOperationException) when (_ownedProcess is { HasExited: false } && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        var everything = Path.Combine(_runtimeDirectory, "Everything.exe");
        var es = Path.Combine(_runtimeDirectory, "es.exe");
        var license = Path.Combine(_runtimeDirectory, "LICENSE.txt");
        if (!File.Exists(everything) || !File.Exists(es) || !File.Exists(license))
            throw new InvalidOperationException("The built-in Everything runtime is unavailable.");

        if (_ownedProcess is null || _ownedProcess.HasExited)
        {
            Directory.CreateDirectory(_dataDirectory);
            var useService = _indexRoots is null;
            if (useService) await EnsureServiceAsync(cancellationToken).ConfigureAwait(false);
            if (useService)
            {
                Directory.CreateDirectory(_clientRuntimeDirectory);
                foreach (var fileName in new[] { "Everything.exe", "es.exe", "Everything64.dll", "LICENSE.txt", "NOTICE.md" })
                    File.Copy(Path.Combine(_runtimeDirectory, fileName), Path.Combine(_clientRuntimeDirectory, fileName), overwrite: true);
                everything = Path.Combine(_clientRuntimeDirectory, "Everything.exe");
                es = Path.Combine(_clientRuntimeDirectory, "es.exe");
            }
            var config = useService
                ? Path.Combine(_clientRuntimeDirectory, $"Everything-{_instanceName}.ini")
                : Path.Combine(_dataDirectory, $"Everything-{_instanceName}.ini");
            var roots = (_indexRoots ?? [])
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!useService && roots.Length == 0) throw new InvalidOperationException("The built-in Everything runtime has no folders to index.");
            var values = string.Join(',', roots);
            var perRoot = string.Join(',', Enumerable.Repeat("1", roots.Length));
            var zeroPerRoot = string.Join(',', Enumerable.Repeat("0", roots.Length));
            var buffers = string.Join(',', Enumerable.Repeat("65536", roots.Length));
            await File.WriteAllTextAsync(config,
                "[Everything]\r\n" +
                $"app_data=0\r\nrun_as_admin=0\r\nservice={(useService ? 1 : 0)}\r\nindex_as_admin=0\r\n" +
                "show_tray_icon=0\r\nrun_in_background=1\r\nshow_window_on_startup=0\r\n" +
                "check_for_updates=0\r\ncheck_for_beta_updates=0\r\n" +
                $"service_pipe_name={(useService ? ServicePipe : string.Empty)}\r\n" +
                $"auto_include_fixed_volumes={(useService ? 1 : 0)}\r\nauto_include_removable_volumes=0\r\n" +
                $"db_location={_dataDirectory}\r\n" +
                $"folders={values}\r\nfolder_monitor_changes={perRoot}\r\nfolder_buffer_size_list={buffers}\r\n" +
                $"folder_rescan_if_full_list={perRoot}\r\nfolder_update_types={zeroPerRoot}\r\n" +
                $"folder_update_days={zeroPerRoot}\r\nfolder_update_ats={zeroPerRoot}\r\n" +
                $"folder_update_intervals={zeroPerRoot}\r\nfolder_update_interval_types={zeroPerRoot}\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            var start = HiddenProcess(everything);
            start.ArgumentList.Add("-instance"); start.ArgumentList.Add(_instanceName);
            start.ArgumentList.Add("-startup");
            start.ArgumentList.Add("-config"); start.ArgumentList.Add(config);
            _ownedProcess = Process.Start(start) ?? throw new InvalidOperationException("The built-in Everything runtime could not start.");
            if (useService)
            {
                // Everything 1.4 exits immediately when a named client is first
                // created with -service-pipe-name. Create the client first, then
                // send the service connection option to that running instance.
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                var connect = HiddenProcess(everything);
                connect.ArgumentList.Add("-instance"); connect.ArgumentList.Add(_instanceName);
                connect.ArgumentList.Add("-service-pipe-name"); connect.ArgumentList.Add(ServicePipe);
                using var request = Process.Start(connect) ?? throw new InvalidOperationException("The Everything service connection could not start.");
                await request.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (request.ExitCode != 0) throw new InvalidOperationException("The Everything service connection failed.");
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = await RunCliAsync(["-instance", _instanceName, "-get-everything-version"], cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException) when (!_ownedProcess.HasExited)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException("The built-in Everything index did not become ready.");
    }

    private async Task EnsureServiceAsync(CancellationToken cancellationToken)
    {
        var marker = Path.Combine(_dataDirectory, "service-installed-1.4.1.1032");
        var serviceDirectory = Path.Combine(_dataDirectory, "service");
        Directory.CreateDirectory(serviceDirectory);
        var source = Path.Combine(_runtimeDirectory, "Everything.exe");
        var serviceExecutable = Path.Combine(serviceDirectory, "Everything.exe");
        if (!File.Exists(serviceExecutable) ||
            !Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(serviceExecutable, cancellationToken).ConfigureAwait(false)))
                .Equals(EverythingExecutableSha256, StringComparison.Ordinal))
            File.Copy(source, serviceExecutable, overwrite: true);

        var expectedMarker = $"{RuntimeVersion}\n{Path.GetFullPath(serviceExecutable)}\n{ServicePipe}\n{ServiceSecurityDescriptor}";
        if (File.Exists(marker) &&
            (await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false))
                .Equals(expectedMarker, StringComparison.OrdinalIgnoreCase))
            return;

        var install = new ProcessStartInfo
        {
            FileName = serviceExecutable,
            WorkingDirectory = serviceDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        install.ArgumentList.Add("-instance"); install.ArgumentList.Add(ServiceInstance);
        install.ArgumentList.Add("-install-service");
        install.ArgumentList.Add("-install-service-pipe-name"); install.ArgumentList.Add(ServicePipe);
        install.ArgumentList.Add("-install-service-security-descriptor"); install.ArgumentList.Add(ServiceSecurityDescriptor);
        try
        {
            using var process = Process.Start(install) ?? throw new InvalidOperationException("The Everything service authorization could not start.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("The Everything service could not be installed.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("The Everything service authorization was cancelled.", exception);
        }
        await File.WriteAllTextAsync(marker, expectedMarker, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> RunCliAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = HiddenProcess(Path.Combine(_runtimeDirectory, "es.exe"));
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Everything IPC could not start.");
        string output;
        string error;
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            output = await outputTask.ConfigureAwait(false);
            error = await errorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        if (process.ExitCode == 8)
            throw new InvalidOperationException("indexing");
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Everything IPC is unavailable (code {process.ExitCode})."
                : $"Everything IPC query failed (code {process.ExitCode}).");
        return output;
    }

    private static ProcessStartInfo HiddenProcess(string fileName) => new()
    {
        FileName = fileName,
        WorkingDirectory = Path.GetDirectoryName(fileName) ?? string.Empty,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ownedProcess is not null && !_ownedProcess.HasExited)
            {
                try
                {
                    var exit = HiddenProcess(Path.Combine(_runtimeDirectory, "Everything.exe"));
                    exit.ArgumentList.Add("-instance"); exit.ArgumentList.Add(_instanceName);
                    exit.ArgumentList.Add("-exit");
                    using var request = Process.Start(exit);
                    if (request is not null) await request.WaitForExitAsync().ConfigureAwait(false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _ownedProcess.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { try { _ownedProcess.Kill(true); } catch { } }
                catch (InvalidOperationException) { }
            }
            _ownedProcess?.Dispose();
            _ownedProcess = null;
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
