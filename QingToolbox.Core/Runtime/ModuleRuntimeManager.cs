using QingToolbox.Abstractions.Modules;
using QingToolbox.ModuleLoader;

namespace QingToolbox.Core.Runtime;

public sealed class ModuleRuntimeManager(InProcessModuleLoader loader) : IAsyncDisposable
{
    private const string MissingManifestMessage = "Module manifest disappeared from discovery.";

    private readonly Dictionary<string, ModuleRuntimeRecord> _records =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private bool _isDisposed;

    public IReadOnlyCollection<ModuleRuntimeRecord> Records => _records.Values;

    public void ReplaceDiscoveredModules(IEnumerable<DiscoveredModule> modules)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(modules);

        if (!_sync.Wait(0))
        {
            throw new ModuleRuntimeException(
                "Module runtime is busy. Try refreshing discovery again.");
        }
        try
        {
            var discoveredById = modules.ToDictionary(
                module => module.Manifest.Id,
                StringComparer.Ordinal);

            foreach (var module in discoveredById.Values)
            {
                if (!_records.TryGetValue(module.Manifest.Id, out var existing) ||
                    !existing.IsLoaded)
                {
                    _records[module.Manifest.Id] = new ModuleRuntimeRecord(module);
                }
            }

            foreach (var record in _records.Values.Where(record => record.IsLoaded))
            {
                if (!discoveredById.ContainsKey(record.Manifest.Id))
                {
                    record.LastError = MissingManifestMessage;
                }
                else if (record.LastError == MissingManifestMessage)
                {
                    record.LastError = null;
                }
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public ModuleRuntimeRecord? GetRecord(string moduleId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        return _records.GetValueOrDefault(moduleId);
    }

    public object? CreateView(string moduleId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        if (!_sync.Wait(0))
        {
            throw new ModuleRuntimeException(
                "Module runtime is busy. Try opening the module again.");
        }

        ModuleRuntimeRecord? record = null;
        try
        {
            record = GetRequiredRecord(moduleId);
            return GetRequiredHandle(record).Module.CreateView();
        }
        catch (Exception exception)
        {
            if (record is not null)
            {
                SetFailure(record, exception);
            }

            throw WrapFailure(
                $"Failed to create view for module '{moduleId}'.",
                exception);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task LoadAsync(
        string moduleId,
        string dataRootDirectory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var record = GetRequiredRecord(moduleId);
            if (record.IsLoaded)
            {
                return;
            }

            if (!record.DiscoveredModule.IsValid)
            {
                throw new ModuleRuntimeException(
                    $"Cannot load invalid module manifest: {moduleId}");
            }

            record.State = ModuleState.Loading;
            record.LastError = null;

            try
            {
                record.Handle = await loader.LoadAsync(
                    record.DiscoveredModule,
                    dataRootDirectory,
                    cancellationToken);
                record.State = ModuleState.Loaded;
            }
            catch (Exception exception)
            {
                SetFailure(record, exception);
                throw WrapFailure($"Failed to load module '{moduleId}'.", exception);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task ActivateAsync(
        string moduleId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var record = GetRequiredRecord(moduleId);
            var handle = GetRequiredHandle(record);

            record.State = ModuleState.Activating;
            record.LastError = null;

            try
            {
                await handle.Module.OnActivateAsync(cancellationToken);
                record.State = ModuleState.Running;
            }
            catch (Exception exception)
            {
                SetFailure(record, exception);
                throw WrapFailure($"Failed to activate module '{moduleId}'.", exception);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task DeactivateAsync(
        string moduleId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var record = GetRequiredRecord(moduleId);
            await DeactivateCoreAsync(record, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task UnloadAsync(
        string moduleId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sync.WaitAsync(cancellationToken);
        try
        {
            await UnloadCoreAsync(GetRequiredRecord(moduleId), cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task UnloadAllAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var failures = new List<Exception>();

            foreach (var record in _records.Values.Where(record => record.IsLoaded).ToArray())
            {
                try
                {
                    await UnloadCoreAsync(record, cancellationToken);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count > 0)
            {
                throw new ModuleRuntimeException(
                    $"Failed to unload {failures.Count} module(s).",
                    new AggregateException(failures));
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            await UnloadAllAsync();
        }
        finally
        {
            _isDisposed = true;
            _sync.Dispose();
        }
    }

    private async Task DeactivateCoreAsync(
        ModuleRuntimeRecord record,
        CancellationToken cancellationToken)
    {
        if (!record.IsLoaded || record.State != ModuleState.Running)
        {
            return;
        }

        record.State = ModuleState.Deactivating;
        record.LastError = null;

        try
        {
            await record.Handle!.Module.OnDeactivateAsync(cancellationToken);
            record.State = ModuleState.Deactivated;
        }
        catch (Exception exception)
        {
            SetFailure(record, exception);
            throw WrapFailure(
                $"Failed to deactivate module '{record.Manifest.Id}'.",
                exception);
        }
    }

    private async Task UnloadCoreAsync(
        ModuleRuntimeRecord record,
        CancellationToken cancellationToken)
    {
        if (!record.IsLoaded)
        {
            record.State = ModuleState.NotLoaded;
            return;
        }

        try
        {
            if (record.State == ModuleState.Running)
            {
                await DeactivateCoreAsync(record, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            record.State = ModuleState.Unloading;
            record.LastError = null;

            await record.Handle!.DisposeAsync();
            record.Handle = null;
            record.State = ModuleState.Unloaded;
        }
        catch (Exception exception)
        {
            if (record.Handle?.IsDisposed == true)
            {
                record.Handle = null;
            }

            SetFailure(record, exception);
            throw WrapFailure(
                $"Failed to unload module '{record.Manifest.Id}'.",
                exception);
        }
    }

    private ModuleRuntimeRecord GetRequiredRecord(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        return _records.TryGetValue(moduleId, out var record)
            ? record
            : throw new ModuleRuntimeException($"Module '{moduleId}' was not found.");
    }

    private static LoadedModuleHandle GetRequiredHandle(ModuleRuntimeRecord record)
    {
        return record.IsLoaded
            ? record.Handle!
            : throw new ModuleRuntimeException(
                $"Module '{record.Manifest.Id}' is not loaded.");
    }

    private static void SetFailure(ModuleRuntimeRecord record, Exception exception)
    {
        record.State = ModuleState.Failed;
        record.LastError = exception.Message;
    }

    private static ModuleRuntimeException WrapFailure(string message, Exception exception)
    {
        return exception as ModuleRuntimeException ??
            new ModuleRuntimeException(message, exception);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
