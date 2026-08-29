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
ordering (including Desktop refreshes), and launch-by-id. The old WPF Launcher remains a separate legacy package while
Everything, global hotkey and Explorer drop handling are migrated onto this
protocol.
