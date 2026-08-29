# Qing Launcher (Tauri process module)

This is the first product module rewritten for the Tauri host. It is an
independent Rust process speaking `qing.module/1` over newline-delimited JSON.
The module owns its JSON store and desktop projection; the host owns process
creation, the module window, and the path boundary.

The UI calls `get_module_window_context` and `invoke_module_window`. It never
sends an executable path to the host. `launchItem` accepts only an item id that
the module previously returned from its own state snapshot.

The current migration slice covers `.exe`/`.lnk`/`.url` items visible on the
user Desktop, custom/alphabetical/desktop projections, persistent custom
ordering (including Desktop refreshes), launch-by-id, and custom-mode folders
with bounded rename/move/order operations. Folder state is module-owned and
stored atomically beside the launcher data.

The search box also supports an isolated Everything mode. `/e query`,
`/e:f query`, and `/e:d query` are sent to a fixed Everything 1.4.1.1032
portable runtime bundled below `third-party/Everything`; normal Launcher,
Desktop and Recent projections are never mixed into those results. The module
uses a private named instance and (on first use) a dedicated
`QingToolboxLauncher` service/pipe. It reuses only that instance, leaves a
user's own Everything process alone, and shuts down only a client process it
created. The package includes the upstream license and pinned notice; the
build never downloads a moving `latest` asset.

Everything result paths stay in a backend-only id map. The Vue page can request
open/open-folder/copy-path only with a current opaque `resultId`, so it cannot
turn an arbitrary web-provided path into a process or shell operation. If the
runtime, index or IPC is unavailable, the UI shows a contained error and the
ordinary Launcher search remains usable. The old WPF Launcher remains a
separate legacy package while the remaining hotkey, icon extraction and
Explorer-drop parity are migrated onto this protocol.

The bounded integration smoke uses `-Everything` with a temporary indexed
folder. On a machine where the dedicated service has already been authorized,
`-EverythingService` exercises the production service connection against a
fixed-volume runtime file without changing the user's Everything settings.
