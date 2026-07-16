using System.Diagnostics;
using System.Drawing;
using System.Windows;
using QingToolbox.Abstractions.Localization;
using Forms = System.Windows.Forms;

namespace QingToolbox.Shell.Services;

public interface INotificationAreaIcon
{
    bool IsAvailable { get; }
    bool IsExiting { get; }
    bool Initialize();
    void PrepareForExit();
}

public sealed class NotificationAreaService : INotificationAreaIcon, IDisposable
{
    private readonly ILocalizationService _localization;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _menu;
    private Icon? _icon;
    private bool _disposed;
    private int _dispatchPending;

    public NotificationAreaService(ILocalizationService localization) =>
        _localization = localization;

    public Func<Task>? OpenRequested { get; set; }
    public Func<Task>? OpenSettingsRequested { get; set; }
    public Func<Task>? FloatingBadgeRequested { get; set; }
    public Func<Task>? ExitRequested { get; set; }
    public bool IsAvailable => !_disposed && _notifyIcon is { Visible: true };
    public bool IsExiting { get; private set; }

    public bool Initialize()
    {
        if (IsAvailable) return true;
        if (_disposed || IsExiting) return false;
        try
        {
            var resource = Application.GetResourceStream(new Uri(
                "pack://application:,,,/QingToolbox.Shell;component/Assets/Branding/QingToolbox.ico"));
            if (resource?.Stream is null) return false;
            using (resource.Stream)
            using (var source = new Icon(resource.Stream)) _icon = (Icon)source.Clone();
            _menu = new Forms.ContextMenuStrip();
            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = _icon,
                Text = "QingToolbox",
                ContextMenuStrip = _menu,
                Visible = true
            };
            _notifyIcon.MouseClick += OnMouseClick;
            _notifyIcon.DoubleClick += OnDoubleClick;
            _localization.CultureChanged += OnCultureChanged;
            RebuildMenu();
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Notification area initialization failed: {exception.GetType().Name}");
            DisposeResources();
            return false;
        }
    }

    public void PrepareForExit()
    {
        IsExiting = true;
        if (_notifyIcon is not null) _notifyIcon.Visible = false;
        if (_menu is not null) _menu.Enabled = false;
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) Dispatch(OpenRequested);
    }

    private void OnDoubleClick(object? sender, EventArgs e) => Dispatch(OpenRequested);

    private void RebuildMenu()
    {
        if (_menu is null || IsExiting) return;
        _menu.Items.Clear();
        AddItem("notificationArea.open", "Open QingToolbox", OpenRequested);
        AddItem("notificationArea.floatingBadge", "Switch to floating badge", FloatingBadgeRequested);
        AddItem("notificationArea.settings", "Open settings", OpenSettingsRequested);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem("notificationArea.exit", "Exit QingToolbox", ExitRequested);
    }

    private void AddItem(string key, string fallback, Func<Task>? action)
    {
        var item = new Forms.ToolStripMenuItem(_localization.GetString(key));
        if (item.Text == key) item.Text = fallback;
        item.Click += (_, _) => Dispatch(action);
        _menu!.Items.Add(item);
    }

    private void Dispatch(Func<Task>? action)
    {
        if (action is null || IsExiting || Interlocked.Exchange(ref _dispatchPending, 1) != 0) return;
        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try { await action(); }
            catch (Exception exception) { Debug.WriteLine($"Notification area action failed: {exception.GetType().Name}"); }
            finally { Interlocked.Exchange(ref _dispatchPending, 0); }
        }).Task.Unwrap();
    }

    private void OnCultureChanged(object? sender, EventArgs e) =>
        Application.Current.Dispatcher.InvokeAsync(RebuildMenu);

    private void DisposeResources()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.MouseClick -= OnMouseClick;
            _notifyIcon.DoubleClick -= OnDoubleClick;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _menu?.Dispose();
        _menu = null;
        _icon?.Dispose();
        _icon = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsExiting = true;
        _localization.CultureChanged -= OnCultureChanged;
        DisposeResources();
    }
}
