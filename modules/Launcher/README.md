# Qing Launcher (Tauri process module)

This is the first product module rewritten for the Tauri host. It is an
independent Rust process speaking `qing.module/1` over newline-delimited JSON.
The module owns its JSON store and desktop projection; the host owns process
creation, the module window, and the path boundary.

The UI calls `get_module_window_context` and `invoke_module_window`. It never
sends an executable path to the host. `launchItem` accepts only an item id that
the module previously returned from its own state snapshot.

The current migration slice covers `.exe`/`.lnk`/`.url` items visible on the
user and public Desktop, custom/alphabetical/desktop projections, persistent custom
ordering (including Desktop refreshes), launch-by-id, and custom-mode folders
with bounded rename/move/order operations. Folder state is module-owned and
stored atomically beside the launcher data.

Desktop roots are resolved on every scan/refresh through Windows
`SHGetKnownFolderPath(FOLDERID_Desktop/FOLDERID_PublicDesktop)` with default
flags. This follows drive/OneDrive redirection and localized directory names;
it does not scan stale `USERPROFILE/Desktop` or guessed OneDrive folders.
Duplicate paths are removed, a failing root does not block other roots, and
resolution/read failures are written to module stderr for host diagnostics.
This remains an application/shortcut projection, not a listing of ordinary
desktop documents, directories or virtual Shell icons.

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
ordinary Launcher search remains usable. Windows Shell icons are extracted
into bounded high-resolution PNGs, cached separately from launcher state. The
old WPF Launcher remains a separate legacy package and is never loaded by the
Tauri host. Explorer drops are accepted only by the native Tauri window
boundary, canonicalized there, and delivered as the manifest-declared
`launcher.externalDrop` event. The module hotkey is persisted here while its
global registration and window toggle stay owned by the Rust host.

Launcher Settings also persist an Everything result limit (default 200,
1–1000) and opt-in batch loading (default off). With batching off the limit
applies to the initial results; with batching on it is the page size. ES
exports a bounded full-path-sorted viewport and the backend owns continuation
cursors, preserving previously returned result IDs until the query changes.
Every page stays below the host's 1 MiB frame budget; a search is capped at
5000 retained results / 8 MiB of retained paths. The UI can load the next batch
by scrolling or clicking Load More, and stale pages cannot replace a new query.
Typing `/` suggests `/e`, `/e:f`, `/e:d`; mouse, arrow keys, Tab and Enter
complete the chosen prefix with a trailing space.

Launcher 0.3.4 adds an opt-out cache (on by default). Settings provides an
Enable Cache checkbox and Clear Cache button. Everything query pages persist
for 60 seconds (64 entries / 16 MiB); Shell icons persist for 10 minutes
(512 entries / 64 MiB), keyed by target path, file size and modification time.
Files live only in `launcher-cache-v1/{everything,icons}` below the module's
data directory. Cache failures fall back to normal queries/extraction; disabled
caching bypasses both disk and in-memory reuse. Clear removes only cache files,
not apps, folders, settings or Everything's own managed database. Query hits
receive fresh session IDs; expired or missing-file pages query Everything again.
The live Everything readiness badge is never inferred from a cached search.
The settings backdrop is clipped to the rounded launcher panel with a smaller
shadow, and cold-mount/native-show entrance animations are coalesced without
resetting an already visible panel to transparent.

The bounded integration smoke uses `-Everything` with a temporary indexed
folder. On a machine where the dedicated service has already been authorized,
`-EverythingService` exercises the production service connection against a
fixed-volume runtime file without changing the user's Everything settings.
