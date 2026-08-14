using System.Runtime.InteropServices;
using System.Windows.Interop;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Modules.Launcher;

public interface ILauncherHotkeyRegistration
{
    bool Register(int id, LauncherHotkeySpec hotkey);
    bool Unregister(int id);
}

public sealed class LauncherHotkeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private readonly ILauncherHotkeyRegistration _registration;
    private readonly bool _listenForMessages;
    private static int _nextId = 1200;
    private int? _registeredId;
    private LauncherHotkeySpec _current = LauncherHotkeySpec.Default;
    private bool _disposed;
    private bool _listening;

    public LauncherHotkeyService()
        : this(new WindowsLauncherHotkeyRegistration(), true) { }

    internal LauncherHotkeyService(ILauncherHotkeyRegistration registration, bool listenForMessages = false)
    {
        _registration = registration;
        _listenForMessages = listenForMessages;
    }

    public event EventHandler? Triggered;
    public string Status { get; private set; } = "Inactive";
    public bool IsRegistered => _registeredId is not null;
    public LauncherHotkeySpec Current => _current;

    public bool Activate(LauncherHotkeySpec requested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        requested = requested.Normalize();
        if (!requested.IsValid)
        {
            Status = "Conflict";
            return false;
        }
        if (_registeredId is not null)
        {
            _current = requested;
            Status = "Registered";
            return true;
        }

        var candidateId = NextId();
        if (!_registration.Register(candidateId, requested))
        {
            Status = "Conflict";
            return false;
        }
        _registeredId = candidateId;
        _current = requested;
        StartListening();
        Status = "Registered";
        return true;
    }

    public bool Replace(LauncherHotkeySpec requested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        requested = requested.Normalize();
        if (!requested.IsValid)
        {
            Status = _registeredId is null ? "Inactive" : "Conflict";
            return false;
        }
        if (_registeredId is null)
        {
            _current = requested;
            Status = "Inactive";
            return true;
        }

        var oldId = _registeredId.Value;
        var candidateId = NextId();
        // Register the candidate before touching the current binding. If this
        // fails, the old shortcut remains registered and usable.
        if (!_registration.Register(candidateId, requested))
        {
            Status = "Conflict";
            return false;
        }
        _registration.Unregister(oldId);
        _registeredId = candidateId;
        _current = requested;
        Status = "Registered";
        return true;
    }

    public void Deactivate()
    {
        if (_registeredId is { } id)
        {
            _registration.Unregister(id);
            _registeredId = null;
        }
        StopListening();
        Status = "Inactive";
    }

    internal void TriggerForTest()
    {
        if (_registeredId is not null) Triggered?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
    }

    private void StartListening()
    {
        if (!_listenForMessages || _listening) return;
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
        _listening = true;
    }

    private void StopListening()
    {
        if (!_listening) return;
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        _listening = false;
    }

    private void OnThreadFilterMessage(ref MSG message, ref bool handled)
    {
        if (message.message != WmHotKey || _registeredId is not { } id || message.wParam.ToInt32() != id) return;
        handled = true;
        Triggered?.Invoke(this, EventArgs.Empty);
    }

    private static int NextId() => Interlocked.Increment(ref _nextId);
}

internal sealed class WindowsLauncherHotkeyRegistration : ILauncherHotkeyRegistration
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    public bool Register(int id, LauncherHotkeySpec hotkey) =>
        RegisterHotKey(IntPtr.Zero, id, ToModifiers(hotkey) | ModNoRepeat, (uint)hotkey.VirtualKey);

    public bool Unregister(int id) => UnregisterHotKey(IntPtr.Zero, id);

    private static uint ToModifiers(LauncherHotkeySpec hotkey) =>
        (hotkey.Ctrl ? ModControl : 0) |
        (hotkey.Alt ? ModAlt : 0) |
        (hotkey.Shift ? ModShift : 0) |
        (hotkey.Win ? ModWin : 0);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
