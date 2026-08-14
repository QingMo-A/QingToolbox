# Plan 015 — Qing Launcher module

## Goal

Deliver **Qing Launcher** as a first-class `.qmod` Web module that can stay active in the background and provide a fast, Android-launcher-like application surface without turning QingToolbox Shell into a launcher implementation.

The module owns its own global hotkey, launcher data, icon cache, application launching behavior, Vue pages, and settings. QingToolbox/ModuleHost only supplies the minimum host-owned window and external-drop hooks that a module cannot implement safely by itself.

## Product shape

### Main Vue page

- Top application area shows saved applications as icon + name tiles.
- Sort mode:
  - `custom`: user can drag tiles and insert them between other tiles; the custom order is persisted.
  - `alphabetical`: display by application name while preserving the saved custom order for switching back later.
- External drag-in from Windows Desktop / Explorer adds supported launcher items. V1 targets `.exe` and `.lnk` only.
- Bottom **Recent** section shows distinct applications most recently launched through Qing Launcher, ordered by `lastLaunchedAt` descending.
- Top-right settings button opens an in-module Vue settings view; no Shell settings page is required.

### Settings Vue page

- Configure the module-owned global hotkey.
- Hotkey registration remains inside Qing Launcher, including conflict handling and persistence.
- Changing a hotkey is transactional: if the new registration fails, the previous working hotkey remains registered.
- Existing QingToolbox module enable/startup controls remain the source of truth for whether the module is enabled and whether it starts with QingToolbox; Plan 015 does not reimplement those controls.

## Module ownership

Qing Launcher owns:

- `RegisterHotKey` / `UnregisterHotKey` and WM_HOTKEY handling.
- Launcher item persistence under the module data directory.
- `.exe` / `.lnk` metadata resolution.
- application launch via native C# backend.
- icon extraction and cached PNG assets.
- custom ordering, alphabetical projection, recent ordering.
- Vue UI, appearance/language handling, drag/reorder UX, settings page.

The module must not ask Shell to maintain a global shortcut registry for it.

## 015A — Minimal host interaction hooks

Before implementing the module, add only two direct prerequisites on `toolbox`:

1. **Self window actions** for a Web module backend: `Show`, `Hide`, `Toggle` its own ModuleHost-owned window.
   - ModuleHost remains the only owner/creator of `WebModuleWindow`.
   - `Hide` does not close the window or deactivate the module.
   - `Toggle` shows/activates a hidden/missing window or hides a visible one.
   - Host suspend/restore semantics remain authoritative and cannot be bypassed.

2. **External file drop sink** for a Web module backend.
   - Capture real Windows paths from an explicit Explorer/Desktop drag into the module window.
   - Normalize, deduplicate, existence-check, and bound the path list.
   - Deliver paths to an optional C# module interface, not directly to JavaScript.
   - Do not create a general file-system bridge or expose arbitrary host objects.

Use optional interfaces/events so existing Web/WPF modules remain unchanged. Do not add a new IPC protocol version, generic HostService, or capability framework.

## 015B — Qing Launcher V1

Implement `modules/Launcher` as an out-of-process Web module using the existing `IWebToolModule` bridge and Web presentation contract.

Suggested persisted model:

```json
{
  "sortMode": "custom",
  "hotkey": {
    "ctrl": true,
    "alt": true,
    "shift": false,
    "win": false,
    "key": "Space"
  },
  "items": [
    {
      "id": "...",
      "name": "Steam",
      "source": "C:\\Users\\...\\Desktop\\Steam.lnk",
      "target": "C:\\Program Files (x86)\\Steam\\steam.exe",
      "arguments": "",
      "workingDirectory": "",
      "customOrder": 0,
      "iconKey": "...",
      "lastLaunchedAt": null
    }
  ]
}
```

Implementation notes:

- Resolve `.lnk` metadata on add so deleting the original desktop shortcut does not necessarily invalidate the launcher entry.
- Cache application icons under the module data directory; do not re-extract all icons whenever the Vue page opens.
- Vue launches by stable item ID; JavaScript must not send arbitrary executable paths to a generic launch API.
- Successful launch updates `lastLaunchedAt` and therefore the Recent section.
- Recent is a projection of launcher items, not a separate unbounded launch-history database.
- In custom mode, drag-and-drop reordering should behave like an Android launcher: nearby tiles make space and the dragged tile can be inserted between them.
- In alphabetical mode, dragging is disabled and the stored custom order remains untouched.
- The module hotkey toggles the same ModuleHost Web window. Esc may request `Hide`; title-bar X remains a real close and a later hotkey can recreate the window.

## Appearance and localization

- Vue frontend follows host `appearancePresetId` and resolved `languageCode` through the established Web module presentation contract.
- Support the five current QingToolbox appearances: `qing-default`, `neon-circuit`, `greenline`, `aurora-flow`, `qing-nova`.
- Keep `en-US` and `zh-CN` resource files as the module's localization source.
- Do not create a new shared npm design-system package solely for this module.

## V1 scope limit

V1 intentionally does **not** include:

- Start Menu / installed-program automatic indexing.
- usage-frequency ranking.
- fuzzy-search engine or command palette.
- categories/folders/groups.
- URL, document, BAT/CMD, UWP or Steam-URI launcher types.
- cloud sync.
- a Shell-managed shortcut registry.
- multiple launcher windows or arbitrary window manipulation.
- a generic filesystem/native bridge.

These may be considered later only when real usage demonstrates the need.

## Validation

### 015A

- Existing Web modules, especially QingTransfer, still open/close/reopen normally.
- Module-owned Show/Hide/Toggle works from a background-origin event without cross-thread UI access.
- Real Explorer/Desktop file drop reaches the optional backend sink with normalized absolute paths and does not navigate/open the dropped file in WebView2.

### 015B

- `.exe` and `.lnk` can be added by external drag-in.
- custom drag insertion persists across reopen.
- alphabetical mode sorts without destroying custom order.
- launch by item ID succeeds and Recent updates by launch time.
- icon cache survives reopen.
- global hotkey can show/hide the launcher while the module remains active.
- failed replacement hotkey registration preserves the previous working hotkey.
- five appearances and both supported languages update without reopening.
- `.qmod` package contains backend, i18n and `ui/` assets without source/node_modules leakage.

## Stop rule

After 015A is proven and Qing Launcher V1 passes the user-visible flow above, freeze the host hooks. Do not expand them into a general module command bus, filesystem API, shortcut manager, or additional window protocol unless another concrete module requirement proves necessary.
