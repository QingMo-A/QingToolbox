use std::{
    collections::BTreeSet,
    fs,
    path::PathBuf,
    process::Command,
    sync::{
        atomic::{AtomicBool, Ordering},
        Mutex,
    },
    thread,
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use serde::Serialize;
use serde_json::Value;
use tauri::{
    menu::MenuBuilder, tray::TrayIconBuilder, webview::WebviewWindow, DragDropEvent, Emitter,
    EventTarget, Manager, State, WindowEvent,
};
use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};

mod importer;
mod modules;
mod paths;
pub mod protocol;
mod runtime;
mod settings;
mod web;

use importer::{import_qmod, update_qmod, ModuleImportResult};
use modules::{discover_modules, ModuleListPayload};
use paths::{resolve_module_roots, user_modules_root, ModuleRoot, ModuleSource};
use protocol::ProtocolEnvelope;
use runtime::{ModuleRuntimeManager, ModuleRuntimeSnapshot, RuntimeError};
use settings::{SettingsSnapshot, SettingsStore, SettingsUpdate};
use web::{open_module_window, serve_module_asset, serve_screenpin_asset, ScreenPinWindowRecord};

const MAX_EXTERNAL_DROP_PATHS: usize = 32;
const MAX_EXTERNAL_DROP_PATH_LENGTH: usize = 32 * 1024;
const MAX_SESSION_LOG_ENTRIES: usize = 256;
const MODULE_STATE_CHANGED_EVENT: &str = "qmod:module-state-changed";
const DEFAULT_LAUNCHER_HOTKEY: &str = "Ctrl+Alt+L";

/// Process-wide state owned by the Rust host. Paths and module records stay on
/// this side of the IPC boundary; the Vue layer only receives stable ids and
/// display metadata.
pub struct HostState {
    roots: Vec<ModuleRoot>,
    module_index: Mutex<std::collections::BTreeMap<String, modules::ModuleRecord>>,
    scan_gate: Mutex<()>,
    runtime: Mutex<ModuleRuntimeManager>,
    settings: Mutex<SettingsStore>,
    module_hotkeys: Mutex<std::collections::BTreeMap<String, String>>,
    screenpin_windows: Mutex<std::collections::BTreeMap<String, ScreenPinWindowRecord>>,
    session_logs: Mutex<Vec<SessionLogEntry>>,
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
            module_hotkeys: Mutex::new(std::collections::BTreeMap::new()),
            screenpin_windows: Mutex::new(std::collections::BTreeMap::new()),
            session_logs: Mutex::new(Vec::new()),
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

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct SessionLogEntry {
    timestamp: String,
    level: String,
    category: String,
    message: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct SessionLogSnapshot {
    generated_at: String,
    entries: Vec<SessionLogEntry>,
}

fn record_log(state: &HostState, level: &str, category: &str, message: impl Into<String>) {
    let Ok(mut entries) = state.session_logs.lock() else {
        return;
    };
    entries.push(SessionLogEntry {
        timestamp: now_rfc3339(),
        level: level.to_string(),
        category: category.to_string(),
        message: message.into(),
    });
    let excess = entries.len().saturating_sub(MAX_SESSION_LOG_ENTRIES);
    if excess > 0 {
        entries.drain(..excess);
    }
}

/// Keep the log contract independent from a third-party time crate. This is
/// UTC with millisecond precision, which is directly consumable by Date.parse
/// in the existing Vue log store.
fn now_rfc3339() -> String {
    let elapsed = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    let total_seconds = elapsed.as_secs() as i64;
    let days = total_seconds.div_euclid(86_400);
    let day_seconds = total_seconds.rem_euclid(86_400);
    let (year, month, day) = civil_date_from_days(days);
    let hour = day_seconds / 3_600;
    let minute = day_seconds.rem_euclid(3_600) / 60;
    let second = day_seconds.rem_euclid(60);
    let millis = elapsed.subsec_millis();
    format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{millis:03}Z")
}

fn civil_date_from_days(days_since_unix_epoch: i64) -> (i64, i64, i64) {
    // Howard Hinnant's proleptic Gregorian conversion, valid for the range
    // relevant to system timestamps and avoiding locale/time-zone APIs.
    let z = days_since_unix_epoch + 719_468;
    let era = (if z >= 0 { z } else { z - 146_096 }).div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096).div_euclid(365);
    let mut year = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let month_part = (5 * doy + 2).div_euclid(153);
    let day = doy - (153 * month_part + 2).div_euclid(5) + 1;
    let month = month_part + if month_part < 10 { 3 } else { -9 };
    year += i64::from(month <= 2);
    (year, month, day)
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

/// Return the bounded in-memory event history for the current host session.
/// Paths and process handles are never exposed through this DTO.
#[tauri::command]
fn get_session_logs(
    state: State<'_, HostState>,
    window: WebviewWindow,
) -> Result<SessionLogSnapshot, CommandError> {
    ensure_main_window(&window)?;
    let entries = state.session_logs.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "会话日志状态不可用。".to_string(),
    })?;
    Ok(SessionLogSnapshot {
        generated_at: now_rfc3339(),
        entries: entries.clone(),
    })
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
    let previous = settings.snapshot();
    let requested_launch_at_login = update.launch_at_login.unwrap_or(previous.launch_at_login);
    let launch_at_login_changed = requested_launch_at_login != previous.launch_at_login;

    // Register the OS startup entry before committing the preference. If the
    // registration fails, the JSON document remains truthful and the user can
    // keep using the rest of the host. Debug builds intentionally skip this
    // side effect unless explicitly enabled for an integration test.
    if launch_at_login_changed && autostart_sync_enabled() {
        sync_launch_at_login(window.app_handle(), requested_launch_at_login).map_err(
            |message| CommandError {
                code: "autostartUnavailable",
                message,
            },
        )?;
    }

    match settings.update(update) {
        Ok(snapshot) => {
            record_log(&state, "Information", "Settings", "Settings updated.");
            Ok(snapshot)
        }
        Err(error) => {
            // The settings write can still fail after an OS registration. Try
            // to restore the previous registration so a retry is safe.
            if launch_at_login_changed && autostart_sync_enabled() {
                let _ = sync_launch_at_login(window.app_handle(), previous.launch_at_login);
            }
            Err(CommandError {
                code: error.code,
                message: error.message,
            })
        }
    }
}

/// Toggle whether a discovered module may be started with the host. The
/// setting is persisted by Rust and the frontend can only submit a validated
/// module id plus a boolean preference.
#[tauri::command]
fn set_module_startup_authorization(
    state: State<'_, HostState>,
    window: WebviewWindow,
    module_id: String,
    enabled: bool,
) -> Result<SettingsSnapshot, CommandError> {
    ensure_main_window(&window)?;
    if !valid_module_id(&module_id) {
        return Err(CommandError {
            code: "moduleIdInvalid",
            message: "模块 id 无效。".to_string(),
        });
    }
    let known = state
        .module_index
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块索引状态不可用。".to_string(),
        })?
        .get(&module_id)
        .is_some_and(|record| {
            record.source == ModuleSource::User || record.source == ModuleSource::Bundled
        });
    if !known {
        return Err(CommandError {
            code: "moduleNotFound",
            message: "模块尚未发现或清单无效，请先刷新模块。".to_string(),
        });
    }
    let mut settings = state.settings.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "工具箱设置状态不可用。".to_string(),
    })?;
    let mut ids = settings.snapshot().startup_module_ids;
    ids.retain(|value| value != &module_id);
    if enabled {
        ids.push(module_id.clone());
    }
    let snapshot = settings
        .update(SettingsUpdate {
            startup_module_ids: Some(ids),
            ..SettingsUpdate::default()
        })
        .map_err(|error| CommandError {
            code: error.code,
            message: error.message,
        })?;
    drop(settings);
    record_log(
        &state,
        "Information",
        "Modules",
        format!(
            "Startup authorization {} for {module_id}.",
            if enabled { "enabled" } else { "disabled" }
        ),
    );
    Ok(snapshot)
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
    record_log(
        &state,
        "Information",
        "Modules",
        format!("Imported module {} v{}.", result.name, result.version),
    );
    Ok(result)
}

/// Open a discovered module directory using Explorer. The frontend supplies
/// only the validated module id; the directory itself remains backend-owned.
#[tauri::command]
fn open_module_directory(
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
    let directory = state
        .module_index
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块索引状态不可用。".to_string(),
        })?
        .get(&module_id)
        .map(|record| record.directory.clone())
        .ok_or_else(|| CommandError {
            code: "moduleNotFound",
            message: "模块尚未发现或清单无效，请先刷新模块。".to_string(),
        })?;
    if !directory.is_dir() {
        return Err(CommandError {
            code: "moduleDirectoryMissing",
            message: "模块目录不存在。".to_string(),
        });
    }
    Command::new("explorer.exe")
        .arg(&directory)
        .spawn()
        .map(|_| {
            record_log(
                &state,
                "Information",
                "Modules",
                format!("Opened module directory for {module_id}."),
            );
        })
        .map_err(|error| CommandError {
            code: "openDirectoryFailed",
            message: format!("无法打开模块目录：{error}"),
        })
}

/// Remove a user-installed module after stopping its process and closing its
/// window. Bundled modules are immutable and can never be removed here.
#[tauri::command]
fn remove_module(
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
    if record.source != ModuleSource::User {
        return Err(CommandError {
            code: "moduleRemoveUnsupported",
            message: "内置模块不能被删除。".to_string(),
        });
    }
    let user_root = user_modules_root().ok_or_else(|| CommandError {
        code: "moduleRootUnavailable",
        message: "用户模块目录不可用。".to_string(),
    })?;
    let canonical_root = fs::canonicalize(&user_root).map_err(|error| CommandError {
        code: "moduleRootUnavailable",
        message: format!("无法验证用户模块目录：{error}"),
    })?;
    let canonical_directory =
        fs::canonicalize(&record.directory).map_err(|error| CommandError {
            code: "moduleDirectoryMissing",
            message: format!("无法验证模块目录：{error}"),
        })?;
    if !canonical_directory.starts_with(&canonical_root) || canonical_directory == canonical_root {
        return Err(CommandError {
            code: "moduleBoundaryViolation",
            message: "模块目录不在用户模块根目录内。".to_string(),
        });
    }

    let label = module_window_label(&module_id);
    if let Some(module_window) = app.get_webview_window(&label) {
        let _ = module_window.close();
    }
    if let Ok(mut runtime) = state.runtime.lock() {
        let _ = runtime.stop(&module_id);
    }
    let _ = clear_module_hotkey_binding(&app, &state, &module_id);
    fs::remove_dir_all(&canonical_directory).map_err(|error| CommandError {
        code: "moduleRemoveFailed",
        message: format!("无法删除模块目录：{error}"),
    })?;
    let discovery = discover_modules(&state.roots);
    if let Ok(mut index) = state.module_index.lock() {
        *index = discovery.records;
    }
    record_log(
        &state,
        "Information",
        "Modules",
        format!("Removed module {module_id}."),
    );
    Ok(())
}

/// Replace one installed user module from an explicitly selected `.qmod`.
/// The selected path is used only by the native file-picker flow; the module
/// id, destination root and executable remain backend-owned. A running module
/// is stopped and its Web window is closed before the atomic package swap.
#[tauri::command]
fn update_module(
    app: tauri::AppHandle,
    state: State<'_, HostState>,
    window: WebviewWindow,
    module_id: String,
    source_path: String,
) -> Result<ModuleImportResult, CommandError> {
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
    if record.source != ModuleSource::User {
        return Err(CommandError {
            code: "moduleUpdateUnsupported",
            message: "内置模块不能从模块窗口覆盖更新。".to_string(),
        });
    }

    let was_running = state
        .runtime
        .lock()
        .ok()
        .map(|mut runtime| {
            matches!(
                runtime.snapshot(&module_id).state,
                runtime::ModuleRuntimeState::Starting | runtime::ModuleRuntimeState::Running
            )
        })
        .unwrap_or(false);

    // Closing a Web window first releases WebView2 file handles and prevents
    // an old qmod asset tree from remaining visible while it is replaced.
    let label = module_window_label(&module_id);
    if let Some(module_window) = app.get_webview_window(&label) {
        let _ = module_window.close();
    }
    if let Ok(mut runtime) = state.runtime.lock() {
        let _ = runtime.stop(&module_id);
    }
    let _ = clear_module_hotkey_binding(&app, &state, &module_id);

    let result = match update_qmod(&source_path, &module_id) {
        Ok(result) => result,
        Err(error) => {
            // An invalid package should not leave a previously running user
            // module stopped. The old record is still valid whenever the
            // atomic replacement has not committed; a best-effort restart is
            // harmless after a committed replacement as well because the
            // directory identity remains the same.
            if was_running {
                if let Ok(mut runtime) = state.runtime.lock() {
                    let _ = runtime.start(&module_id, &record);
                }
            }
            return Err(CommandError {
                code: error.code,
                message: error.message,
            });
        }
    };
    let discovery = discover_modules(&state.roots);
    if let Ok(mut index) = state.module_index.lock() {
        *index = discovery.records;
    }
    record_log(
        &state,
        "Information",
        "Modules",
        format!("Updated module {} to v{}.", result.id, result.version),
    );
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
    let snapshot = runtime
        .start(&module_id, &record)
        .map_err(CommandError::from)?;
    drop(runtime);
    record_log(
        &state,
        "Information",
        "Runtime",
        format!("Started module {module_id}."),
    );
    Ok(snapshot)
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
    if module_id == "qing.launcher" {
        let requested = launcher_hotkey_from_state(&state, &module_id, &record)
            .unwrap_or_else(|| DEFAULT_LAUNCHER_HOTKEY.to_string());
        if let Err(error) = register_module_hotkey_binding(&app, &state, &module_id, &requested) {
            // A shortcut conflict must not prevent the module itself from
            // opening; the Launcher UI remains usable and can request a
            // different binding through set_module_hotkey.
            if requested != DEFAULT_LAUNCHER_HOTKEY {
                if let Err(fallback) = register_module_hotkey_binding(
                    &app,
                    &state,
                    &module_id,
                    DEFAULT_LAUNCHER_HOTKEY,
                ) {
                    eprintln!(
                        "Qing Launcher module hotkey unavailable: {}; fallback failed: {}",
                        error.message, fallback.message
                    );
                }
            } else {
                eprintln!("Qing Launcher module hotkey unavailable: {}", error.message);
            }
        }
    }
    record_log(
        &state,
        "Information",
        "Runtime",
        format!("Opened module {module_id}."),
    );
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
    let snapshot = runtime.stop(&module_id);
    drop(runtime);
    let _ = clear_module_hotkey_binding(window.app_handle(), &state, &module_id);
    if module_id == "qing.screenpin" {
        close_screenpin_windows(window.app_handle(), &state);
    }
    record_log(
        &state,
        "Information",
        "Runtime",
        format!("Stopped module {module_id}."),
    );
    Ok(snapshot)
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
    let removed_pin = (module_id == "qing.screenpin" && method == "removePin")
        .then(|| {
            payload
                .get("pinId")
                .and_then(Value::as_str)
                .map(str::to_string)
        })
        .flatten();
    let clear_pins = module_id == "qing.screenpin" && method == "clearPins";
    let mut runtime = state.runtime.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "模块运行状态不可用。".to_string(),
    })?;
    let result = runtime
        .invoke(&module_id, &record, &method, payload)
        .map_err(CommandError::from);
    drop(runtime);
    if result.is_ok() {
        if clear_pins {
            close_screenpin_windows(window.app_handle(), &state);
        } else if let Some(pin_id) = removed_pin {
            close_screenpin_pin_window(window.app_handle(), &state, &pin_id);
        }
    }
    result
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
    let removed_pin = (module_id == "qing.screenpin" && method == "removePin")
        .then(|| {
            payload
                .get("pinId")
                .and_then(Value::as_str)
                .map(str::to_string)
        })
        .flatten();
    let clear_pins = module_id == "qing.screenpin" && method == "clearPins";
    let mut runtime = state.runtime.lock().map_err(|_| CommandError {
        code: "stateUnavailable",
        message: "模块运行状态不可用。".to_string(),
    })?;
    let result = runtime
        .invoke(&module_id, &record, &method, payload)
        .map_err(CommandError::from);
    drop(runtime);
    if result.is_ok() {
        if clear_pins {
            close_screenpin_windows(window.app_handle(), &state);
        } else if let Some(pin_id) = removed_pin {
            close_screenpin_pin_window(window.app_handle(), &state, &pin_id);
        }
    }
    result
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

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ModuleHotkeySnapshot {
    module_id: String,
    hotkey: Option<String>,
    status: &'static str,
}

fn launcher_hotkey_from_state(
    state: &HostState,
    module_id: &str,
    record: &modules::ModuleRecord,
) -> Option<String> {
    if !record.operations.contains("getState") {
        return None;
    }
    let value = state
        .runtime
        .lock()
        .ok()?
        .invoke(
            module_id,
            record,
            "getState",
            Value::Object(Default::default()),
        )
        .ok()?;
    let hotkey = value.get("hotkey")?.as_object()?;
    let key = hotkey.get("keyLabel")?.as_str()?.trim();
    if key.is_empty() || key.chars().count() > 32 {
        return None;
    }
    let mut parts = Vec::new();
    for (field, label) in [
        ("ctrl", "Ctrl"),
        ("alt", "Alt"),
        ("shift", "Shift"),
        ("win", "Super"),
    ] {
        if hotkey.get(field).and_then(Value::as_bool).unwrap_or(false) {
            parts.push(label);
        }
    }
    if parts.is_empty() || !key.bytes().all(|byte| !byte.is_ascii_control()) {
        return None;
    }
    parts.push(key);
    Some(parts.join("+"))
}

/// Register a module-owned shortcut through the host's native global-shortcut
/// backend. The module page may request only its own binding (derived from the
/// window label), and an empty value explicitly releases it. No arbitrary
/// callback or window target crosses the WebView boundary.
#[tauri::command]
fn set_module_hotkey(
    app: tauri::AppHandle,
    state: State<'_, HostState>,
    window: WebviewWindow,
    hotkey: String,
) -> Result<ModuleHotkeySnapshot, CommandError> {
    let module_id = module_id_for_window(&window).ok_or_else(|| CommandError {
        code: "moduleWindowUnauthorized",
        message: "只有模块窗口可以设置模块快捷键。".to_string(),
    })?;
    if hotkey.chars().count() > 64 {
        return Err(CommandError {
            code: "hotkeyInvalid",
            message: "快捷键长度超过限制。".to_string(),
        });
    }
    let declared = state
        .module_index
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块索引状态不可用。".to_string(),
        })?
        .get(&module_id)
        .is_some_and(|record| record.operations.contains("setHotkey"));
    if !declared {
        return Err(CommandError {
            code: "operationNotDeclared",
            message: "该模块未声明快捷键设置能力。".to_string(),
        });
    }

    let requested = hotkey.trim().to_string();
    if requested.is_empty() {
        clear_module_hotkey_binding(&app, &state, &module_id)?;
        return Ok(ModuleHotkeySnapshot {
            module_id,
            hotkey: None,
            status: "inactive",
        });
    }
    register_module_hotkey_binding(&app, &state, &module_id, &requested)?;
    Ok(ModuleHotkeySnapshot {
        module_id,
        hotkey: Some(requested),
        status: "registered",
    })
}

#[tauri::command]
fn clear_module_hotkey(
    app: tauri::AppHandle,
    state: State<'_, HostState>,
    window: WebviewWindow,
) -> Result<ModuleHotkeySnapshot, CommandError> {
    let module_id = module_id_for_window(&window).ok_or_else(|| CommandError {
        code: "moduleWindowUnauthorized",
        message: "只有模块窗口可以清理模块快捷键。".to_string(),
    })?;
    clear_module_hotkey_binding(&app, &state, &module_id)?;
    Ok(ModuleHotkeySnapshot {
        module_id,
        hotkey: None,
        status: "inactive",
    })
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ScreenPinWindowSnapshot {
    pin_id: String,
    window_label: String,
}

/// Open a Screen Pin as a real top-level, always-on-top Tauri window. The
/// caller supplies only an opaque pin id; the image bytes are fetched from
/// the trusted Screen Pin process and kept in a host-side token map.
#[tauri::command]
fn open_screenpin_pin(
    app: tauri::AppHandle,
    state: State<'_, HostState>,
    window: WebviewWindow,
    pin_id: String,
) -> Result<ScreenPinWindowSnapshot, CommandError> {
    let module_id = module_id_for_window(&window).ok_or_else(|| CommandError {
        code: "moduleWindowUnauthorized",
        message: "只有模块窗口可以打开浮窗。".to_string(),
    })?;
    if module_id != "qing.screenpin" || !valid_screenpin_pin_id(&pin_id) {
        return Err(CommandError {
            code: "pinIdInvalid",
            message: "截图 id 无效。".to_string(),
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
            message: "Screen Pin 模块尚未发现。".to_string(),
        })?;
    if !record.operations.contains("getPin") {
        return Err(CommandError {
            code: "operationNotDeclared",
            message: "Screen Pin 模块未声明浮窗读取能力。".to_string(),
        });
    }

    if let Ok(records) = state.screenpin_windows.lock() {
        if let Some((label, _)) = records.iter().find(|(_, value)| value.pin_id == pin_id) {
            if let Some(existing) = app.get_webview_window(label) {
                let _ = existing.show();
                let _ = existing.unminimize();
                let _ = existing.set_focus();
                return Ok(ScreenPinWindowSnapshot {
                    pin_id,
                    window_label: label.clone(),
                });
            }
        }
    }

    let value = state
        .runtime
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块运行状态不可用。".to_string(),
        })?
        .invoke(
            &module_id,
            &record,
            "getPin",
            serde_json::json!({ "pinId": pin_id }),
        )
        .map_err(CommandError::from)?;
    let pin = value.get("pin").unwrap_or(&value);
    let data_url = pin
        .get("dataUrl")
        .and_then(Value::as_str)
        .filter(|value| value.starts_with("data:image/png;base64,"))
        .ok_or_else(|| CommandError {
            code: "pinDataInvalid",
            message: "截图数据不可用。".to_string(),
        })?
        .to_string();
    if data_url.len() > web::MAX_PIN_DATA_URL_BYTES {
        return Err(CommandError {
            code: "pinDataTooLarge",
            message: "截图数据超过浮窗限制。".to_string(),
        });
    }
    let encoded = data_url
        .strip_prefix("data:image/png;base64,")
        .ok_or_else(|| CommandError {
            code: "pinDataInvalid",
            message: "截图编码格式不可用。".to_string(),
        })?;
    use base64::{engine::general_purpose::STANDARD as BASE64, Engine as _};
    let decoded = BASE64.decode(encoded).map_err(|_| CommandError {
        code: "pinDataInvalid",
        message: "截图编码格式不可用。".to_string(),
    })?;
    if decoded.is_empty() || decoded.len() > 700 * 1024 {
        return Err(CommandError {
            code: "pinDataTooLarge",
            message: "截图数据超过浮窗限制。".to_string(),
        });
    }
    let x = bounded_i32(pin.get("x"), -32_000, 32_000).unwrap_or(0);
    let y = bounded_i32(pin.get("y"), -32_000, 32_000).unwrap_or(0);
    let width = bounded_i32(pin.get("width"), 160, 2_048).unwrap_or(640);
    let height = bounded_i32(pin.get("height"), 120, 2_048).unwrap_or(360);
    let token = new_nonce_for_window().map_err(|message| CommandError {
        code: "windowTokenUnavailable",
        message,
    })?;
    let label = format!("screenpin-{token}");
    let pin_record = ScreenPinWindowRecord {
        pin_id: pin_id.clone(),
        data_url,
    };
    state
        .screenpin_windows
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "浮窗状态不可用。".to_string(),
        })?
        .insert(token.clone(), pin_record);
    // Wry/WebView2 can deadlock when a WebviewWindowBuilder is used directly
    // from a synchronous invoke handler. Build the window on a detached
    // worker, as recommended by Tauri's Windows guidance, and return the
    // opaque handle immediately. The token map keeps the image available until
    // the worker finishes creating the window.
    let app_for_window = app.clone();
    let label_for_window = label.clone();
    let token_for_window = token.clone();
    let queued = thread::Builder::new()
        .name("qing-screenpin-window".to_string())
        .spawn(move || {
            let url = match tauri::Url::parse(&format!("qpin://localhost/{token_for_window}")) {
                Ok(url) => url,
                Err(error) => {
                    eprintln!("Screen Pin window URL unavailable: {error}");
                    remove_screenpin_window_record(&app_for_window, &token_for_window);
                    return;
                }
            };
            let built = tauri::WebviewWindowBuilder::new(
                &app_for_window,
                label_for_window.clone(),
                tauri::WebviewUrl::CustomProtocol(url),
            )
            .title("Screen Pin")
            .inner_size(width as f64, height as f64)
            .min_inner_size(160.0, 120.0)
            .position(x as f64, y as f64)
            .resizable(true)
            .always_on_top(true)
            .build();
            let pin_window = match built {
                Ok(window) => window,
                Err(error) => {
                    eprintln!("Screen Pin window unavailable: {error}");
                    remove_screenpin_window_record(&app_for_window, &token_for_window);
                    return;
                }
            };
            let app_for_close = app_for_window.clone();
            let token_for_close = token_for_window.clone();
            pin_window.on_window_event(move |event| {
                if matches!(
                    event,
                    tauri::WindowEvent::CloseRequested { .. } | tauri::WindowEvent::Destroyed
                ) {
                    remove_screenpin_window_record(&app_for_close, &token_for_close);
                }
            });
        });
    if let Err(error) = queued {
        remove_screenpin_window_record(&app, &token);
        return Err(CommandError {
            code: "windowUnavailable",
            message: format!("无法排队打开截图浮窗：{error}"),
        });
    }
    Ok(ScreenPinWindowSnapshot {
        pin_id,
        window_label: label,
    })
}

fn valid_screenpin_pin_id(value: &str) -> bool {
    value.len() >= 5
        && value.len() <= 24
        && value.starts_with("pin-")
        && value[4..].bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn bounded_i32(value: Option<&Value>, minimum: i32, maximum: i32) -> Option<i32> {
    value
        .and_then(Value::as_i64)
        .and_then(|value| i32::try_from(value).ok())
        .map(|value| value.clamp(minimum, maximum))
}

fn new_nonce_for_window() -> Result<String, String> {
    let mut bytes = [0_u8; 12];
    getrandom::fill(&mut bytes).map_err(|error| format!("无法生成浮窗 token：{error}"))?;
    let mut token = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        let _ = write!(&mut token, "{byte:02x}");
    }
    Ok(token)
}

fn close_screenpin_windows<R: tauri::Runtime>(app: &tauri::AppHandle<R>, state: &HostState) {
    let labels = state
        .screenpin_windows
        .lock()
        .ok()
        .map(|mut records| {
            let labels = records
                .keys()
                .map(|token| format!("screenpin-{token}"))
                .collect::<Vec<_>>();
            records.clear();
            labels
        })
        .unwrap_or_default();
    for label in labels {
        if let Some(window) = app.get_webview_window(&label) {
            let _ = window.close();
        }
    }
}

fn remove_screenpin_window_record<R: tauri::Runtime>(app: &tauri::AppHandle<R>, token: &str) {
    if let Some(state) = app.try_state::<HostState>() {
        if let Ok(mut records) = state.screenpin_windows.lock() {
            records.remove(token);
        }
    }
}

fn close_screenpin_pin_window<R: tauri::Runtime>(
    app: &tauri::AppHandle<R>,
    state: &HostState,
    pin_id: &str,
) {
    let labels = state
        .screenpin_windows
        .lock()
        .ok()
        .map(|mut records| {
            let labels = records
                .iter()
                .filter(|(_, record)| record.pin_id == pin_id)
                .map(|(token, _)| format!("screenpin-{token}"))
                .collect::<Vec<_>>();
            records.retain(|_, record| record.pin_id != pin_id);
            labels
        })
        .unwrap_or_default();
    for label in labels {
        if let Some(window) = app.get_webview_window(&label) {
            let _ = window.close();
        }
    }
}

#[cfg(desktop)]
fn register_module_hotkey_binding<R: tauri::Runtime + 'static>(
    app: &tauri::AppHandle<R>,
    state: &HostState,
    module_id: &str,
    requested: &str,
) -> Result<(), CommandError> {
    use std::str::FromStr;
    use tauri_plugin_global_shortcut::{GlobalShortcutExt, Shortcut};

    let shortcut = Shortcut::from_str(requested).map_err(|error| CommandError {
        code: "hotkeyInvalid",
        message: format!("快捷键格式无效：{error}"),
    })?;
    let main_hotkey = state
        .settings
        .lock()
        .ok()
        .map(|settings| settings.snapshot().toggle_hotkey);
    if main_hotkey
        .as_deref()
        .and_then(|value| Shortcut::from_str(value).ok())
        .is_some_and(|value| value.id() == shortcut.id())
    {
        return Err(CommandError {
            code: "hotkeyConflict",
            message: "模块快捷键不能与工具箱主窗口快捷键相同。".to_string(),
        });
    }

    let previous = state
        .module_hotkeys
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块快捷键状态不可用。".to_string(),
        })?
        .get(module_id)
        .cloned();
    if previous.as_deref() == Some(requested) {
        return Ok(());
    }
    {
        let bindings = state.module_hotkeys.lock().map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块快捷键状态不可用。".to_string(),
        })?;
        let duplicate = bindings
            .iter()
            .filter(|(id, _)| id.as_str() != module_id)
            .any(|(_, value)| {
                Shortcut::from_str(value)
                    .ok()
                    .is_some_and(|candidate| candidate.id() == shortcut.id())
            });
        if duplicate {
            return Err(CommandError {
                code: "hotkeyConflict",
                message: "该快捷键已被另一个模块占用。".to_string(),
            });
        }
    }

    // Release the old registration before claiming the new one. If the new
    // registration fails, restore the old binding so the in-memory map and
    // the OS registration never disagree.
    let previous_shortcut = previous
        .as_deref()
        .map(Shortcut::from_str)
        .transpose()
        .map_err(|error| CommandError {
            code: "hotkeyInvalid",
            message: format!("已保存的模块快捷键格式无效：{error}"),
        })?;
    if let Some(old) = previous_shortcut {
        app.global_shortcut()
            .unregister(old)
            .map_err(|error| CommandError {
                code: "hotkeyUnavailable",
                message: format!("无法更新旧模块快捷键：{error}"),
            })?;
    }

    if let Err(error) = register_native_module_shortcut(app, shortcut, module_id) {
        if let Some(old) = previous_shortcut {
            let _ = register_native_module_shortcut(app, old, module_id);
        }
        return Err(error);
    }

    let mut bindings = match state.module_hotkeys.lock() {
        Ok(bindings) => bindings,
        Err(_) => {
            // The OS registration succeeded, but the host state could not be
            // committed. Roll the native registration back so a later retry
            // cannot observe a shortcut that the map does not describe.
            let _ = app.global_shortcut().unregister(shortcut);
            if let Some(old) = previous_shortcut {
                if let Err(error) = register_native_module_shortcut(app, old, module_id) {
                    eprintln!(
                        "QingToolbox could not restore module hotkey after state lock failure: {}",
                        error.message
                    );
                }
            }
            return Err(CommandError {
                code: "stateUnavailable",
                message: "模块快捷键状态不可用。".to_string(),
            });
        }
    };
    bindings.insert(module_id.to_string(), requested.to_string());
    Ok(())
}

#[cfg(desktop)]
fn register_native_module_shortcut<R: tauri::Runtime + 'static>(
    app: &tauri::AppHandle<R>,
    shortcut: tauri_plugin_global_shortcut::Shortcut,
    module_id: &str,
) -> Result<(), CommandError> {
    use tauri_plugin_global_shortcut::{GlobalShortcutExt, ShortcutState};

    let module_id_for_callback = module_id.to_string();
    app.global_shortcut()
        .on_shortcut(shortcut, move |app, _shortcut, event| {
            if event.state() == ShortcutState::Pressed {
                toggle_module_window(app, &module_id_for_callback);
            }
        })
        .map_err(|error| CommandError {
            code: "hotkeyUnavailable",
            message: format!("无法注册模块快捷键：{error}"),
        })
}

#[cfg(not(desktop))]
fn register_module_hotkey_binding<R: tauri::Runtime + 'static>(
    _app: &tauri::AppHandle<R>,
    _state: &HostState,
    _module_id: &str,
    _requested: &str,
) -> Result<(), CommandError> {
    Err(CommandError {
        code: "hotkeyUnavailable",
        message: "当前平台不支持全局模块快捷键。".to_string(),
    })
}

#[cfg(desktop)]
fn clear_module_hotkey_binding<R: tauri::Runtime + 'static>(
    app: &tauri::AppHandle<R>,
    state: &HostState,
    module_id: &str,
) -> Result<(), CommandError> {
    use std::str::FromStr;
    use tauri_plugin_global_shortcut::{GlobalShortcutExt, Shortcut};

    let previous_value = state
        .module_hotkeys
        .lock()
        .map_err(|_| CommandError {
            code: "stateUnavailable",
            message: "模块快捷键状态不可用。".to_string(),
        })?
        .get(module_id)
        .cloned();
    let previous_shortcut = previous_value
        .as_deref()
        .map(Shortcut::from_str)
        .transpose()
        .map_err(|error| CommandError {
            code: "hotkeyInvalid",
            message: format!("已保存的模块快捷键格式无效：{error}"),
        })?;
    if let Some(previous) = previous_shortcut {
        app.global_shortcut()
            .unregister(previous)
            .map_err(|error| CommandError {
                code: "hotkeyUnavailable",
                message: format!("无法注销模块快捷键：{error}"),
            })?;
    }
    match state.module_hotkeys.lock() {
        Ok(mut bindings) => {
            bindings.remove(module_id);
            Ok(())
        }
        Err(_) => {
            // Keep the native registration and the in-memory map consistent
            // if the map becomes poisoned while clearing a shortcut.
            if let Some(previous) = previous_value {
                if let Ok(previous) = Shortcut::from_str(&previous) {
                    if let Err(error) = register_native_module_shortcut(app, previous, module_id) {
                        eprintln!(
                            "QingToolbox could not restore module hotkey after state lock failure: {}",
                            error.message
                        );
                    }
                }
            }
            Err(CommandError {
                code: "stateUnavailable",
                message: "模块快捷键状态不可用。".to_string(),
            })
        }
    }
}

#[cfg(not(desktop))]
fn clear_module_hotkey_binding<R: tauri::Runtime + 'static>(
    _app: &tauri::AppHandle<R>,
    state: &HostState,
    module_id: &str,
) -> Result<(), CommandError> {
    if let Ok(mut bindings) = state.module_hotkeys.lock() {
        bindings.remove(module_id);
    }
    Ok(())
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

/// Tauri receives OS file drops at the native window boundary. Keep the
/// paths out of Vue entirely: canonicalize and constrain them here, then
/// deliver them as a manifest-declared module event. This is deliberately
/// limited to launcher entries that Windows can execute/open directly.
fn sanitize_launcher_drop_paths(paths: &[PathBuf]) -> Vec<String> {
    let mut seen = BTreeSet::new();
    paths
        .iter()
        .take(MAX_EXTERNAL_DROP_PATHS)
        .filter(|path| path.is_absolute())
        .filter_map(|path| {
            let canonical = fs::canonicalize(path).ok()?;
            if !canonical.is_file() {
                return None;
            }
            let extension = canonical
                .extension()
                .and_then(|value| value.to_str())
                .map(|value| value.to_ascii_lowercase())?;
            if !matches!(extension.as_str(), "exe" | "lnk" | "url") {
                return None;
            }
            let value = canonical.to_string_lossy().to_string();
            if value.chars().count() > MAX_EXTERNAL_DROP_PATH_LENGTH {
                return None;
            }
            let key = value.replace('/', "\\").to_ascii_lowercase();
            seen.insert(key).then_some(value)
        })
        .collect()
}

/// Handle a native drop asynchronously so a slow module or a first-start
/// handshake never stalls Tauri's window event loop. A successful dispatch
/// emits only a path-free invalidation signal; the launcher Web UI then asks
/// its own module for a fresh, backend-projected state snapshot.
fn handle_window_drop<R: tauri::Runtime + 'static>(
    app: &tauri::AppHandle<R>,
    window_label: &str,
    paths: &[PathBuf],
) {
    let Some(module_id) = module_id_from_window_label(window_label) else {
        return;
    };
    if module_id != "qing.launcher" {
        return;
    }
    let paths = sanitize_launcher_drop_paths(paths);
    if paths.is_empty() {
        return;
    }
    let app = app.clone();
    let label = window_label.to_string();
    thread::spawn(move || {
        let Some(state) = app.try_state::<HostState>() else {
            return;
        };
        let record = match state.module_index.lock() {
            Ok(index) => index.get(&module_id).cloned(),
            Err(_) => None,
        };
        let Some(record) = record else {
            return;
        };
        if !record.events.contains("launcher.externalDrop") {
            return;
        }
        let result = state
            .runtime
            .lock()
            .map_err(|_| "runtime state unavailable".to_string())
            .and_then(|mut runtime| {
                runtime
                    .send_event(
                        &module_id,
                        &record,
                        "launcher.externalDrop",
                        serde_json::json!({ "paths": paths }),
                    )
                    .map_err(|error| error.message)
            });
        if result.is_ok() {
            let _ = app.emit_to(
                EventTarget::webview_window(label),
                MODULE_STATE_CHANGED_EVENT,
                serde_json::json!({ "reason": "externalDrop" }),
            );
        } else if let Err(error) = result {
            eprintln!("Qing Launcher external drop was rejected: {error}");
        }
    });
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
        builder = builder.plugin(tauri_plugin_autostart::init(
            tauri_plugin_autostart::MacosLauncher::LaunchAgent,
            None,
        ));
    }
    // Desktop smoke tests may run alongside the user's installed QingToolbox.
    // Keep the production single-instance behavior by default, while allowing
    // an explicitly opted-in test process to use its own host instance. The
    // environment variable is only set by local/CI smoke tooling; it is not
    // part of the normal user-facing launch path.
    let disable_single_instance = std::env::var_os("QING_TAURI_DISABLE_SINGLE_INSTANCE").is_some();
    if !disable_single_instance {
        builder = builder.plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            show_main_window(app);
        }));
    }
    builder
        .on_window_event(|window, event| {
            if let WindowEvent::DragDrop(DragDropEvent::Drop { paths, .. }) = event {
                handle_window_drop(window.app_handle(), window.label(), paths);
            }
        })
        .register_uri_scheme_protocol("qmod", |context, request| {
            serve_module_asset(context.app_handle(), request)
        })
        .register_uri_scheme_protocol("qpin", |context, request| {
            serve_screenpin_asset(context.app_handle(), request)
        })
        .manage(HostState::new())
        .invoke_handler(tauri::generate_handler![
            get_host_info,
            get_settings,
            get_session_logs,
            update_settings,
            set_module_startup_authorization,
            import_module,
            update_module,
            open_module_directory,
            remove_module,
            list_modules,
            hide_to_tray,
            start_module,
            open_module,
            stop_module,
            invoke_module,
            invoke_module_window,
            get_module_window_context,
            hide_module_window,
            set_module_hotkey,
            clear_module_hotkey,
            open_screenpin_pin,
            get_module_runtime,
            get_all_module_runtime
        ])
        .setup(|app| {
            start_runtime_supervisor(app.handle().clone());
            start_authorized_modules(&app.state::<HostState>());
            record_log(
                &app.state::<HostState>(),
                "Information",
                "Application",
                "QingToolbox session started.",
            );

            register_toggle_hotkey(app);
            sync_persisted_autostart(app);

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

fn autostart_sync_enabled() -> bool {
    // A normal release build owns its registration. Development and smoke
    // processes must not unexpectedly edit the user's Run key or launch agent;
    // set this switch only when an integration test explicitly opts in.
    if std::env::var("QING_TAURI_DISABLE_AUTOSTART_SYNC")
        .ok()
        .as_deref()
        == Some("1")
    {
        return false;
    }
    !cfg!(debug_assertions)
        || std::env::var("QING_TAURI_ENABLE_AUTOSTART_SYNC")
            .ok()
            .as_deref()
            == Some("1")
}

#[cfg(desktop)]
fn sync_launch_at_login<R: tauri::Runtime>(
    app: &tauri::AppHandle<R>,
    enabled: bool,
) -> Result<(), String> {
    use tauri_plugin_autostart::ManagerExt;

    let manager = app.autolaunch();
    if enabled {
        manager.enable().map_err(|error| error.to_string())
    } else {
        manager.disable().map_err(|error| error.to_string())
    }
}

#[cfg(not(desktop))]
fn sync_launch_at_login<R: tauri::Runtime>(
    _app: &tauri::AppHandle<R>,
    _enabled: bool,
) -> Result<(), String> {
    Ok(())
}

fn sync_persisted_autostart<R: tauri::Runtime>(app: &mut tauri::App<R>) {
    if !autostart_sync_enabled() {
        return;
    }
    let enabled = app
        .try_state::<HostState>()
        .and_then(|state| {
            state
                .settings
                .lock()
                .ok()
                .map(|settings| settings.snapshot().launch_at_login)
        })
        .unwrap_or(false);
    if let Err(error) = sync_launch_at_login(app.handle(), enabled) {
        eprintln!("QingToolbox autostart synchronization failed: {error}");
    }
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

fn toggle_module_window<R: tauri::Runtime>(app: &tauri::AppHandle<R>, module_id: &str) {
    let label = module_window_label(module_id);
    let Some(window) = app.get_webview_window(&label) else {
        return;
    };
    if window.is_visible().unwrap_or(false) {
        let _ = window.hide();
    } else {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

fn start_authorized_modules(state: &HostState) {
    let discovery = discover_modules(&state.roots);
    if let Ok(mut index) = state.module_index.lock() {
        *index = discovery.records;
    }
    let ids = state
        .settings
        .lock()
        .ok()
        .map(|settings| settings.snapshot().startup_module_ids)
        .unwrap_or_default();
    let Ok(index) = state.module_index.lock() else {
        return;
    };
    let Ok(mut runtime) = state.runtime.lock() else {
        return;
    };
    let mut started = 0_usize;
    let mut failed = 0_usize;
    for module_id in ids {
        if let Some(record) = index.get(&module_id) {
            if runtime.start(&module_id, record).is_ok() {
                started += 1;
            } else {
                failed += 1;
            }
        }
    }
    drop(runtime);
    drop(index);
    record_log(
        state,
        if failed == 0 {
            "Information"
        } else {
            "Warning"
        },
        "Runtime",
        format!("Startup authorization processed: {started} started, {failed} failed."),
    );
}

fn stop_all_modules<R: tauri::Runtime>(app: &tauri::AppHandle<R>) {
    if let Some(state) = app.try_state::<HostState>() {
        let ids = state
            .module_hotkeys
            .lock()
            .ok()
            .map(|bindings| bindings.keys().cloned().collect::<Vec<_>>())
            .unwrap_or_default();
        for id in ids {
            let _ = clear_module_hotkey_binding(app, &state, &id);
        }
        close_screenpin_windows(app, &state);
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
    use super::{
        civil_date_from_days, module_id_from_window_label, module_window_label, now_rfc3339,
        record_log, sanitize_launcher_drop_paths, HostState, MAX_SESSION_LOG_ENTRIES,
    };
    use std::{
        fs,
        time::{SystemTime, UNIX_EPOCH},
    };

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

    #[test]
    fn launcher_drop_boundary_accepts_only_existing_shortcuts_and_executables() {
        let root = std::env::temp_dir().join(format!(
            "qingtoolbox-drop-test-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .expect("clock")
                .as_nanos()
        ));
        fs::create_dir_all(&root).expect("drop test directory");
        let exe = root.join("Demo.EXE");
        let shortcut = root.join("Demo.lnk");
        let url = root.join("Demo.url");
        let text = root.join("Demo.txt");
        fs::write(&exe, b"exe").expect("exe");
        fs::write(&shortcut, b"shortcut").expect("shortcut");
        fs::write(&url, b"url").expect("url");
        fs::write(&text, b"text").expect("text");

        let accepted = sanitize_launcher_drop_paths(&[
            exe.clone(),
            exe.clone(),
            shortcut.clone(),
            url.clone(),
            text,
            root.join("missing.exe"),
        ]);
        assert_eq!(accepted.len(), 3);
        assert!(accepted
            .iter()
            .any(|value| value.to_ascii_lowercase().ends_with("demo.exe")));
        assert!(accepted
            .iter()
            .any(|value| value.to_ascii_lowercase().ends_with("demo.lnk")));
        assert!(accepted
            .iter()
            .any(|value| value.to_ascii_lowercase().ends_with("demo.url")));
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn session_logs_are_timestamped_and_bounded() {
        assert_eq!(civil_date_from_days(0), (1970, 1, 1));
        let timestamp = now_rfc3339();
        assert!(timestamp.ends_with('Z'));
        assert_eq!(timestamp.len(), 24);

        let state = HostState::new();
        for index in 0..(MAX_SESSION_LOG_ENTRIES + 3) {
            record_log(&state, "Information", "Test", format!("event-{index}"));
        }
        let entries = state.session_logs.lock().expect("session logs");
        assert_eq!(entries.len(), MAX_SESSION_LOG_ENTRIES);
        assert_eq!(
            entries.first().map(|entry| entry.message.as_str()),
            Some("event-3")
        );
        assert_eq!(
            entries.last().map(|entry| entry.message.as_str()),
            Some("event-258")
        );
    }
}
