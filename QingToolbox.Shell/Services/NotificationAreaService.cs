using System.Diagnostics;
using System.Drawing;
using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Shell.Startup;
using Forms = System.Windows.Forms;

namespace QingToolbox.Shell.Services;

public sealed record NotificationAvailabilityChangedEventArgs(
    bool Available,
    bool RecoveredAfterExplorerRestart,
    string? FailureDiagnostic);

public interface INotificationAreaIcon : IDisposable
{
    bool IsAvailable { get; }
    bool IsExiting { get; }
    event EventHandler<NotificationAvailabilityChangedEventArgs>? AvailabilityChanged;
    Func<Task>? OpenRequested { get; set; }
    Func<Task>? OpenSettingsRequested { get; set; }
    Func<Task>? FloatingBadgeRequested { get; set; }
    Func<Task>? ExitRequested { get; set; }
    bool Initialize();
    void PrepareForExit();
}

public sealed class NotificationAreaService : INotificationAreaIcon, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly ApplicationExecutionEnvironment _environment;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _menu;
    private Icon? _icon;
    private bool _disposed;
    private bool _cultureSubscribed;
    private int _dispatchPending;
    private TaskbarCreatedWindow? _taskbarCreatedWindow;

    public NotificationAreaService(ILocalizationService localization, ApplicationExecutionEnvironment environment)
    { _localization = localization; _environment = environment; }

    public Func<Task>? OpenRequested { get; set; }
    public Func<Task>? OpenSettingsRequested { get; set; }
    public Func<Task>? FloatingBadgeRequested { get; set; }
    public Func<Task>? ExitRequested { get; set; }
    public bool IsAvailable => !_disposed && _notifyIcon is { Visible: true };
    public bool IsExiting { get; private set; }
    public event EventHandler<NotificationAvailabilityChangedEventArgs>? AvailabilityChanged;

    public bool Initialize()
    {
        if (IsAvailable) return true;
        if (_disposed || IsExiting) return false;
        Icon? icon = null;
        Forms.ContextMenuStrip? menu = null;
        Forms.NotifyIcon? notifyIcon = null;
        try
        {
            var resource = Application.GetResourceStream(new Uri(
                "pack://application:,,,/QingToolbox.Shell;component/Assets/Branding/QingToolbox.ico"));
            if (resource?.Stream is null) return false;
            using (resource.Stream)
            using (var source = new Icon(resource.Stream)) icon = (Icon)source.Clone();
            menu = new Forms.ContextMenuStrip();
            BuildMenu(menu);
            notifyIcon = new Forms.NotifyIcon
            {
                Icon = icon,
                Text = _environment.DisplayName,
                ContextMenuStrip = menu,
                Visible = true
            };
            notifyIcon.MouseClick += OnMouseClick;
            notifyIcon.DoubleClick += OnDoubleClick;

            _icon = icon;
            _menu = menu;
            _notifyIcon = notifyIcon;
            icon = null;
            menu = null;
            notifyIcon = null;
            if (!_cultureSubscribed)
            {
                _localization.CultureChanged += OnCultureChanged;
                _cultureSubscribed = true;
            }
            _taskbarCreatedWindow ??= new TaskbarCreatedWindow(OnTaskbarCreated);
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Notification area initialization failed: {exception.GetType().Name}");
            if (_cultureSubscribed)
            {
                _localization.CultureChanged -= OnCultureChanged;
                _cultureSubscribed = false;
            }
            DisposeResources();
            return false;
        }
        finally
        {
            if (notifyIcon is not null)
            {
                notifyIcon.Visible = false;
                notifyIcon.MouseClick -= OnMouseClick;
                notifyIcon.DoubleClick -= OnDoubleClick;
                notifyIcon.Dispose();
            }
            menu?.Dispose();
            icon?.Dispose();
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
        BuildMenu(_menu);
    }

    private void BuildMenu(Forms.ContextMenuStrip menu)
    {
        AddItem(menu, "notificationArea.open", "Open QingToolbox", OpenRequested);
        AddItem(menu, "notificationArea.floatingBadge", "Switch to floating badge", FloatingBadgeRequested);
        AddItem(menu, "notificationArea.settings", "Open settings", OpenSettingsRequested);
        menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem(menu, "notificationArea.exit", "Exit QingToolbox", ExitRequested);
    }

    private void AddItem(Forms.ContextMenuStrip menu, string key, string fallback, Func<Task>? action)
    {
        var item = new Forms.ToolStripMenuItem(_localization.GetString(key));
        if (item.Text == key) item.Text = fallback;
        item.Click += (_, _) => Dispatch(action);
        menu.Items.Add(item);
    }

    private void Dispatch(Func<Task>? action)
    {
        if (action is null || IsExiting || Interlocked.Exchange(ref _dispatchPending, 1) != 0) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            Interlocked.Exchange(ref _dispatchPending, 0);
            return;
        }
        try
        {
            var task = dispatcher.InvokeAsync(async () =>
            {
                try { await action(); }
                catch (Exception exception) { Debug.WriteLine($"Notification area action failed: {exception.GetType().Name}"); }
            }).Task.Unwrap();
            ObserveDispatcherTask(task, "action", resetDispatch: true);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _dispatchPending, 0);
            Debug.WriteLine($"Notification area dispatch failed: {exception.GetType().Name}");
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        if (IsExiting) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        try { ObserveDispatcherTask(dispatcher.InvokeAsync(RebuildMenu).Task, "menu refresh", resetDispatch: false); }
        catch (Exception exception) { Debug.WriteLine($"Notification area menu refresh failed: {exception.GetType().Name}"); }
    }

    private void OnTaskbarCreated()
    {
        if (_disposed || IsExiting) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        try
        {
            ObserveDispatcherTask(dispatcher.InvokeAsync(() =>
            {
                if (_disposed || IsExiting) return;
                DisposeResources();
                var recovered = Initialize();
                AvailabilityChanged?.Invoke(this, new(recovered, recovered,
                    recovered ? null : "startup.notificationRecoveryFailed"));
            }).Task, "Explorer recovery", resetDispatch: false);
        }
        catch (Exception exception)
        { Debug.WriteLine($"Notification area Explorer recovery failed: {exception.GetType().Name}"); }
    }

    private void ObserveDispatcherTask(Task task, string operation, bool resetDispatch)
    {
        _ = task.ContinueWith(completed =>
        {
            if (completed.IsFaulted)
                Debug.WriteLine($"Notification area {operation} failed: {completed.Exception?.GetBaseException().GetType().Name}");
            else if (completed.IsCanceled)
                Debug.WriteLine($"Notification area {operation} canceled");
            if (resetDispatch) Interlocked.Exchange(ref _dispatchPending, 0);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

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
        if (_cultureSubscribed)
        {
            _localization.CultureChanged -= OnCultureChanged;
            _cultureSubscribed = false;
        }
        DisposeResources();
        _taskbarCreatedWindow?.Dispose();
        _taskbarCreatedWindow = null;
    }

    private sealed class TaskbarCreatedWindow : Forms.NativeWindow, IDisposable
    {
        private readonly Action _callback;
        private readonly int _message;
        private bool _disposed;

        public TaskbarCreatedWindow(Action callback)
        {
            _callback = callback;
            _message = RegisterWindowMessage("TaskbarCreated");
            CreateHandle(new Forms.CreateParams { Caption = "QingToolbox.NotificationAreaMonitor" });
        }

        protected override void WndProc(ref Forms.Message message)
        {
            if (!_disposed && message.Msg == _message) _callback();
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DestroyHandle();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);
    }
}
