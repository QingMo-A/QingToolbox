use std::{sync::Mutex, thread, time::Duration};

use serde::Serialize;
use tauri::{
    menu::MenuBuilder, tray::TrayIconBuilder, webview::WebviewWindow, Manager, State, WindowEvent,
};

mod modules;
mod paths;
pub mod protocol;
mod runtime;
mod web;

use modules::{discover_modules, ModuleListPayload};
use paths::{resolve_module_roots, ModuleRoot};
use protocol::ProtocolEnvelope;
use runtime::{ModuleRuntimeManager, ModuleRuntimeSnapshot, RuntimeError};
use web::{open_module_window, serve_module_asset};

/// Process-wide state owned by the Rust host. Paths and module records stay on
/// this side of the IPC boundary; the Vue layer only receives stable ids and
/// display metadata.
pub struct HostState {
    roots: Vec<ModuleRoot>,
    module_index: Mutex<std::collections::BTreeMap<String, modules::ModuleRecord>>,
    scan_gate: Mutex<()>,
    runtime: Mutex<ModuleRuntimeManager>,
}

impl HostState {
    pub fn new() -> Self {
        Self {
            roots: resolve_module_roots(),
            module_index: Mutex::new(std::collections::BTreeMap::new()),
            scan_gate: Mutex::new(()),
            runtime: Mutex::new(ModuleRuntimeManager::new()),
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
fn hide_to_tray(window: tauri::WebviewWindow) -> Result<(), CommandError> {
    window.hide().map_err(|error| CommandError {
        code: "windowUnavailable",
        message: format!("无法隐藏工具箱窗口：{error}"),
    })
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct CommandError {
    code: &'static str,
    message: String,
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
) -> Result<ProtocolEnvelope<ModuleListPayload>, CommandError> {
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

fn valid_module_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_'))
}

/// Start only the executable recorded in a previously discovered manifest.
/// The frontend cannot submit a path or arbitrary command line.
#[tauri::command]
fn start_module(
    state: State<'_, HostState>,
    module_id: String,
) -> Result<ModuleRuntimeSnapshot, CommandError> {
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
fn open_module(
    app: tauri::AppHandle,
    state: State<'_, HostState>,
    module_id: String,
) -> Result<(), CommandError> {
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
    module_id: String,
) -> Result<ModuleRuntimeSnapshot, CommandError> {
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

#[tauri::command]
fn get_module_runtime(
    state: State<'_, HostState>,
    module_id: String,
) -> Result<ModuleRuntimeSnapshot, CommandError> {
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
) -> Result<Vec<ModuleRuntimeSnapshot>, CommandError> {
    let mut runtime = state.runtime.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "模块运行状态不可用。".to_string(),
    })?;
    Ok(runtime.snapshots())
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            show_main_window(app);
        }))
        .register_uri_scheme_protocol("qmod", |context, request| {
            serve_module_asset(context.app_handle(), request)
        })
        .manage(HostState::new())
        .invoke_handler(tauri::generate_handler![
            get_host_info,
            list_modules,
            hide_to_tray,
            start_module,
            open_module,
            stop_module,
            get_module_runtime,
            get_all_module_runtime
        ])
        .setup(|app| {
            start_runtime_supervisor(app.handle().clone());

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
                install_close_to_tray_behavior(&window);
            }
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running QingToolbox Tauri application");
}

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

fn install_close_to_tray_behavior(window: &WebviewWindow) {
    let window_for_handler = window.clone();
    window.on_window_event(move |event| {
        if let WindowEvent::CloseRequested { api, .. } = event {
            api.prevent_close();
            let _ = window_for_handler.hide();
        }
    });
}
