using QingToolbox.Abstractions.Modules;

namespace QingToolbox.ModuleLoader;

public sealed class LoadedModuleHandle : IAsyncDisposable
{
    private IToolModule? _module;
    private InProcessModuleLoadContext? _loadContext;
    private bool _isDisposed;

    internal LoadedModuleHandle(
        ModuleManifest manifest,
        string moduleDirectory,
        IToolModule module,
        InProcessModuleLoadContext loadContext)
    {
        Manifest = manifest;
        ModuleDirectory = moduleDirectory;
        _module = module;
        _loadContext = loadContext;
        LoadContextWeakReference = new WeakReference(loadContext, trackResurrection: false);
    }

    public ModuleManifest Manifest { get; }

    public string ModuleDirectory { get; }

    public IToolModule Module
        => _module ?? throw new ObjectDisposedException(nameof(LoadedModuleHandle));

    public WeakReference LoadContextWeakReference { get; }

    public bool IsDisposed => _isDisposed;

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            if (_module is not null)
            {
                try
                {
                    await _module.OnUnloadAsync();
                }
                finally
                {
                    await _module.DisposeAsync();
                }
            }
        }
        finally
        {
            _module = null;

            if (_loadContext is not null)
            {
                _loadContext.Unload();
                _loadContext = null;
            }
        }
    }
}
