using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Reflection;
using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Updates;
using QingToolbox.ModuleLoader;

namespace QingToolbox.ModuleHost;

internal static class Program
{
    private const int ProtocolVersion = 1;
    private const int MaximumMessageCharacters = 16 * 1024;

    [STAThread]
    private static int Main(string[] args)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;
        application.Startup += async (_, _) =>
        {
            try { await RunAsync(args); exitCode = 0; }
            catch { exitCode = 2; }
            finally { application.Shutdown(exitCode); }
        };
        application.Run();
        return exitCode;
    }

    private static async Task RunAsync(string[] args)
    {
        var options = Options.Parse(args);
        await using var pipe = new NamedPipeClientStream(".", options.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        var manifest = await new ModuleManifestReader().ReadAsync(Path.Combine(options.ModuleDirectory, "module.json"))
            ?? throw new InvalidDataException("Manifest unavailable.");
        if (manifest.Id != options.ModuleId || manifest.Version != options.ManifestVersion ||
            ModuleRuntimeCapabilities.Resolve(manifest) is not
                { RuntimeIsolation: ModuleRuntimeIsolation.OutOfProcess, UiKind: ModuleUiKind.Wpf })
            throw new InvalidDataException("Manifest identity mismatch.");
        var files = await SnapshotAsync(options.ModuleDirectory);
        var treeIdentity = ModuleProgramRuntimeIdentityHash.Compute(files);
        if (treeIdentity != options.ProgramTreeIdentity) throw new InvalidDataException("Program identity mismatch.");

        var discovered = new DiscoveredModule { Manifest = manifest, ModuleDirectory = options.ModuleDirectory,
            ManifestPath = Path.Combine(options.ModuleDirectory, "module.json"), State = ModuleState.NotLoaded, Errors = [] };
        await using var handle = await new InProcessModuleLoader(new PassthroughLocalization())
            .LoadAsync(discovered, options.DataRoot);
        var viewFactory = handle.ViewFactory
            ?? throw new InvalidDataException("The WPF module does not expose a view factory.");
        var variant = handle.Module.GetType().Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "QingToolbox.TextToolsCanary.Variant")?.Value;
        await SendAsync(writer, new Message(ProtocolVersion, "Hello", options.Nonce, manifest.Id, manifest.Version,
            options.ModuleApiVersion, treeIdentity, Environment.ProcessId, false, false, variant, null, false));
        if (options.TestExitAfterHello) return;

        Window? window = null;
        WindowSnapshot? suspendedWindow = null;
        var active = false;
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            if (line.Length > MaximumMessageCharacters) throw new InvalidDataException("IPC message too large.");
            var request = JsonSerializer.Deserialize<Message>(line) ?? throw new InvalidDataException("Invalid IPC message.");
            if (request.ProtocolVersion != ProtocolVersion || request.Nonce != options.Nonce ||
                request.ModuleId != options.ModuleId)
                throw new UnauthorizedAccessException("IPC identity rejected.");
            switch (request.Type)
            {
                case "GetState": break;
                case "Activate": if (!active) { await handle.Module.OnActivateAsync(); active = true; } break;
                case "Deactivate": if (active) { await handle.Module.OnDeactivateAsync(); active = false; } break;
                case "OpenWindow":
                    window ??= CreateWindow(viewFactory, manifest.Name, () => window = null);
                    window.Show(); window.Activate(); break;
                case "CloseWindow": window?.Close(); window = null; break;
                case "SuspendWindow":
                    if (window is not null && suspendedWindow is null)
                    {
                        suspendedWindow = new(window.IsVisible, window.WindowState, window.IsActive);
                        if (window.IsVisible) window.Hide();
                    }
                    break;
                case "RestoreWindow":
                    if (window is not null && suspendedWindow is { } snapshot)
                    {
                        if (snapshot.WasVisible)
                        {
                            window.Show();
                            window.WindowState = snapshot.State;
                            if (snapshot.WasActive) window.Activate();
                        }
                        suspendedWindow = null;
                    }
                    break;
                case "Shutdown": window?.Close(); if (active) await handle.Module.OnDeactivateAsync(); return;
                default: throw new InvalidDataException("Unknown IPC command.");
            }
            await SendAsync(writer, new Message(ProtocolVersion, "State", options.Nonce, manifest.Id, manifest.Version,
                options.ModuleApiVersion, treeIdentity, Environment.ProcessId, active, window is not null, variant, null,
                window?.IsVisible == true));
        }
    }

    private static Window CreateWindow(IModuleWpfViewFactory module, string title, Action closed)
    {
        var view = module.CreateView() ?? throw new InvalidOperationException("WPF view unavailable.");
        var window = new Window { Title = title, Content = view, Width = 820, Height = 620 };
        window.Closed += (_, _) => { window.Content = null; closed(); };
        return window;
    }

    private static async Task<IReadOnlyList<QmodStagedFile>> SnapshotAsync(string root)
    {
        var files = new List<QmodStagedFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative == ".qing-transaction-owner") continue;
            await using var stream = File.OpenRead(path);
            files.Add(new(relative, stream.Length, Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)).ToLowerInvariant()));
        }
        return files;
    }

    private static Task SendAsync(StreamWriter writer, Message message) => writer.WriteLineAsync(JsonSerializer.Serialize(message));
    private sealed record WindowSnapshot(bool WasVisible, WindowState State, bool WasActive);
    private sealed record Message(int ProtocolVersion, string Type, string Nonce, string ModuleId, string ManifestVersion,
        string ModuleApiVersion, string ProgramTreeIdentity, int ProcessId, bool IsActive, bool HasWindows,
        string? RuntimeVariant, string? Error, bool WindowVisible = false);
    private sealed record Options(string PipeName, string Nonce, string ModuleId, string ManifestVersion,
        string ModuleApiVersion, string ProgramTreeIdentity, string ModuleDirectory, string DataRoot,
        bool TestExitAfterHello)
    {
        public static Options Parse(string[] args)
        {
            var values = args.Chunk(2).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal);
            string Get(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException($"Missing {key}.");
            return new(Get("--pipe"), Get("--nonce"), Get("--module-id"), Get("--manifest-version"),
                Get("--module-api"), Get("--tree-identity"), Path.GetFullPath(Get("--module-directory")),
                Path.GetFullPath(Get("--data-root")),
                values.TryGetValue("--test-exit-after-hello", out var testExit) &&
                bool.TryParse(testExit, out var enabled) && enabled);
        }
    }
    private sealed class PassthroughLocalization : ILocalizationService
    {
        public CultureInfo CurrentCulture => CultureInfo.CurrentUICulture;
        public string CurrentLanguageCode => CurrentCulture.Name;
        public event EventHandler? CultureChanged { add { } remove { } }
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(CurrentCulture, key, args);
        public string GetModuleString(string moduleId, string key, string? fallback = null) => fallback ?? key;
        public string GetModuleString(string moduleId, string key, string? fallback, params object[] args) =>
            string.Format(CurrentCulture, fallback ?? key, args);
    }
}
