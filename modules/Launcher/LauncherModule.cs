using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.Launcher;

public interface ILauncherProcessStarter
{
    bool Start(LauncherItem item);
}

public sealed class LauncherModule : IWebToolModule, IWebExternalFileDropSink, IModuleHostWindowActionSource, IModuleHostWindowPresentationSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly ILauncherProcessStarter _processStarter;
    private readonly ILauncherDesktopSource _desktopSource;
    private readonly LauncherHotkeyService _hotkey;
    private LauncherStore? _store;
    private ModuleContext? _context;
    private string? _iconsDirectory;
    private bool _active;
    private bool _disposed;

    public LauncherModule()
        : this(null, null, null) { }

    internal LauncherModule(ILauncherProcessStarter? processStarter, ILauncherHotkeyRegistration? hotkeyRegistration, ILauncherDesktopSource? desktopSource = null)
    {
        _processStarter = processStarter ?? new WindowsLauncherProcessStarter();
        _desktopSource = desktopSource ?? new WindowsLauncherDesktopSource();
        _hotkey = hotkeyRegistration is null
            ? new LauncherHotkeyService()
            : new LauncherHotkeyService(hotkeyRegistration);
        _hotkey.Triggered += OnHotkeyTriggered;
    }

    public string Id => "qing.launcher";
    public string Name => "Qing Launcher";
    public string Description => "Launch user-selected Windows applications and shortcuts.";
    public ModuleHostWindowPresentationMode HostWindowPresentationMode => ModuleHostWindowPresentationMode.Overlay;
    public event EventHandler<ModuleWebEventArgs>? WebEvent;
    public event EventHandler<ModuleHostWindowActionEventArgs>? HostWindowActionRequested;

    public Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context is not null) return Task.CompletedTask;
        _context = context;
        _store = new LauncherStore(context.DataDirectory);
        _iconsDirectory = Path.Combine(context.DataDirectory, "icons");
        RefreshDesktopItems();
        MigrateIconCache();
        return Task.CompletedTask;
    }

    public Task OnActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = RequireStore();
        lock (_gate) _active = true;
        _hotkey.Activate(store.Hotkey);
        PublishState();
        return Task.CompletedTask;
    }

    public Task OnDeactivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _active = false;
        _hotkey.Deactivate();
        PublishState();
        return Task.CompletedTask;
    }

    public async Task<JsonElement?> HandleWebRequestAsync(
        string method,
        JsonElement? payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = RequireStore();
        switch (method)
        {
            case "getState":
                return Snapshot();
            case "setSortMode":
                var mode = RequiredString(payload, "mode");
                if (mode == "desktop") { RefreshDesktopItems(); MigrateIconCache(); }
                if (!store.SetSortMode(mode)) throw new ArgumentException("View mode must be custom, alphabetical, or desktop.");
                PublishState();
                return Snapshot();
            case "setCustomOrder":
                if (!store.SetCustomOrder(RequiredStringArray(payload, "ids"))) throw new ArgumentException("The active view order must contain every item exactly once.");
                PublishState();
                return Snapshot();
            case "launchItem":
                await LaunchItemAsync(RequiredString(payload, "id"), cancellationToken).ConfigureAwait(false);
                return Snapshot();
            case "removeItem":
                RemoveItem(RequiredString(payload, "id"));
                return Snapshot();
            case "getIcon":
                return GetIcon(RequiredString(payload, "id"));
            case "setHotkey":
                return SetHotkey(payload);
            case "hideWindow":
                RequestWindowAction(ModuleHostWindowAction.Hide);
                return Snapshot();
            default:
                throw new InvalidOperationException("Unknown Qing Launcher method.");
        }
    }

    public Task HandleExternalFilesDroppedAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = RequireStore();
        var added = new List<string>();
        var skipped = new List<string>();
        foreach (var (displayPath, resolved) in LauncherItemResolver.ResolveMany(paths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolved is null)
            {
                if (!string.IsNullOrWhiteSpace(displayPath)) skipped.Add(displayPath);
                continue;
            }
            if (!store.Add(resolved, null, out var item))
            {
                skipped.Add(displayPath);
                continue;
            }
            var iconKey = LauncherIconCache.TryCache(resolved.IconSourcePath, _iconsDirectory!, item.Id);
            if (iconKey is not null) store.SetIconKey(item.Id, iconKey);
            added.Add(item.Name);
        }
        PublishState();
        PublishEvent("dropResult", new { added = added.ToArray(), skipped = skipped.ToArray() });
        return Task.CompletedTask;
    }

    public async Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context is null) return;
        _hotkey.Deactivate();
        lock (_gate) _active = false;
        _store = null;
        _iconsDirectory = null;
        _context = null;
        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _hotkey.Triggered -= OnHotkeyTriggered;
        _hotkey.Dispose();
        _store = null;
        _context = null;
        return ValueTask.CompletedTask;
    }

    internal LauncherStateView GetStateForTest() => RequireStore().Snapshot(_hotkey.Status, IsActive());
    internal void TriggerHotkeyForTest() => _hotkey.TriggerForTest();

    private async Task LaunchItemAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = RequireStore();
        if (!store.TryGet(id, out var item)) throw new InvalidOperationException("The selected application is no longer available.");
        bool started;
        try { started = _processStarter.Start(item); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            throw new InvalidOperationException("The selected application could not be started.", exception);
        }
        if (!started) throw new InvalidOperationException("The selected application could not be started.");
        store.MarkLaunched(id, DateTimeOffset.UtcNow);
        PublishState();
        RequestWindowAction(ModuleHostWindowAction.Hide);
        await Task.CompletedTask;
    }

    private void RemoveItem(string id)
    {
        var store = RequireStore();
        if (!store.Remove(id, out var removed) || removed is null) return;
        LauncherIconCache.DeleteBestEffort(removed.IconKey, _iconsDirectory!);
        PublishState();
    }

    private void MigrateIconCache()
    {
        if (_store is null || _iconsDirectory is null) return;
        foreach (var item in _store.Items.Concat(_store.DesktopItems))
        {
            var existing = item.IconKey;
            var path = string.IsNullOrWhiteSpace(existing) ? null : Path.Combine(_iconsDirectory, existing);
            if (LauncherIconCache.IsCurrentKey(existing) && path is not null && File.Exists(path)) continue;
            var key = LauncherIconCache.TryCache(item.IconSourcePath ?? item.Target, _iconsDirectory, item.Id);
            if (key is null || string.Equals(existing, key, StringComparison.Ordinal)) continue;
            _store.SetIconKey(item.Id, key);
            LauncherIconCache.DeleteBestEffort(existing, _iconsDirectory);
        }
    }

    private void RefreshDesktopItems()
    {
        if (_store is null) return;
        _store.SynchronizeDesktopItems(_desktopSource.Scan());
    }

    private JsonElement SetHotkey(JsonElement? payload)
    {
        var current = RequireStore().Hotkey;
        var requested = new LauncherHotkeySpec(
            OptionalBool(payload, "ctrl", current.Ctrl),
            OptionalBool(payload, "alt", current.Alt),
            OptionalBool(payload, "shift", current.Shift),
            OptionalBool(payload, "win", current.Win),
            OptionalInt(payload, "virtualKey", current.VirtualKey),
            OptionalString(payload, "keyLabel", current.KeyLabel)).Normalize();
        if (!_hotkey.Replace(requested))
        {
            PublishState();
            return Snapshot();
        }
        RequireStore().SetHotkey(requested);
        PublishState();
        return Snapshot();
    }

    private JsonElement GetIcon(string id)
    {
        var store = RequireStore();
        if (!store.TryGet(id, out var item) || string.IsNullOrWhiteSpace(item.IconKey) || Path.GetFileName(item.IconKey) != item.IconKey)
            return JsonSerializer.SerializeToElement(new { dataUrl = (string?)null });
        var path = Path.Combine(_iconsDirectory!, item.IconKey);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > 512 * 1024) return JsonSerializer.SerializeToElement(new { dataUrl = (string?)null });
            var data = Convert.ToBase64String(File.ReadAllBytes(path));
            return JsonSerializer.SerializeToElement(new { dataUrl = "data:image/png;base64," + data });
        }
        catch (IOException) { return JsonSerializer.SerializeToElement(new { dataUrl = (string?)null }); }
        catch (UnauthorizedAccessException) { return JsonSerializer.SerializeToElement(new { dataUrl = (string?)null }); }
    }

    private void OnHotkeyTriggered(object? sender, EventArgs e)
    {
        if (!IsActive()) return;
        RequestWindowAction(ModuleHostWindowAction.Toggle);
    }

    private void RequestWindowAction(ModuleHostWindowAction action) =>
        HostWindowActionRequested?.Invoke(this, new ModuleHostWindowActionEventArgs(action));

    private void PublishState() => PublishEvent("stateChanged", Snapshot());

    private void PublishEvent(string name, object payload)
    {
        WebEvent?.Invoke(this, new ModuleWebEventArgs(name, JsonSerializer.SerializeToElement(payload, JsonOptions)));
    }

    private JsonElement Snapshot() =>
        JsonSerializer.SerializeToElement(RequireStore().Snapshot(_hotkey.Status, IsActive()), JsonOptions);

    private bool IsActive() { lock (_gate) return _active; }

    private LauncherStore RequireStore() => _store ?? throw new InvalidOperationException("Module is not loaded.");

    private static string RequiredString(JsonElement? payload, string name)
    {
        if (payload is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())) return property.GetString()!;
        throw new ArgumentException($"Missing {name}.");
    }

    private static IReadOnlyList<string> RequiredStringArray(JsonElement? payload, string name)
    {
        if (payload is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array)
        {
            var values = property.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray();
            return values;
        }
        throw new ArgumentException($"Missing {name}.");
    }

    private static bool OptionalBool(JsonElement? payload, string name, bool fallback) =>
        payload is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : fallback;

    private static int OptionalInt(JsonElement? payload, string name, int fallback) =>
        payload is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) &&
        property.TryGetInt32(out var result) ? result : fallback;

    private static string OptionalString(JsonElement? payload, string name, string fallback) =>
        payload is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()! : fallback;
}

internal sealed class WindowsLauncherProcessStarter : ILauncherProcessStarter
{
    public bool Start(LauncherItem item)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = item.Target,
            Arguments = item.Arguments,
            WorkingDirectory = item.WorkingDirectory,
            UseShellExecute = true,
        });
        return process is not null;
    }
}
