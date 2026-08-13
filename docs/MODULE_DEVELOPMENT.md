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

## Web module adaptation checklist

Web modules use the same backend lifecycle as other modules, but their complete
runtime payload and presentation readiness must be treated as one deployment
unit. Apply this checklist to every module migrated from WPF to Web UI.

### Module author responsibilities

- Keep `entry` as the module backend assembly. Declare
  `runtimeIsolation: "OutOfProcess"`, `uiKind: "Web"`, and a module-relative
  `.html` `webEntry` such as `ui/index.html`.
- Keep `icon` module-relative and point it to a valid SVG below the module root.
  Absolute paths, traversal, missing files, unsupported formats, and reparse
  points are rejected or fall back to the host icon.
- Build the Web UI before packaging. The package must contain the manifest,
  backend assembly and required dependency metadata, icon, localization files,
  the `webEntry`, and every referenced hashed JS/CSS asset. Copying only the DLL
  is not a valid Web module deployment.
- Wait for the host-owned `hostReady` and presentation context before rendering
  language- or appearance-sensitive content. `NavigationCompleted` is not proof
  that the page has painted.
- Do not implement a second module splash screen. The host owns the native
  startup surface and keeps the WebView hidden until its internal paint-ready
  signal is validated.
- Verify light/dark presentation, all supported appearance presets, both host
  languages, reduced motion, window reopening, and narrow-window wrapping.

### Local deployment responsibilities

- Install the payload as a direct child named exactly after the manifest ID:
  `<UserModulesRoot>/<moduleId>`. For example, QingTransfer must be deployed to
  `local/modules/qing.qingtransfer`, not `local/modules/QingTransfer`.
- Replace the complete payload while the Development Shell and the module's
  ModuleHost process are stopped or the module is unloaded. Do not mix an old
  manifest with a new DLL or retain stale `ui/` assets.
- After deployment, compare the installed manifest, assembly, Web entry, and
  generated asset hashes with the package before starting the host.

Common symptoms are intentionally diagnostic:

- A card is discovered but Load reports that the installed directory is
  unavailable: the directory is usually not the exact `<moduleId>` direct child.
- The old WPF view still opens: the installed manifest is stale or lacks the Web
  declarations, or the old module remained loaded during replacement.
- The Web window has a generic icon: the packaged SVG is missing or failed the
  module-root safety checks.
- The window flashes white during startup: the host loading surface or internal
  paint-ready transition regressed; do not work around it inside each module.
