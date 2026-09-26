# Module Development

> **Status (2026-09-26)**
>
> - **Current track — Tauri process modules.** The host is `QingToolbox.Tauri` (Rust + Tauri 2 + Vue 3).
>   Modules are independent OS processes that speak the versioned protocol in [`../protocol/README.md`](../protocol/README.md).
>   Start with [Current track](#current-track-tauri-process-modules).
> - **Legacy track — WPF in-process modules.** The `.NET` APIs described in
>   [Legacy track](#legacy-track-wpf-in-process-modules) belong to the retired WPF host.
>   The Tauri host does not load them, and manifests declaring `runtimeType` other than `Process` are rejected.
> - Module source for the legacy track lives on the [`modules` branch](https://github.com/QingMo-A/QingToolbox/tree/modules),
>   not on `toolbox`.

---

## Current track: Tauri process modules

A Tauri module is a standalone executable plus an optional Vue surface. The host owns the process, the paths,
and every security decision; the module owns its own state machine and system capabilities.

### 1. Wire contract

Read [`../protocol/README.md`](../protocol/README.md) first. In short:

- One UTF-8 JSON object per line over host-created stdin/stdout pipes. A frame is capped at **1 MiB**.
- The host generates an OS-RNG nonce per module start and passes it over the private process boundary.
  The module must echo it in `module.hello.response`; only then does the host send `activate`, `deactivate`, or `shutdown`.
- `invoke` is module-specific. Any path, process, or window operation must be represented by a host-defined
  command plus an opaque ID — never by a user-supplied string.
- The host forwards an operation **only** when it appears in the manifest `operations` array. An omitted or
  empty array exposes no invoke surface. The same rule applies to `events`.
- Unknown operations must return an error without terminating the module process.

### 2. Lifecycle contract

Read [`TAURI_MODULE_LIFECYCLE.md`](TAURI_MODULE_LIFECYCLE.md). Residency and activation are separate concerns:

| Action | Result | Background work |
| --- | --- | --- |
| Load | Loaded; process and state resident | Off |
| Enable | Running; same process and generation | On |
| Disable | Deactivated; same process and generation | Off |
| Unload | Stopped; process and Web surface released | Off |
| Delete | Unload, then remove the validated user-package directory | Off |

Key rules that are easy to get wrong:

- Constructors and the `hello` exchange may only initialize resident data. **No** background listener,
  discovery, or recurring monitor may start there.
- `Disable` must cancel or drain continuous work and timers, but keep settings and caches. Operations that
  would restart continuous work must reject while inactive, and re-enable must work **without** restarting the process.
- A failed acknowledgement must never be projected as a successful disable. Protocol corruption or timeout
  is a visible failure, not a disable.
- Modules without the lifecycle contract fail clearly; the host will not pretend that terminating an old
  module is a disable operation.

### 3. Manifest

The authoritative schema is [`../protocol/module-manifest.tauri.v1.schema.json`](../protocol/module-manifest.tauri.v1.schema.json).
Required fields: `id`, `name`, `version`, `entry`, `runtimeType`, `runtimeIsolation`, `loadMode`.

Minimal example:

```json
{
  "id": "qing.example",
  "name": "Example",
  "description": "Example Tauri process module.",
  "version": "0.1.0",
  "author": "your-name",
  "entry": "bin/qing-example.exe",
  "runtimeType": "Process",
  "runtimeIsolation": "OutOfProcess",
  "uiKind": "Web",
  "webEntry": "ui/index.html",
  "loadMode": "Manual",
  "permissions": ["FileRead"],
  "operations": ["getState", "doSomething"],
  "events": [],
  "icon": "icon.svg"
}
```

Field notes:

- `id` must match `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`. The install directory must be named **exactly** after it.
- `entry` must be a relative `.exe` path. Absolute paths, traversal, and drive-colon forms are rejected.
- `runtimeType` must be `Process` and `runtimeIsolation` must be `OutOfProcess`. The legacy host's DLL
  manifests are rejected rather than attempted.
- `uiKind` is `None` or `Web`. When it is `Web`, `webEntry` is required and must be a relative `.html` path.
- `loadMode` is `Manual` or `Startup`. `Startup` still requires explicit user authorization at runtime.
- `permissions` (`FileRead`, `FileWrite`, `ProcessStart`, `Network`, `Clipboard`, `WindowControl`) is a
  **declaration for user awareness** — it is not an enforced sandbox.
- `icon` must be a module-relative SVG below the module root. Absolute paths, traversal, missing files,
  unsupported formats, and reparse points are rejected or fall back to the host icon.

### 4. Module Web window

A Web UI is served over the host-owned `qmod://` protocol into a separate Tauri window labelled
`module-<moduleId>`. The page may call three host commands:

```ts
import { invoke } from '@tauri-apps/api/core'

export const context = () => invoke('get_module_window_context')
export const call = (method: string, payload: Record<string, unknown> = {}) =>
  invoke('invoke_module_window', { method, payload })
export const hide = () => invoke('hide_module_window')
```

Rules:

- `invoke_module_window` and `hide_module_window` derive the module identity from the **window label**.
  A page can never supply an arbitrary module ID or filesystem path, and the manifest `operations`
  allowlist is still applied before the Rust runtime forwards the request.
- `get_module_window_context` returns the module ID, name, version, validated icon, protocol version, and
  operation names. It never returns the module directory, executable path, or data directory.
- Wait for the host-owned presentation context before rendering language- or appearance-sensitive content.
  Do not implement a second module splash screen — the host owns the startup surface.
- The `module-*` capability grants only Tauri core IPC. Filesystem and process permissions are **not**
  inherited by the WebView.

### 5. Build and package

```powershell
# Package a single module
pwsh ./scripts/package-tauri-module.ps1 -ModuleId qing.example -Smoke

# Package all official modules
pwsh ./scripts/package-tauri-modules.ps1 -SkipBuild -Smoke
```

Outputs land in `artifacts/tauri-modules/` as `<id>-<version>-tauri.qmod` plus a matching `.sha256`.
The root-level `qmod.json` and `module.json` are cross-checked, and the importer re-validates identity,
version, and directory boundaries before publication.

Use [`../QingToolbox.Tauri/native-module-canary/`](../QingToolbox.Tauri/native-module-canary/) as the
reference implementation: it is a minimal, reproducible Rust process module that exercises the
nonce-bound `hello → invoke → shutdown` cycle.

### 6. Deployment and diagnosis

- Install the payload as a direct child named exactly after the manifest ID:
  `<UserModulesRoot>/<moduleId>`. For example `qing.qingtransfer` — **not** `QingTransfer`.
- Replace the complete payload while the host and the module's process are stopped or the module is unloaded.
  Do not mix an old manifest with a new executable, and do not retain stale `ui/` assets.
- After deployment, compare the installed manifest, executable, Web entry, and generated asset hashes
  against the package before starting the host.
- The package must contain the manifest, executable, required dependency metadata, icon, localization
  files, the `webEntry`, and every referenced hashed JS/CSS asset. Copying only the executable is not a
  valid deployment.

Common symptoms are intentionally diagnostic:

| Symptom | Usual cause |
| --- | --- |
| Card discovered but Load reports the installed directory is unavailable | Directory is not the exact `<moduleId>` direct child |
| Module is rejected as invalid at discovery | Manifest declares `runtimeType`/`runtimeIsolation` other than `Process`/`OutOfProcess` |
| Web window shows a generic icon | Packaged SVG is missing or failed the module-root safety checks |
| Window flashes white during startup | Host loading surface or paint-ready transition regressed — do not work around it inside each module |

### 7. Localization

Localization has two independent paths:

1. **Manifest metadata** — `module.name`, `module.description` and related card text.
2. **Module UI** — text inside your own Vue surface.

The host supports Simplified Chinese and English. Verify both host languages, light/dark presentation,
reduced motion, window reopening, and narrow-window wrapping before packaging.

---

## Legacy track: WPF in-process modules

> **These APIs belong to the retired WPF host.** They are kept for maintaining historical installations and
> for the `modules` branch. Do not use them for new modules. The Tauri host rejects these manifests.

Modules depend only on `QingToolbox.Abstractions`. They must not reference the Shell, Core, or concrete host
implementation.

### Localization (WPF)

There are two localization paths:

1. `module.json` localization controls module card metadata such as `module.name` and `module.description`.
2. Module UI localization uses `ModuleContext.Localization`, which is available after
   `OnLoadAsync(ModuleContext context)` is called.

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

If a View subscribes to `CultureChanged` itself, unsubscribe when the View is unloaded or disposed:

```csharp
_localization.CultureChanged += OnCultureChanged;
Unloaded += (_, _) => _localization.CultureChanged -= OnCultureChanged;
```

This avoids keeping the module alive after unload and preserves collectible `AssemblyLoadContext` behavior.

Recommended i18n keys: `module.name`, `module.description`, `view.title`, `actions.xxx`, `status.xxx`, `errors.xxx`.

Refresh Modules reads manifests and i18n JSON files only. It does not load module DLLs. Module UI
localization starts only after a module is loaded and its View is created.

### Web module adaptation checklist (WPF host)

Apply this checklist to every module migrated from WPF UI to Web UI **on the WPF host**.

Module author responsibilities:

- Keep `entry` as the module backend assembly. Declare `runtimeIsolation: "OutOfProcess"`, `uiKind: "Web"`,
  and a module-relative `.html` `webEntry` such as `ui/index.html`.
- Build the Web UI before packaging. The package must contain the manifest, backend assembly and required
  dependency metadata, icon, localization files, the `webEntry`, and every referenced hashed JS/CSS asset.
- Wait for the host-owned `hostReady` and presentation context before rendering language- or
  appearance-sensitive content. `NavigationCompleted` is not proof that the page has painted.
- Do not implement a second module splash screen.
- Verify light/dark presentation, all supported appearance presets, both host languages, reduced motion,
  window reopening, and narrow-window wrapping.

Local deployment responsibilities:

- Install the payload as a direct child named exactly after the manifest ID.
- Replace the complete payload while the Development Shell and the module's `ModuleHost` process are
  stopped or the module is unloaded.
- After deployment, compare the installed manifest, assembly, Web entry, and generated asset hashes with
  the package before starting the host.

---

## Related documents

| Topic | Document |
| --- | --- |
| Wire contract | [`../protocol/README.md`](../protocol/README.md) |
| Lifecycle | [`TAURI_MODULE_LIFECYCLE.md`](TAURI_MODULE_LIFECYCLE.md) |
| Architecture and boundaries | [`ARCHITECTURE.md`](ARCHITECTURE.md) |
| Package format | [`QMOD_FORMAT.md`](QMOD_FORMAT.md) |
| Staging security | [`QMOD_STAGING_SECURITY.md`](QMOD_STAGING_SECURITY.md) |
| Module API status | [`sdk/README.md`](sdk/README.md) |
| Environment isolation | [`DEVELOPMENT_ENVIRONMENTS.md`](DEVELOPMENT_ENVIRONMENTS.md) |
