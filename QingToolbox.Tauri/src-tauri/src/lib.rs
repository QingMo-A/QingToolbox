use std::{
    sync::{
        atomic::{AtomicBool, Ordering},
        Mutex,
    },
    thread,
    time::Duration,
};

use serde::Serialize;
use serde_json::Value;
use tauri::{
    menu::MenuBuilder, tray::TrayIconBuilder, webview::WebviewWindow, Manager, State, WindowEvent,
};
use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};

mod importer;
mod modules;
mod paths;
pub mod protocol;
mod runtime;
mod settings;
mod web;

use importer::{import_qmod, ModuleImportResult};
use modules::{discover_modules, ModuleListPayload};
use paths::{resolve_module_roots, ModuleRoot};
use protocol::ProtocolEnvelope;
use runtime::{ModuleRuntimeManager, ModuleRuntimeSnapshot, RuntimeError};
use settings::{SettingsSnapshot, SettingsStore, SettingsUpdate};
use web::{open_module_window, serve_module_asset};

/// Process-wide state owned by the Rust host. Paths and module records stay on
/// this side of the IPC boundary; the Vue layer only receives stable ids and
/// display metadata.
pub struct HostState {
    roots: Vec<ModuleRoot>,
    module_index: Mutex<std::collections::BTreeMap<String, modules::ModuleRecord>>,
    scan_gate: Mutex<()>,
    runtime: Mutex<ModuleRuntimeManager>,
    settings: Mutex<SettingsStore>,
    close_prompt_active: AtomicBool,
}

impl HostState {
    pub fn new() -> Self {
        Self {
            roots: resolve_module_roots(),
            module_index: Mutex::new(std::collections::BTreeMap::new()),
            scan_gate: Mutex::new(()),
            runtime: Mutex::new(ModuleRuntimeManager::new()),
            settings: Mutex::new(SettingsStore::new()),
            close_prompt_active: AtomicBool::new(false),
        }
    }
}

impl Default for HostState {
    fn default() -> Self {
        Self::new()
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct HostInfo {
    product_name: &'static str,
    version: &'static str,
    backend: &'static str,
    protocol_version: &'static str,
}

/// The first command in the new host contract. Keep commands narrow and typed;
/// filesystem/process operations will be added behind explicit capabilities.
#[tauri::command]
fn get_host_info() -> HostInfo {
    HostInfo {
        product_name: "QingToolbox",
        version: env!("CARGO_PKG_VERSION"),
        backend: "rust",
        protocol_version: "1",
    }
}

#[tauri::command]
fn hide_to_tray(window: WebviewWindow) -> Result<(), CommandError> {
    ensure_main_window(&window)?;
    window.hide().map_err(|error| CommandError {
        code: "windowUnavailable",
        message: format!("无法隐藏工具箱窗口：{error}"),
    })
}

#[tauri::command]
fn get_settings(
    state: State<'_, HostState>,
    window: WebviewWindow,
) -> Result<SettingsSnapshot, CommandError> {
    ensure_main_window(&window)?;
    let settings = state.settings.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "工具箱设置状态不可用。".to_string(),
    })?;
    Ok(settings.snapshot())
}

/// Apply a bounded, typed settings patch. The frontend cannot provide a
/// destination path or arbitrary JSON document; Rust keeps the canonical file
/// location and performs an atomic replacement.
#[tauri::command]
fn update_settings(
    state: State<'_, HostState>,
    window: WebviewWindow,
    update: SettingsUpdate,
) -> Result<SettingsSnapshot, CommandError> {
    ensure_main_window(&window)?;
    let mut settings = state.settings.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "工具箱设置状态不可用。".to_string(),
    })?;
    settings.update(update).map_err(|error| CommandError {
        code: error.code,
        message: error.message,
    })
}

/// Import a user-selected `.qmod` package. The path is accepted only for this
/// explicit file-import operation; the importer validates the archive and
/// publishes a new process-profile module without executing it. Module launch
/// commands continue to use manifest-owned records and opaque ids.
#[tauri::command]
fn import_module(
    state: State<'_, HostState>,
    window: WebviewWindow,
    source_path: String,
) -> Result<ModuleImportResult, CommandError> {
    ensure_main_window(&window)?;
    let result = import_qmod(&source_path).map_err(|error| CommandError {
        code: error.code,
        message: error.message,
    })?;
    // Refresh the in-memory discovery index so the newly imported module is
    // immediately visible. A successful package publication remains valid even
    // if this best-effort UI index refresh cannot acquire its mutex.
    let discovery = discover_modules(&state.roots);
    if let Ok(mut index) = state.module_index.lock() {
        *index = discovery.records;
    }
    Ok(result)
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct CommandError {
    code: &'static str,
    message: String,
}

fn ensure_main_window(window: &WebviewWindow) -> Result<(), CommandError> {
    if window.label() == "main" {
        Ok(())
    } else {
        Err(CommandError {
            code: "mainWindowUnauthorized",
            message: "该操作只能由工具箱主窗口发起。".to_string(),
        })
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ModuleWindowContext {
    module_id: String,
    name: String,
    version: String,
    icon_data_url: Option<String>,
    protocol_version: u16,
    operations: Vec<String>,
}

impl From<RuntimeError> for CommandError {
    fn from(error: RuntimeError) -> Self {
        Self {
            code: error.code,
            message: error.message,
        }
    }
}

/// Discover manifests only. This command never loads a DLL, starts a module,
/// or accepts a path from the frontend.
#[tauri::command]
fn list_modules(
    state: State<'_, HostState>,
    window: WebviewWindow,
) -> Result<ProtocolEnvelope<ModuleListPayload>, CommandError> {
    ensure_main_window(&window)?;
    let _scan_guard = state.scan_gate.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "模块扫描状态不可用。".to_string(),
    })?;

    let discovery = discover_modules(&state.roots);
    let request_id = format!("scan-{}", discovery.payload.scanned_at_unix_ms);
    let envelope = ProtocolEnvelope::new("modules.list", request_id, discovery.payload);
    envelope.validate().map_err(|error| CommandError {
        code: error.code,
        message: error.message,
    })?;

    let mut index = state.module_index.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "模块索引状态不可用。".to_string(),
    })?;
    *index = discovery.records;

    Ok(envelope)
}

pub(crate) fn valid_module_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .next()
            .is_some_and(|byte| byte.is_ascii_alphanumeric())
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_'))
}

fn valid_operation_name(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 64
        && value
            .bytes()
            .next()
            .is_some_and(|byte| byte.is_ascii_alphanumeric())
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_' | b':'))
}

/// Start only the executable recorded in a previously discovered manifest.
/// The frontend cannot submit a path or arbitrary command line.
#[tauri::command]
fn start_module(
    state: State<'_, HostState>,
    window: WebviewWindow,
    module_id: String,
) -> Result<ModuleRuntimeSnapshot, CommandError> {
    ensure_main_window(&window)?;
    if !valid_module_id(&module_id) {
        return Err(CommandError {
            code: "moduleIdInvalid",
            message: "模块 id 无效。".to_string(),
        });
    }
    let record = state
        .module_index
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块索引状态不可用。".to_string(),
        })?
        .get(&module_id)
        .cloned()
        .ok_or_else(|| CommandError {
            code: "moduleNotFound",
            message: "模块尚未发现或清单无效，请先刷新模块。".to_string(),
        })?;

    let mut runtime = state.runtime.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "模块运行状态不可用。".to_string(),
    })?;
    runtime
        .start(&module_id, &record)
        .map_err(CommandError::from)
}

/// Start a validated module and open its backend-owned Web surface. The
/// frontend supplies only the module id; it cannot choose a URL or path.
#[tauri::command]
async fn open_module(
    app: tauri::AppHandle,
    state: State<'_, HostState>,
    window: WebviewWindow,
    module_id: String,
) -> Result<(), CommandError> {
    ensure_main_window(&window)?;
    if !valid_module_id(&module_id) {
        return Err(CommandError {
            code: "moduleIdInvalid",
            message: "模块 id 无效。".to_string(),
        });
    }
    let record = state
        .module_index
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块索引状态不可用。".to_string(),
        })?
        .get(&module_id)
        .cloned()
        .ok_or_else(|| CommandError {
            code: "moduleNotFound",
            message: "模块尚未发现或清单无效，请先刷新模块。".to_string(),
        })?;
    if record.web_entry.is_none() {
        return Err(CommandError {
            code: "moduleUiUnavailable",
            message: "该模块没有 Web 界面。".to_string(),
        });
    }
    {
        let mut runtime = state.runtime.lock().map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块运行状态不可用。".to_string(),
        })?;
        runtime
            .start(&module_id, &record)
            .map_err(CommandError::from)?;
    }
    if let Err(message) = open_module_window(&app, &state, &module_id) {
        if let Ok(mut runtime) = state.runtime.lock() {
            let _ = runtime.stop(&module_id);
        }
        return Err(CommandError {
            code: "moduleWindowUnavailable",
            message,
        });
    }
    Ok(())
}

#[tauri::command]
fn stop_module(
    state: State<'_, HostState>,
    window: WebviewWindow,
    module_id: String,
) -> Result<ModuleRuntimeSnapshot, CommandError> {
    ensure_main_window(&window)?;
    if !valid_module_id(&module_id) {
        return Err(CommandError {
            code: "moduleIdInvalid",
            message: "模块 id 无效。".to_string(),
        });
    }
    let mut runtime = state.runtime.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "模块运行状态不可用。".to_string(),
    })?;
    Ok(runtime.stop(&module_id))
}

/// Forward a module-specific operation only when the module declared it in
/// `module.json`. The payload is opaque to the host, but the process and
/// operation identity remain backend-owned and the response is correlated by
/// a host-generated request id inside the runtime manager.
#[tauri::command]
fn invoke_module(
    state: State<'_, HostState>,
    window: WebviewWindow,
    module_id: String,
    method: String,
    payload: Value,
) -> Result<Value, CommandError> {
    ensure_main_window(&window)?;
    if !valid_module_id(&module_id) {
        return Err(CommandError {
            code: "moduleIdInvalid",
            message: "模块 id 无效。".to_string(),
        });
    }
    if !valid_operation_name(&method) {
        return Err(CommandError {
            code: "operationInvalid",
            message: "模块操作名无效。".to_string(),
        });
    }
    let record = state
        .module_index
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块索引状态不可用。".to_string(),
        })?
        .get(&module_id)
        .cloned()
        .ok_or_else(|| CommandError {
            code: "moduleNotFound",
            message: "模块尚未发现或清单无效，请先刷新模块。".to_string(),
        })?;
    if !record.operations.contains(&method) {
        return Err(CommandError {
            code: "operationNotDeclared",
            message: "该模块未声明此操作。".to_string(),
        });
    }
    let mut runtime = state.runtime.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "模块运行状态不可用。".to_string(),
    })?;
    runtime
        .invoke(&module_id, &record, &method, payload)
        .map_err(CommandError::from)
}

/// Invoke an operation from a module-owned Web window. The module id is
/// derived from the window label (`module-<id>`) instead of being accepted
/// from the Web UI. This prevents a compromised module page from reaching a
/// different module's process while keeping the payload JSON-only.
#[tauri::command]
fn invoke_module_window(
    state: State<'_, HostState>,
    window: WebviewWindow,
    method: String,
    payload: Value,
) -> Result<Value, CommandError> {
    let module_id = module_id_for_window(&window).ok_or_else(|| CommandError {
        code: "moduleWindowUnauthorized",
        message: "只有模块窗口可以调用模块操作。".to_string(),
    })?;
    if !valid_operation_name(&method) {
        return Err(CommandError {
            code: "operationInvalid",
            message: "模块操作名无效。".to_string(),
        });
    }
    let record = state
        .module_index
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块索引状态不可用。".to_string(),
        })?
        .get(&module_id)
        .cloned()
        .ok_or_else(|| CommandError {
            code: "moduleNotFound",
            message: "模块尚未发现或清单无效，请先刷新模块。".to_string(),
        })?;
    if !record.operations.contains(&method) {
        return Err(CommandError {
            code: "operationNotDeclared",
            message: "该模块未声明此操作。".to_string(),
        });
    }
    let mut runtime = state.runtime.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "模块运行状态不可用。".to_string(),
    })?;
    runtime
        .invoke(&module_id, &record, &method, payload)
        .map_err(CommandError::from)
}

/// Return the narrow context a module Web surface needs to initialize its
/// bridge. It intentionally contains no module directory, executable path or
/// user-data path; those remain Rust-only values.
#[tauri::command]
fn get_module_window_context(
    state: State<'_, HostState>,
    window: WebviewWindow,
) -> Result<ModuleWindowContext, CommandError> {
    let module_id = module_id_for_window(&window).ok_or_else(|| CommandError {
        code: "moduleWindowUnauthorized",
        message: "只有模块窗口可以读取模块上下文。".to_string(),
    })?;
    let (name, version, icon_data_url, operations) = state
        .module_index
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块索引状态不可用。".to_string(),
        })?
        .get(&module_id)
        .map(|record| {
            (
                record.name.clone(),
                record.version.clone(),
                record.icon_data_url.clone(),
                record.operations.iter().cloned().collect(),
            )
        })
        .ok_or_else(|| CommandError {
            code: "moduleNotFound",
            message: "模块尚未发现或清单无效，请先刷新模块。".to_string(),
        })?;
    Ok(ModuleWindowContext {
        module_id,
        name,
        version,
        icon_data_url,
        protocol_version: protocol::PROTOCOL_VERSION,
        operations,
    })
}

/// Hide only the module window that issued the request. This is intentionally
/// narrower than `hide_to_tray`, so a module cannot hide or manipulate the
/// main shell through its page bridge.
#[tauri::command]
fn hide_module_window(window: WebviewWindow) -> Result<(), CommandError> {
    if module_id_for_window(&window).is_none() {
        return Err(CommandError {
            code: "moduleWindowUnauthorized",
            message: "只有模块窗口可以隐藏自身窗口。".to_string(),
        });
    }
    window.hide().map_err(|error| CommandError {
        code: "windowUnavailable",
        message: format!("无法隐藏模块窗口：{error}"),
    })
}

fn module_id_for_window(window: &WebviewWindow) -> Option<String> {
    module_id_from_window_label(window.label())
}

/// Tauri window labels intentionally have a smaller alphabet than manifest
/// ids (for example, `qing.launcher` contains a dot). Encode the id before it
/// becomes a label instead of replacing characters and introducing collisions.
/// Hex keeps the label deterministic, ASCII-only, and reversible without
/// consulting mutable process state from the window command boundary.
fn module_window_label(module_id: &str) -> String {
    let encoded: String = module_id
        .as_bytes()
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect();
    format!("module-{encoded}")
}

fn module_id_from_window_label(label: &str) -> Option<String> {
    let encoded = label.strip_prefix("module-")?;
    if encoded.is_empty()
        || encoded.len() % 2 != 0
        || !encoded.bytes().all(|byte| byte.is_ascii_hexdigit())
    {
        return None;
    }
    let bytes = (0..encoded.len())
        .step_by(2)
        .map(|offset| u8::from_str_radix(&encoded[offset..offset + 2], 16).ok())
        .collect::<Option<Vec<_>>>()?;
    let id = String::from_utf8(bytes).ok()?;
    valid_module_id(&id).then_some(id)
}

#[tauri::command]
fn get_module_runtime(
    state: State<'_, HostState>,
    window: WebviewWindow,
    module_id: String,
) -> Result<ModuleRuntimeSnapshot, CommandError> {
    ensure_main_window(&window)?;
    if !valid_module_id(&module_id) {
        return Err(CommandError {
            code: "moduleIdInvalid",
            message: "模块 id 无效。".to_string(),
        });
    }
    let mut runtime = state.runtime.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "模块运行状态不可用。".to_string(),
    })?;
    Ok(runtime.snapshot(&module_id))
}

#[tauri::command]
fn get_all_module_runtime(
    state: State<'_, HostState>,
    window: WebviewWindow,
) -> Result<Vec<ModuleRuntimeSnapshot>, CommandError> {
    ensure_main_window(&window)?;
    let mut runtime = state.runtime.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "模块运行状态不可用。".to_string(),
    })?;
    Ok(runtime.snapshots())
}

pub fn run() {
    let mut builder = tauri::Builder::default();
    builder = builder.plugin(tauri_plugin_dialog::init());
    #[cfg(desktop)]
    {
        builder = builder.plugin(tauri_plugin_global_shortcut::Builder::new().build());
    }
    // Desktop smoke tests may run alongside the user's installed QingToolbox.
    // Keep the production single-instance behavior by default, while allowing
    // an explicitly opted-in debug test process to use its own host instance.
    let disable_single_instance =
        cfg!(debug_assertions) && std::env::var_os("QING_TAURI_DISABLE_SINGLE_INSTANCE").is_some();
    if !disable_single_instance {
        builder = builder.plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            show_main_window(app);
        }));
    }
    builder
        .register_uri_scheme_protocol("qmod", |context, request| {
            serve_module_asset(context.app_handle(), request)
        })
        .manage(HostState::new())
        .invoke_handler(tauri::generate_handler![
            get_host_info,
            get_settings,
            update_settings,
            import_module,
            list_modules,
            hide_to_tray,
            start_module,
            open_module,
            stop_module,
            invoke_module,
            invoke_module_window,
            get_module_window_context,
            hide_module_window,
            get_module_runtime,
            get_all_module_runtime
        ])
        .setup(|app| {
            start_runtime_supervisor(app.handle().clone());

            register_toggle_hotkey(app);

            let menu = MenuBuilder::new(app)
                .text("open", "打开工具箱")
                .separator()
                .text("quit", "退出 QingToolbox")
                .build()?;
            let icon = app
                .default_window_icon()
                .cloned()
                .ok_or_else(|| "default window icon is unavailable".to_string())?;
            TrayIconBuilder::with_id("main")
                .icon(icon)
                .tooltip("QingToolbox")
                .menu(&menu)
                .show_menu_on_left_click(true)
                .on_menu_event(|app, event| match event.id().as_ref() {
                    "open" => show_main_window(app),
                    "quit" => app.exit(0),
                    _ => {}
                })
                .build(app)?;

            if let Some(window) = app.get_webview_window("main") {
                install_close_behavior(&window);
                apply_startup_presentation(app.handle(), &window);
            }
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while building QingToolbox Tauri application")
        .run(|app, event| {
            if matches!(
                event,
                tauri::RunEvent::Exit | tauri::RunEvent::ExitRequested { .. }
            ) {
                stop_all_modules(app);
            }
        });
}

#[cfg(desktop)]
fn register_toggle_hotkey<R: tauri::Runtime>(app: &mut tauri::App<R>) {
    use std::str::FromStr;
    use tauri_plugin_global_shortcut::{GlobalShortcutExt, Shortcut, ShortcutState};

    let value = app
        .try_state::<HostState>()
        .and_then(|state| {
            state
                .settings
                .lock()
                .ok()
                .map(|settings| settings.snapshot().toggle_hotkey)
        })
        .unwrap_or_else(|| "Ctrl+Alt+Space".to_string());
    let shortcut = match Shortcut::from_str(&value) {
        Ok(shortcut) => shortcut,
        Err(error) => {
            eprintln!("QingToolbox global hotkey is invalid ({value}): {error}");
            return;
        }
    };
    let result = app
        .handle()
        .global_shortcut()
        .on_shortcut(shortcut, |_app, _shortcut, event| {
            if event.state() == ShortcutState::Pressed {
                toggle_main_window(_app);
            }
        });
    if let Err(error) = result {
        eprintln!("QingToolbox global hotkey could not be registered ({value}): {error}");
    }
}

#[cfg(not(desktop))]
fn register_toggle_hotkey<R: tauri::Runtime>(_app: &mut tauri::App<R>) {}

/// Enforce process exits and hello deadlines in Rust. Runtime correctness must
/// not depend on the Vue page polling status or remaining responsive.
fn start_runtime_supervisor<R: tauri::Runtime>(app: tauri::AppHandle<R>) {
    thread::spawn(move || loop {
        thread::sleep(Duration::from_millis(250));
        let Some(state) = app.try_state::<HostState>() else {
            break;
        };
        let Ok(mut runtime) = state.runtime.lock() else {
            break;
        };
        let _ = runtime.snapshots();
    });
}

fn show_main_window<R: tauri::Runtime>(app: &tauri::AppHandle<R>) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

fn toggle_main_window<R: tauri::Runtime>(app: &tauri::AppHandle<R>) {
    if let Some(window) = app.get_webview_window("main") {
        let visible = window.is_visible().unwrap_or(false);
        if visible {
            let _ = window.hide();
        } else {
            show_main_window(app);
        }
    }
}

fn stop_all_modules<R: tauri::Runtime>(app: &tauri::AppHandle<R>) {
    if let Some(state) = app.try_state::<HostState>() {
        if let Ok(mut runtime) = state.runtime.lock() {
            runtime.stop_all();
        }
    }
}

fn apply_startup_presentation<R: tauri::Runtime>(
    app: &tauri::AppHandle<R>,
    window: &WebviewWindow<R>,
) {
    let configured = app
        .try_state::<HostState>()
        .and_then(|state| {
            state
                .settings
                .lock()
                .ok()
                .map(|settings| settings.snapshot().startup_presentation)
        })
        .unwrap_or_else(|| "main".to_string());
    // Smoke and local development can request a deterministic visible window
    // without changing the user's persisted preference. Production has no
    // override unless the environment is explicitly set.
    let presentation = std::env::var("QING_TAURI_STARTUP_PRESENTATION")
        .ok()
        .filter(|value| matches!(value.as_str(), "main" | "minimized" | "tray"))
        .unwrap_or(configured);
    match presentation.as_str() {
        "minimized" => {
            let _ = window.show();
            let _ = window.minimize();
        }
        "tray" => {
            let _ = window.hide();
        }
        _ => show_main_window(app),
    }
}

fn install_close_behavior(window: &WebviewWindow) {
    let window_for_handler = window.clone();
    let app = window.app_handle().clone();
    window.on_window_event(move |event| {
        let WindowEvent::CloseRequested { api, .. } = event else {
            return;
        };
        let behavior = app
            .try_state::<HostState>()
            .and_then(|state| {
                state
                    .settings
                    .lock()
                    .ok()
                    .map(|settings| settings.snapshot().close_behavior)
            })
            .unwrap_or_else(|| "tray".to_string());
        match behavior.as_str() {
            "exit" => {
                // Prevent the event first and let Tauri's exit path perform
                // the normal module supervisor cleanup exactly once.
                api.prevent_close();
                app.exit(0);
            }
            "ask" => {
                api.prevent_close();
                let Some(state) = app.try_state::<HostState>() else {
                    let _ = window_for_handler.hide();
                    return;
                };
                if state.close_prompt_active.swap(true, Ordering::AcqRel) {
                    return;
                }
                let callback_app = app.clone();
                let callback_window = window_for_handler.clone();
                app.dialog()
                    .message("关闭窗口后要如何处理 QingToolbox？")
                    .title("QingToolbox")
                    .kind(MessageDialogKind::Info)
                    .buttons(MessageDialogButtons::OkCancelCustom(
                        "退出工具箱".to_string(),
                        "最小化到托盘".to_string(),
                    ))
                    .parent(&window_for_handler)
                    .show(move |should_exit| {
                        if let Some(state) = callback_app.try_state::<HostState>() {
                            state.close_prompt_active.store(false, Ordering::Release);
                        }
                        if should_exit {
                            callback_app.exit(0);
                        } else {
                            let _ = callback_window.hide();
                        }
                    });
            }
            _ => {
                api.prevent_close();
                let _ = window_for_handler.hide();
            }
        }
    });
}

#[cfg(test)]
mod tests {
    use super::{module_id_from_window_label, module_window_label};

    #[test]
    fn module_window_label_is_the_only_authorized_shape() {
        let label = module_window_label("qing.launcher");
        assert_eq!(label, "module-71696e672e6c61756e63686572");
        assert_eq!(
            module_id_from_window_label(&label).as_deref(),
            Some("qing.launcher")
        );
        assert!(module_id_from_window_label("main").is_none());
        assert!(module_id_from_window_label("module-2e2e2f2f657363617065").is_none());
        assert!(module_id_from_window_label("module-").is_none());
    }
}
