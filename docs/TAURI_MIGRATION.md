# QingToolbox Tauri migration

> Status: native host and official module slices validated; production cut-over
> gates remain (2026-08-30)

This document records the user-directed Tauri rewrite track. It supersedes the
"no Tauri rewrite" non-goal in the earlier hybrid UI planning document; that
plan remains the historical design record for the WPF/WebView2 production
track, which is intentionally kept intact until this migration reaches parity.

## Goal

QingToolbox is moving to a small Rust/Tauri host with a Vue front end. The
host, rather than an individual module, is the primary migration target. The
existing WPF host remains available while the new host reaches feature parity;
this document describes the target contract and the order in which the old
implementation is replaced.

This is a platform migration, not a source-to-source translation. New
modules must not depend on the old .NET assembly ABI. Existing modules may be
rewritten against the new protocol when they are migrated.

## Target boundaries

```text
Tauri application
  Rust core
    single-instance / tray / windows / hotkeys
    settings / permissions / module registry
    module process supervisor
    updater and package verification
  Vue UI
    shell navigation and module surfaces
    no direct filesystem or process access

Module process (one per active module)
  versioned JSON lines protocol over a private local transport
  module-owned backend + Vue assets
```

The Rust core owns paths, process creation, module state and all security
decisions. Vue receives opaque IDs and typed snapshots; it never supplies an
arbitrary path to an operating-system operation.

## Versioned module protocol

The first protocol version is deliberately small. Every request and event has
an explicit `protocolVersion` value (`1`) and a message type. Unknown
operations must return an error without terminating the module process.

Requests:

- `hello` — negotiate protocol and return module metadata.
- `activate` / `deactivate` — change the module lifecycle state.
- `snapshot` — return the current serializable state.
- `invoke` — call a module operation using a JSON object payload.
- `shutdown` — ask the module to exit; the host owns the final kill timeout.

Events:

- `state` — lifecycle transition and diagnostic status.
- `event` — module-defined event payload.
- `ready` — UI entry is ready to be shown.

The host assigns a monotonically increasing request ID and rejects oversized
frames, malformed JSON, duplicate IDs and messages received after shutdown.
Payload limits and the envelope shape are defined in
`protocol/module-protocol.v1.schema.json`.

## Package policy

The `.qmod` container and module IDs remain the user-facing package format.
During migration, the required entry is a protocol-aware executable (or a
platform-specific sidecar bundle) rather than a .NET DLL. The importer keeps
the existing path traversal, size and atomic-install checks. A future schema
revision will add the runtime kind without changing the package extension.

The following values are preserved when user data is migrated:

- `%APPDATA%\\QingToolbox\\settings.json`
- `%APPDATA%\\QingToolbox\\Data`
- `%LOCALAPPDATA%\\QingToolbox\\Modules`
- module IDs, versions and load-mode choices

The Tauri host must read the existing data first and write only after a
successful migration marker is recorded. No destructive cleanup is part of
the first release.

## Migration phases

### M0 — foundation (implemented)

- Add the Tauri host and Vue shell.
- Define and test the protocol envelope and path boundaries.
- Add a reproducible native canary and desktop startup smoke checks.
- Record WPF baseline metrics: cold start, idle RSS, hidden/resume latency and
  module launch latency. (The comparison dataset is still pending.)

### M1 — host core (settings and import slice implemented)

- Implement single-instance locking, tray, hide/show and close-to-tray behavior
  in Rust.
- Implement process-profile discovery and a backend-owned module index without
  loading module code.
- Keep process handshake deadlines and child cleanup in a Rust supervisor loop;
  Vue status polling is informational only.
- Add capability files with only the explicit dialog file-picker permission;
  Vue still cannot perform filesystem or process operations. Rust now owns the
  typed host settings snapshot and atomic persistence at the shared settings
  path, while preserving unknown legacy fields and bounded corrupt backups.
- Add a bounded `.qmod` importer for the new process profile. It validates the
  ZIP before extraction, stages below the backend-selected user module root,
  validates the extracted manifest without executing it, and publishes with an
  atomic same-volume rename. Existing IDs are rejected instead of replaced.
- Apply the persisted startup presentation and close behavior in the native
  window lifecycle. A first-run Tauri profile opens the main window; tray and
  exit paths remain explicit settings, and the `ask` path uses a native dialog.
- Register the host toggle shortcut in Rust through the fixed Tauri global
  shortcut plugin. The Vue shell only edits the bounded preference; a failed
  registration is diagnostic and does not prevent the host from starting.
- Synchronize the typed `launchAtLogin` preference through the fixed Tauri
  autostart plugin. Release builds repair the current-user startup entry during
  setup; debug/smoke builds require an explicit opt-in environment variable so
  validation never mutates a developer's login configuration accidentally. A
  smoke process can force-disable synchronization with
  `QING_TAURI_DISABLE_AUTOSTART_SYNC=1`, including when it exercises a Release
  executable.
- Own font selection and import in Rust. The host exposes a fixed, safe system
  font catalog plus hash-addressed user font records, persists only the selected
  opaque ID, and serves verified `.ttf`/`.otf`/`.ttc` bytes through the private
  `qfont://` protocol. The Vue surface never submits a path to an operating
  system operation; missing or tampered imported assets fall back to the default
  font.
- Persist a bounded list of module IDs authorized to start with the host. The
  module page can toggle only a discovered manifest ID; startup initialization
  re-discovers the backend-owned module index and starts only those records.
- Expose a bounded, in-memory session log snapshot from Rust. The existing Vue
  logs page now reads `get_session_logs`; operational events are recorded without
  returning backend paths, process handles, or arbitrary frontend data.
- Broadcast bounded runtime-state changes from the Rust supervisor to the main
  window. The Vue bridge coalesces overlapping events and re-reads authoritative
  app/module snapshots, so child exit or failure does not require manual refresh.

### M2 — native product modules (validated migration slice)

- `native-launcher/` now provides the first Rust process profile and a Vue
  surface. Its state store, Desktop projection, custom/alphabetical/desktop
  ordering (including order retention across Desktop refreshes) and
  launch-by-id path map are module-owned.
- The host exposes only `get_module_window_context`,
  `invoke_module_window` and `hide_module_window` to a `module-*` window. The
  window label supplies the module identity; a page cannot select another
  module or submit an executable path.
- Launcher search parity now includes a bounded Everything bridge. The module
  bundles the fixed, license-preserving Everything 1.4.1.1032 runtime, owns a
  private named instance/service pipe, and returns only opaque result IDs for
  open/copy actions. `/e`, `/e:f` and `/e:d` are mutually exclusive with the
  Launcher/Desktop/Recent projections; stale Vue requests are ignored. Runtime
  or IPC failure is contained in the Everything result surface and does not
  change normal Launcher search. Windows Shell icons are projected as bounded,
  session-local PNG data URLs with a frame-level aggregate budget. Explorer drop
  is canonicalized at the Tauri native window boundary and delivered only as the
  declared `launcher.externalDrop` event; the module hotkey is registered by the
  Rust host and can be edited from the module page without a browser key hook.
  Drop events receive a correlated module acknowledgement before the host emits
  the path-free WebView invalidation event, so a refresh cannot race the module's
  atomic state write.
  The old WPF Launcher is not loaded by this host.

- `native-pdf/` is a Rust process module with a Vue surface. It keeps qpdf
  12.4.1 in a checked-in, hash-pinned module-owned runtime and performs merge,
  balanced split, page extraction and rotation through bounded native
  operations. Input and output paths are canonicalized in Rust, outputs are
  written through a temporary file and atomically replaced, and generated
  results are opened through opaque IDs.

- `native-transfer/` is a Rust process module with a Vue surface. It owns
  DNS-SD discovery, a nonce-bound TCP endpoint probe, connection approval,
  bounded JSON control frames, SHA-256 verified file streaming and atomic
  receive-file publication. Discovery or network failures stay in module
  state and do not make arbitrary filesystem or process operations available
  to the WebView.

- `native-texttools/` is a bounded Rust text-processing module with a Vue
  surface. JSON/Base64/URL/case transforms and clipboard writes are backend
  operations; no general clipboard or filesystem primitive is exposed.

- `native-windowtopmost/` enumerates ordinary visible windows in Rust and
  exposes only short-lived opaque IDs to Vue. HWND values are retained in the
  module process and revalidated immediately before `SetWindowPos`.

- `native-powerguard/` owns bounded connectivity probes, outage/recovery state,
  countdown timing and settings persistence. The guard is disabled by default;
  the shutdown operation is confirmation-gated and no arbitrary command or
  network target is accepted from Vue.

- `native-screenpin/` owns bounded Windows GDI capture and PNG encoding. The
  WebView receives only session-scoped data URLs and opaque pin IDs; it cannot
  invoke a screen API or supply a filesystem path. A pin can be promoted to a
  dedicated always-on-top Tauri window through a host-issued token URL; the host
  closes and releases those windows when the pin or module is removed.

### M3 — production hardening remaining

- A reproducible unsigned Inno Setup installer candidate now consumes the same
  production directory and manifest as the portable path. It has an isolated
  migration AppId, preserves user data outside the install directory, verifies
  every staged file and runs an install/uninstall smoke. It is intentionally not
  the public installer yet: signed installer/bundle, updater integration, AppId
  cut-over and the production parity checklist remain.
- Complete signed installer/bundle and updater integration, then run the
  production parity checklist on every official module. Portable and production
  directory builders already use the same release executable and resource
  manifest, so this phase is packaging and trust hardening rather than another
  host rewrite.
- Run heavy runtimes (Everything/qpdf) as module-owned child processes or
  sidecars and close only instances created by the module. Qing Launcher now
  follows this rule for its private Everything client; its dedicated service
  remains independently named and is never used to terminate a user's own
  Everything instance.

- The repository's default `run-latest.bat` now launches the Tauri Release
  candidate. The legacy WPF maintenance path is explicit (`run-legacy-wpf.bat`)
  and is not referenced by the Tauri host or its module packages.

The current migration slice has passed the repository's full Windows validation
command (`scripts/verify-tauri.ps1 -BuildDesktop -SmokeDesktop
-SmokeEverything`), including all eight native module smoke suites, host Rust
tests, Vue typecheck/build, Release resource staging and the module-window IPC
smoke. This is a validated engineering candidate, not yet the signed public
installer described in M3.

### M4 — retire WPF

- Compare the baseline metrics with the Tauri build.
- Remove the WPF shell and old in-process loader only after all official
  modules pass the parity checklist.

## Non-goals for the first Tauri release

- A Rust/C# FFI compatibility layer.
- A new general-purpose filesystem bridge.
- A plugin marketplace or remote code execution service.
- Copying the old WPF view-model hierarchy into Rust.

## Acceptance gates

The foundation is ready for module migration when all of the following are
true:

1. `cargo check` and the Vue typecheck/build pass in a clean checkout.
2. The host can start, hide, restore and exit without leaving a child process.
3. The checked-in canary can complete a nonce-bound `hello → shutdown` cycle;
   activate/snapshot operations are added with the first product module.
4. An invalid or oversized frame is rejected and does not crash the host.
5. Vue cannot invoke an arbitrary path or executable; only backend-issued IDs
   are accepted.
6. Existing settings and `.qmod` files remain readable by the legacy host; the
   new host accepts only the validated Tauri process profile and does not load
   legacy DLLs.
7. A real Tauri desktop smoke opens the module Web window through `qmod://`,
   completes the Vue-to-Rust module invoke, and closes the child window without
   requiring a second host instance.
