using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace QingToolbox.Modules.QingTransfer;

/// <summary>
/// Windows DNS-SD discovery for the D0 QingTransfer surface.  The TCP socket is
/// only an ephemeral advertisement endpoint; no transfer protocol is accepted.
/// </summary>
public sealed class QingTransferDiscoveryService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly QingTransferPeerTable _peers = new();
    private readonly ConcurrentDictionary<IntPtr, ResolveOperation> _resolves = new();
    private readonly string _friendlyName;
    private readonly string _hostName;
    private readonly string? _diagnosticPath;
    private readonly QingTransferNative.ServiceComplete _registerCallback;
    private readonly QingTransferNative.BrowseComplete _browseCallback;
    private readonly QingTransferNative.ServiceComplete _resolveCallback;
    private CancellationTokenSource? _lifetime;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private GCHandle _selfHandle;
    private IntPtr _selfContext;
    private IntPtr _registrationInstance;
    private QingTransferNative.RegisterRequest _registrationRequest;
    private QingTransferNative.ServiceCancel _registrationCancel;
    private QingTransferNative.ServiceCancel _browseCancel;
    private bool _hasBrowse;
    private RegistrationPhase _registrationPhase;
    private bool _running;
    private bool _disposed;
    private string? _registeredServiceName;
    private TaskCompletionSource<bool>? _registrationCompletion;
    private bool _registrationNativeCallActive;
    private IntPtr _deferredRegistrationInstance;

    private enum RegistrationPhase
    {
        None,
        Registering,
        Registered,
        Deregistering,
        Canceling,
    }

    public QingTransferDiscoveryService(string friendlyName, string? diagnosticDirectory = null)
    {
        _friendlyName = SanitizeFriendlyName(friendlyName);
        _hostName = $"{SanitizeDnsLabel(Environment.MachineName)}.local";
        _diagnosticPath = string.IsNullOrWhiteSpace(diagnosticDirectory) ? null : Path.Combine(diagnosticDirectory, "discovery-debug.log");
        _registerCallback = OnRegisterComplete;
        _browseCallback = OnBrowseComplete;
        _resolveCallback = OnResolveComplete;
    }

    public event EventHandler<IReadOnlyList<QingTransferPeer>>? PeersChanged;
    /// <summary>Raised for an accepted TCP endpoint. The handler owns/disposes the client.</summary>
    public Func<TcpClient, CancellationToken, Task>? IncomingClientHandler { get; set; }

    public bool IsRunning { get { lock (_gate) return _running; } }

    public IReadOnlyList<QingTransferPeer> Peers
    {
        get { lock (_gate) return _peers.Snapshot(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_running) return;
            _running = true;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            _selfContext = GCHandle.ToIntPtr(_selfHandle);
            _registrationPhase = RegistrationPhase.None;
        }

        try
        {
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            _acceptTask = AcceptUnexpectedConnectionsAsync(_listener, _lifetime!.Token);
            RegisterService(_listener.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0);
            BrowseServices();
        }
        catch (DllNotFoundException)
        {
            await StopAsync().ConfigureAwait(false);
            throw new PlatformNotSupportedException("Windows DNS-SD is unavailable on this system.");
        }
        catch (EntryPointNotFoundException)
        {
            await StopAsync().ConfigureAwait(false);
            throw new PlatformNotSupportedException("Windows DNS-SD is unavailable on this system.");
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        TcpListener? listener;
        Task? acceptTask;
        CancellationTokenSource? lifetime;
        QingTransferNative.ServiceCancel browseCancel;
        RegistrationPhase registrationPhase;
        bool hasBrowse;
        QingTransferNative.RegisterRequest registrationRequest;
        QingTransferNative.ServiceCancel registrationCancel;
        IntPtr registrationInstance;
        lock (_gate)
        {
            if (!_running)
            {
                _lifetime?.Dispose();
                _lifetime = null;
                return;
            }
            _running = false;
            listener = _listener;
            _listener = null;
            acceptTask = _acceptTask;
            _acceptTask = null;
            lifetime = _lifetime;
            _lifetime = null;
            browseCancel = _browseCancel;
            registrationPhase = _registrationPhase;
            hasBrowse = _hasBrowse;
            registrationRequest = _registrationRequest;
            registrationCancel = _registrationCancel;
            registrationInstance = _registrationInstance;
            _hasBrowse = false;
            _browseCancel = default;
            _registeredServiceName = null;
            if (registrationPhase == RegistrationPhase.Registering)
                _registrationPhase = RegistrationPhase.Canceling;
            else if (registrationPhase == RegistrationPhase.Registered)
                _registrationPhase = RegistrationPhase.Deregistering;
        }

        lifetime?.Cancel();
        try { listener?.Stop(); } catch { }
        if (acceptTask is not null)
        {
            try { await acceptTask.ConfigureAwait(false); } catch { }
        }

        if (hasBrowse)
        {
            try { QingTransferNative.DnsServiceBrowseCancel(ref browseCancel); } catch { }
        }
        foreach (var operation in _resolves.Values.ToArray())
        {
            try { if (operation.HasCancel) QingTransferNative.DnsServiceResolveCancel(ref operation.Cancel); } catch { }
        }
        _resolves.Clear();
        if (registrationPhase == RegistrationPhase.Registering)
        {
            try
            {
                lock (_gate) _registrationNativeCallActive = true;
                QingTransferNative.DnsServiceRegisterCancel(ref registrationCancel);
            }
            catch { }
            finally
            {
                lock (_gate) _registrationNativeCallActive = false;
                DrainDeferredRegistrationInstance();
            }
            if (_registrationCompletion is not null)
            {
                try { await _registrationCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { }
            }
            CompleteRegistrationState(IntPtr.Zero);
        }
        else if (registrationPhase == RegistrationPhase.Registered)
        {
            uint deregisterStatus = uint.MaxValue;
            try
            {
                // The API requires the exact request used by DnsServiceRegister;
                // all callback/context/instance fields remain valid until its callback.
                lock (_gate) _registrationNativeCallActive = true;
                deregisterStatus = QingTransferNative.DnsServiceDeRegister(ref registrationRequest, IntPtr.Zero);
            }
            catch { }
            finally
            {
                lock (_gate) _registrationNativeCallActive = false;
                DrainDeferredRegistrationInstance();
            }
            if (deregisterStatus != 0 && deregisterStatus != QingTransferNative.DnsRequestPending)
                CompleteRegistrationState(registrationInstance);
            else if (_registrationCompletion is not null)
            {
                try { await _registrationCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { ScheduleRegistrationCleanup(registrationInstance); }
                if (_registrationPhase == RegistrationPhase.None) CompleteRegistrationState(IntPtr.Zero);
            }
        }
        else if (registrationInstance != IntPtr.Zero)
        {
            // A synchronous registration failure can leave the constructed
            // instance owned by this service even though no cancel handle exists.
            CompleteRegistrationState(registrationInstance);
        }
        lifetime?.Dispose();

        lock (_gate)
        {
            _peers.Clear();
            if (_registrationPhase == RegistrationPhase.None && _selfHandle.IsAllocated)
            {
                _selfHandle.Free();
                _selfContext = IntPtr.Zero;
            }
        }
        RaisePeersChanged();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await StopAsync().ConfigureAwait(false);
    }

    private void RegisterService(int port)
    {
        var serviceName = $"{SanitizeDnsLabel(_friendlyName)}.{QingTransferMetadata.ServiceType}.local";
        var properties = new NativePropertyArrays(QingTransferMetadata.Create("windows", _friendlyName));
        try
        {
            _registrationInstance = QingTransferNative.DnsServiceConstructInstance(
                serviceName, _hostName, IntPtr.Zero, IntPtr.Zero, (ushort)port, 0, 0,
                (uint)properties.Count, properties.Keys, properties.Values);
            if (_registrationInstance == IntPtr.Zero)
                throw new InvalidOperationException("DnsServiceConstructInstance returned no instance.");

            var request = new QingTransferNative.RegisterRequest
            {
                Version = QingTransferNative.DnsQueryRequestVersion1,
                InterfaceIndex = 0,
                ServiceInstance = _registrationInstance,
                RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(_registerCallback),
                QueryContext = _selfContext,
                Credentials = IntPtr.Zero,
                UnicastEnabled = 0,
            };
            lock (_gate)
            {
                _registrationRequest = request;
                _registrationPhase = RegistrationPhase.Registering;
                _registrationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            // Pass the field, not a stack-local copy: the async API retains the
            // request address until registration/deregistration callback.
            uint status;
            lock (_gate) _registrationNativeCallActive = true;
            try
            {
                status = QingTransferNative.DnsServiceRegister(ref _registrationRequest, out _registrationCancel);
            }
            finally
            {
                lock (_gate) _registrationNativeCallActive = false;
                DrainDeferredRegistrationInstance();
            }
            if (status != 0 && status != QingTransferNative.DnsRequestPending)
            {
                lock (_gate) _registrationPhase = RegistrationPhase.None;
                throw new InvalidOperationException($"DnsServiceRegister failed with status {status}.");
            }
            lock (_gate)
                if (_registrationPhase == RegistrationPhase.Registering)
                    _registrationPhase = status == QingTransferNative.DnsRequestPending ? RegistrationPhase.Registering : RegistrationPhase.Registered;
        }
        finally
        {
            properties.Dispose();
        }
    }

    private void BrowseServices()
    {
        var queryName = Marshal.StringToCoTaskMemUni(QingTransferMetadata.WindowsServiceType);
        try
        {
            var request = new QingTransferNative.BrowseRequest
            {
                Version = QingTransferNative.DnsQueryRequestVersion1,
                InterfaceIndex = 0,
                QueryName = queryName,
                BrowseCompletionCallback = Marshal.GetFunctionPointerForDelegate(_browseCallback),
                QueryContext = _selfContext,
            };
            var status = QingTransferNative.DnsServiceBrowse(ref request, out _browseCancel);
            if (status != 0 && status != QingTransferNative.DnsRequestPending)
                throw new InvalidOperationException($"DnsServiceBrowse failed with status {status}.");
            _hasBrowse = true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(queryName);
        }
    }

    private async Task AcceptUnexpectedConnectionsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var handler = IncomingClientHandler;
                if (handler is null)
                {
                    client.Dispose();
                    continue;
                }
                var accepted = client;
                client = null;
                _ = Task.Run(async () =>
                {
                    try { await handler(accepted, cancellationToken).ConfigureAwait(false); }
                    catch { accepted.Dispose(); }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            finally { client?.Dispose(); }
        }
    }

    private void OnRegisterComplete(uint status, IntPtr queryContext, IntPtr instance)
    {
        RegistrationPhase phase;
        bool deferFree;
        try
        {
            lock (_gate)
            {
                phase = _registrationPhase;
                deferFree = _registrationNativeCallActive;
                if (deferFree && instance != IntPtr.Zero)
                    _deferredRegistrationInstance = instance;
            }
            if (instance != IntPtr.Zero)
            {
                var native = Marshal.PtrToStructure<QingTransferNative.ServiceInstance>(instance);
                var name = PtrToString(native.InstanceName);
                if (!string.IsNullOrWhiteSpace(name)) _registeredServiceName = NormalizeServiceName(name);
                if (!deferFree)
                    QingTransferNative.DnsServiceFreeInstance(instance);
            }
            if (phase is RegistrationPhase.Deregistering or RegistrationPhase.Canceling)
            {
                _registrationCompletion?.TrySetResult(true);
                lock (_gate) _registrationPhase = RegistrationPhase.None;
                if (!deferFree) CompleteRegistrationState(IntPtr.Zero);
                return;
            }
            lock (_gate)
            if (_registrationPhase == RegistrationPhase.Registering && status == 0)
                    _registrationPhase = RegistrationPhase.Registered;
            _registrationCompletion?.TrySetResult(true);
            if (instance == IntPtr.Zero && status != 0)
                CompleteRegistrationState(IntPtr.Zero);
        }
        catch { CompleteRegistrationState(IntPtr.Zero); }
    }

    private void DrainDeferredRegistrationInstance()
    {
        IntPtr instance;
        lock (_gate)
        {
            instance = _deferredRegistrationInstance;
            _deferredRegistrationInstance = IntPtr.Zero;
        }
        if (instance != IntPtr.Zero)
        {
            try { QingTransferNative.DnsServiceFreeInstance(instance); } catch { }
        }
    }

    private void OnBrowseComplete(uint status, IntPtr queryContext, IntPtr records)
    {
        WriteDiagnostic($"browse status={status} records={(records == IntPtr.Zero ? 0 : 1)}");
        if (status != 0 || records == IntPtr.Zero) return;
        try
        {
            for (var current = records; current != IntPtr.Zero;)
            {
                var record = Marshal.PtrToStructure<QingTransferNative.DnsRecord>(current);
                var next = record.Next;
                WriteDiagnostic($"browse-record type={record.Type} flags={record.Flags}");
                if (record.Type == QingTransferNative.DnsRecordTypePtr)
                {
                    var serviceName = ReadPtrRecord(record.Data);
                    var self = !string.IsNullOrWhiteSpace(serviceName) && IsSelf(serviceName);
                    WriteDiagnostic($"browse-ptr present={!string.IsNullOrWhiteSpace(serviceName)} self={self} length={serviceName?.Length ?? 0}");
                    if (!string.IsNullOrWhiteSpace(serviceName) && !IsSelf(serviceName))
                    {
                        if ((record.Flags & QingTransferNative.DnsRecordDeleteFlag) != 0) RemovePeer(serviceName);
                        else ResolveService(serviceName);
                    }
                }
                current = next;
            }
        }
        catch { }
        finally
        {
            try { QingTransferNative.DnsRecordListFree(records, QingTransferNative.DnsFreeRecordList); } catch { }
        }
    }

    private void ResolveService(string serviceName)
    {
        var self = IsSelf(serviceName);
        WriteDiagnostic($"resolve-gate running={IsRunning} self={self} length={serviceName.Length}");
        if (!IsRunning || self) return;
        WriteDiagnostic("resolve-start");
        var operation = new ResolveOperation(NormalizeServiceName(serviceName));
        operation.Handle = GCHandle.Alloc(operation, GCHandleType.Normal);
        var context = GCHandle.ToIntPtr(operation.Handle);
        operation.Context = context;
        _resolves[context] = operation;
        var queryName = Marshal.StringToCoTaskMemUni(serviceName);
        try
        {
            var request = new QingTransferNative.ResolveRequest
            {
                Version = QingTransferNative.DnsQueryRequestVersion1,
                InterfaceIndex = 0,
                QueryName = queryName,
                ResolveCompletionCallback = Marshal.GetFunctionPointerForDelegate(_resolveCallback),
                QueryContext = context,
            };
            var status = QingTransferNative.DnsServiceResolve(ref request, out operation.Cancel);
            WriteDiagnostic($"resolve-submit status={status}");
            operation.HasCancel = status == QingTransferNative.DnsRequestPending || status == 0;
            if (status != 0 && status != QingTransferNative.DnsRequestPending) CompleteResolve(operation);
        }
        catch { CompleteResolve(operation); }
        finally { Marshal.FreeCoTaskMem(queryName); }
    }

    private void OnResolveComplete(uint status, IntPtr queryContext, IntPtr instance)
    {
        WriteDiagnostic($"resolve-callback status={status} instance={(instance == IntPtr.Zero ? 0 : 1)}");
        ResolveOperation? operation = null;
        try
        {
            operation = _resolves.TryGetValue(queryContext, out var found) ? found : null;
            if (operation is not null && status == 0 && instance != IntPtr.Zero)
            {
                var native = Marshal.PtrToStructure<QingTransferNative.ServiceInstance>(instance);
                var fields = ReadProperties(native.PropertyCount, native.Keys, native.Values);
                var peer = QingTransferMetadata.Parse(
                    operation.ServiceName, fields, PtrToString(native.HostName), native.Port,
                    ReadAddresses(native), DateTimeOffset.UtcNow);
                WriteDiagnostic($"resolve-parse valid={peer is not null}");
                if (peer is not null && !IsSelf(peer.ServiceName)) UpsertPeer(peer);
                else RemovePeer(operation.ServiceName);
            }
        }
        catch { if (operation is not null) RemovePeer(operation.ServiceName); }
        finally
        {
            if (instance != IntPtr.Zero) try { QingTransferNative.DnsServiceFreeInstance(instance); } catch { }
            if (operation is not null) CompleteResolve(operation);
        }
    }

    private void CompleteResolve(ResolveOperation operation)
    {
        _resolves.TryRemove(operation.Context, out _);
        if (operation.Handle.IsAllocated) operation.Handle.Free();
    }

    private void UpsertPeer(QingTransferPeer peer)
    {
        lock (_gate)
        {
            if (_peers.Upsert(peer)) { }
            else return;
        }
        RaisePeersChanged();
    }

    private void RemovePeer(string serviceName)
    {
        lock (_gate)
        {
            if (!_peers.Remove(NormalizeServiceName(serviceName))) return;
        }
        RaisePeersChanged();
    }

    private void RaisePeersChanged() => PeersChanged?.Invoke(this, Peers);

    private void WriteDiagnostic(string message)
    {
        var path = _diagnosticPath;
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private bool IsSelf(string serviceName)
    {
        var normalized = NormalizeServiceName(serviceName);
        lock (_gate)
        {
            var registered = _registeredServiceName;
            return !string.IsNullOrEmpty(registered)
                ? string.Equals(normalized, registered, StringComparison.OrdinalIgnoreCase)
                : string.Equals(normalized, NormalizeServiceName($"{SanitizeDnsLabel(_friendlyName)}.{QingTransferMetadata.ServiceType}.local"), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeServiceName(string value) => value.Trim().TrimEnd('.');

    private static string SanitizeFriendlyName(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "QingToolbox" : value.Trim();
        return text.Length > 64 ? text[..64] : text;
    }

    private static string SanitizeDnsLabel(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value)
            if (char.IsLetterOrDigit(c) || c == '-') builder.Append(c);
        return builder.Length == 0 ? "qingtoolbox" : builder.ToString()[..Math.Min(builder.Length, 63)];
    }

    private static string? PtrToString(IntPtr value) => value == IntPtr.Zero ? null : Marshal.PtrToStringUni(value);

    private static string? ReadPtrRecord(IntPtr data)
    {
        // DNS_RECORD.Data is the first member of the inline DNS_PTR_DATA
        // union, so the managed IntPtr already contains pNameHost.
        return PtrToString(data);
    }

    private static Dictionary<string, string?> ReadProperties(uint count, IntPtr keys, IntPtr values)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var itemSize = IntPtr.Size;
        for (var i = 0; i < count && i < 16; i++)
        {
            var key = PtrToString(Marshal.ReadIntPtr(keys, i * itemSize));
            var value = PtrToString(Marshal.ReadIntPtr(values, i * itemSize));
            if (!string.IsNullOrWhiteSpace(key) && value is not null) result[key] = value;
        }
        return result;
    }

    private static IReadOnlyList<IPAddress> ReadAddresses(QingTransferNative.ServiceInstance native)
    {
        var addresses = new List<IPAddress>(2);
        if (native.Ip4Address != IntPtr.Zero)
        {
            var bytes = new byte[4]; Marshal.Copy(native.Ip4Address, bytes, 0, bytes.Length); addresses.Add(new IPAddress(bytes));
        }
        if (native.Ip6Address != IntPtr.Zero)
        {
            var bytes = new byte[16]; Marshal.Copy(native.Ip6Address, bytes, 0, bytes.Length); addresses.Add(new IPAddress(bytes));
        }
        return addresses;
    }

    private void CompleteRegistrationState(IntPtr callbackInstance)
    {
        if (callbackInstance != IntPtr.Zero)
        {
            try { QingTransferNative.DnsServiceFreeInstance(callbackInstance); } catch { }
        }
        lock (_gate)
        {
            // The originally constructed instance is owned by this service and is
            // released exactly once after DNS has no more callbacks for the request.
            if (_registrationInstance != IntPtr.Zero && _registrationInstance != callbackInstance)
            {
                try { QingTransferNative.DnsServiceFreeInstance(_registrationInstance); } catch { }
            }
            _registrationInstance = IntPtr.Zero;
            _registrationRequest = default;
            _registrationCancel = default;
            _registrationPhase = RegistrationPhase.None;
            _registrationCompletion?.TrySetResult(true);
            _registrationCompletion = null;
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
                _selfContext = IntPtr.Zero;
            }
        }
    }

    private void ScheduleRegistrationCleanup(IntPtr registrationInstance)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var shouldRelease = false;
            lock (_gate)
                shouldRelease = _registrationPhase is RegistrationPhase.Canceling or RegistrationPhase.Deregistering;
            if (shouldRelease) CompleteRegistrationState(registrationInstance);
        });
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(QingTransferDiscoveryService));
    }

    private sealed class ResolveOperation(string serviceName)
    {
        public string ServiceName { get; } = serviceName;
        public GCHandle Handle;
        public IntPtr Context;
        public QingTransferNative.ServiceCancel Cancel;
        public bool HasCancel;
    }

    private sealed class NativePropertyArrays : IDisposable
    {
        private readonly List<IntPtr> _strings = [];
        private readonly IntPtr[] _keyPointers;
        private readonly IntPtr[] _valuePointers;
        public int Count => _keyPointers.Length;
        public IntPtr Keys { get; }
        public IntPtr Values { get; }

        public NativePropertyArrays(IReadOnlyDictionary<string, string> properties)
        {
            _keyPointers = new IntPtr[properties.Count]; _valuePointers = new IntPtr[properties.Count];
            var index = 0;
            foreach (var pair in properties)
            {
                _keyPointers[index] = Alloc(pair.Key); _valuePointers[index] = Alloc(pair.Value); index++;
            }
            Keys = Marshal.AllocCoTaskMem(IntPtr.Size * _keyPointers.Length);
            Values = Marshal.AllocCoTaskMem(IntPtr.Size * _valuePointers.Length);
            for (var i = 0; i < _keyPointers.Length; i++)
            {
                Marshal.WriteIntPtr(Keys, i * IntPtr.Size, _keyPointers[i]);
                Marshal.WriteIntPtr(Values, i * IntPtr.Size, _valuePointers[i]);
            }
        }

        private IntPtr Alloc(string text)
        {
            var pointer = Marshal.StringToCoTaskMemUni(text); _strings.Add(pointer); return pointer;
        }

        public void Dispose()
        {
            if (Keys != IntPtr.Zero) Marshal.FreeCoTaskMem(Keys);
            if (Values != IntPtr.Zero) Marshal.FreeCoTaskMem(Values);
            foreach (var pointer in _strings) if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
        }
    }
}
