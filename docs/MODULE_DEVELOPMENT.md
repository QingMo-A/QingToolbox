# Module Development

Modules depend only on `QingToolbox.Abstractions`. They must not reference the
Shell, Core, or concrete host implementation.

## Localization

There are two localization paths:

1. `module.json` localization controls module card metadata such as
   `module.name` and `module.description`.
2. Module UI localization uses `ModuleContext.Localization`, which is available
   after `OnLoadAsync(ModuleContext context)` is called.

Minimal pattern:

```csharp
private ModuleContext? _context;

public Task OnLoadAsync(
    ModuleContext context,
    CancellationToken cancellationToken)
{
    _context = context;
    return Task.CompletedTask;
}

public object? CreateView()
{
    return new MyModuleView(_context!.Localization, _context.ModuleId);
}
```

In the View:

```csharp
public sealed class MyModuleView : UserControl, ILocalizedModuleView
{
    private readonly ILocalizationService _localization;
    private readonly string _moduleId;

    public MyModuleView(
        ILocalizationService localization,
        string moduleId)
    {
        _localization = localization;
        _moduleId = moduleId;
        RefreshLocalization();
    }

    public void RefreshLocalization()
    {
        title.Text = _localization.GetModuleString(
            _moduleId,
            "view.title",
            "My Module");
    }
}
```

If a View subscribes to `CultureChanged` itself, unsubscribe when the View is
unloaded or disposed:

```csharp
_localization.CultureChanged += OnCultureChanged;
Unloaded += (_, _) => _localization.CultureChanged -= OnCultureChanged;
```

This avoids keeping the module alive after unload and preserves collectible
AssemblyLoadContext behavior.

Recommended i18n keys:

- `module.name`
- `module.description`
- `view.title`
- `actions.xxx`
- `status.xxx`
- `errors.xxx`

Refresh Modules reads manifests and i18n JSON files only. It does not load
module DLLs. Module UI localization starts only after a module is loaded and its
View is created.
