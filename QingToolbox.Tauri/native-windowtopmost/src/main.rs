#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! Native Window Topmost module.
//!
//! Window handles never cross the module boundary as an executable capability.
//! The WebView receives a short-lived opaque `windowId`; the Rust process keeps
//! the HWND map and revalidates the target immediately before every native
//! operation.  This keeps the UI useful while avoiding a generic Win32 bridge.

use std::{
    io::{self, BufRead, BufReader, BufWriter, Write},
    sync::{Arc, Mutex},
    thread,
    time::Duration,
};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

const HOST_PROTOCOL_VERSION: u16 = 1;
const HOST_MAX_FRAME_BYTES: usize = 1024 * 1024;
const MAX_TITLE_BYTES: usize = 1024;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HostEnvelope {
    protocol_version: u16,
    message_type: String,
    request_id: String,
    payload: Value,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct HostResponse<'a> {
    protocol_version: u16,
    message_type: &'a str,
    request_id: &'a str,
    payload: Value,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<HostErrorBody>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct HostErrorBody {
    code: &'static str,
    message: String,
}

#[derive(Debug, Clone)]
struct ModuleError {
    code: &'static str,
    message: String,
}

impl ModuleError {
    fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

#[derive(Debug, Clone)]
struct WindowTarget {
    id: String,
    hwnd: isize,
    pid: u32,
    title: String,
    process_name: String,
    is_topmost: bool,
}

#[derive(Debug, Serialize, Clone)]
#[serde(rename_all = "camelCase")]
struct WindowRecord {
    id: String,
    title: String,
    process_name: String,
    process_id: u32,
    handle_text: String,
    is_topmost: bool,
}

impl From<&WindowTarget> for WindowRecord {
    fn from(window: &WindowTarget) -> Self {
        Self {
            id: window.id.clone(),
            title: window.title.clone(),
            process_name: window.process_name.clone(),
            process_id: window.pid,
            handle_text: format!("0x{:X}", window.hwnd),
            is_topmost: window.is_topmost,
        }
    }
}

#[derive(Debug, Default)]
struct WindowState {
    windows: Vec<WindowTarget>,
    selected_window_id: Option<String>,
    status: String,
    error: Option<String>,
}

#[derive(Clone, Debug)]
struct WindowApp {
    state: Arc<Mutex<WindowState>>,
}

impl WindowApp {
    fn new() -> Self {
        let app = Self {
            state: Arc::new(Mutex::new(WindowState {
                status: "ready".to_string(),
                ..WindowState::default()
            })),
        };
        app.refresh_windows(None);
        app
    }

    fn snapshot(&self) -> Value {
        let state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let windows = state
            .windows
            .iter()
            .map(WindowRecord::from)
            .collect::<Vec<_>>();
        json!({
            "windows": windows,
            "selectedWindowId": state.selected_window_id,
            "status": state.status,
            "error": state.error,
        })
    }

    fn invoke(&self, method: &str, payload: &Value) -> Result<Value, ModuleError> {
        match method {
            "getState" => Ok(self.snapshot()),
            "refresh" => {
                self.refresh_windows(None);
                Ok(self.snapshot())
            }
            "pickWindow" => {
                // Keep the three-second affordance used by the old module. The
                // status is also visible when the operation returns.
                self.set_status("pickPending", None);
                thread::sleep(Duration::from_secs(3));
                let picked = pick_window_under_cursor();
                self.refresh_windows(picked);
                let mut state = self
                    .state
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner());
                if let Some(hwnd) = picked {
                    let id = window_id(hwnd);
                    if state.windows.iter().any(|window| window.id == id) {
                        state.selected_window_id = Some(id);
                        state.status = "selected".to_string();
                        state.error = None;
                    } else {
                        state.status = "pickFailed".to_string();
                        state.error = None;
                    }
                } else {
                    state.status = "pickFailed".to_string();
                    state.error = None;
                }
                drop(state);
                Ok(self.snapshot())
            }
            "setTopmost" => self.set_selected(payload, true),
            "removeTopmost" => self.set_selected(payload, false),
            "clearSelection" => {
                let mut state = self
                    .state
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner());
                state.selected_window_id = None;
                state.status = "ready".to_string();
                state.error = None;
                Ok(json!({
                    "windows": state.windows.iter().map(WindowRecord::from).collect::<Vec<_>>(),
                    "selectedWindowId": state.selected_window_id,
                    "status": state.status,
                    "error": state.error,
                }))
            }
            _ => Err(ModuleError::new("unknown_method", "未知的窗口置顶操作。")),
        }
    }

    fn set_selected(&self, payload: &Value, topmost: bool) -> Result<Value, ModuleError> {
        let id = payload
            .get("windowId")
            .and_then(Value::as_str)
            .ok_or_else(|| ModuleError::new("invalid_payload", "windowId 必须是字符串。"))?;
        if !valid_window_id(id) {
            return Err(ModuleError::new("invalid_payload", "windowId 无效。"));
        }
        let target = {
            let state = self
                .state
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            state.windows.iter().find(|window| window.id == id).cloned()
        }
        .ok_or_else(|| ModuleError::new("window_unavailable", "目标窗口已不可用，请刷新列表。"))?;

        set_window_topmost(target.hwnd, topmost).map_err(|message| {
            ModuleError::new("operation_failed", format!("无法修改目标窗口：{message}"))
        })?;
        self.refresh_windows(Some(target.hwnd));
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        state.selected_window_id = Some(id.to_string());
        state.status = if topmost {
            "topmostSet"
        } else {
            "topmostRemoved"
        }
        .to_string();
        state.error = None;
        drop(state);
        Ok(self.snapshot())
    }

    fn set_status(&self, status: &str, error: Option<String>) {
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        state.status = status.to_string();
        state.error = error;
    }

    fn refresh_windows(&self, selected_hwnd: Option<isize>) {
        let previous = {
            let state = self
                .state
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            selected_hwnd
                .map(window_id)
                .or_else(|| state.selected_window_id.clone())
        };
        let windows = enumerate_windows();
        let selected = previous.filter(|id| windows.iter().any(|window| &window.id == id));
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        state.windows = windows;
        state.selected_window_id = selected;
        if state.status != "pickPending" {
            state.status = "refreshed".to_string();
        }
        state.error = None;
    }
}

fn valid_token(value: &str, max: usize) -> bool {
    !value.is_empty()
        && value.len() <= max
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_' | b':'))
}

fn valid_window_id(value: &str) -> bool {
    value.len() >= 3
        && value.len() <= 32
        && value.starts_with("w-")
        && value[2..].bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn valid_envelope(envelope: &HostEnvelope) -> bool {
    envelope.protocol_version == HOST_PROTOCOL_VERSION
        && valid_token(&envelope.message_type, 64)
        && valid_token(&envelope.request_id, 128)
}

fn write_response(
    writer: &mut BufWriter<impl Write>,
    message_type: &str,
    request_id: &str,
    payload: Value,
    error: Option<HostErrorBody>,
) {
    let response = HostResponse {
        protocol_version: HOST_PROTOCOL_VERSION,
        message_type,
        request_id,
        payload,
        error,
    };
    if let Ok(bytes) = serde_json::to_vec(&response) {
        if bytes.len() <= HOST_MAX_FRAME_BYTES {
            let _ = writer.write_all(&bytes);
            let _ = writer.write_all(b"\n");
            let _ = writer.flush();
        }
    }
}

fn main() {
    let app = WindowApp::new();
    let module_id =
        std::env::var("QINGTOOLBOX_MODULE_ID").unwrap_or_else(|_| "qing.windowtopmost".to_string());
    let nonce = std::env::var("QINGTOOLBOX_MODULE_NONCE").unwrap_or_default();
    let stdin = io::stdin();
    let stdout = io::stdout();
    let mut reader = BufReader::new(stdin.lock());
    let mut writer = BufWriter::new(stdout.lock());
    let mut line = Vec::new();
    let mut handshaken = false;

    loop {
        line.clear();
        let read = match reader.read_until(b'\n', &mut line) {
            Ok(read) => read,
            Err(_) => break,
        };
        if read == 0 {
            break;
        }
        if line.len() > HOST_MAX_FRAME_BYTES {
            write_response(
                &mut writer,
                "module.protocol.response",
                "unknown",
                json!({}),
                Some(HostErrorBody {
                    code: "frame_too_large",
                    message: "模块请求帧过大。".to_string(),
                }),
            );
            break;
        }
        let envelope = match serde_json::from_slice::<HostEnvelope>(&line) {
            Ok(envelope) if valid_envelope(&envelope) => envelope,
            _ => {
                write_response(
                    &mut writer,
                    "module.protocol.response",
                    "unknown",
                    json!({}),
                    Some(HostErrorBody {
                        code: "invalid_frame",
                        message: "模块请求帧无效。".to_string(),
                    }),
                );
                continue;
            }
        };
        match envelope.message_type.as_str() {
            "module.hello.request" if !handshaken => {
                let valid = envelope.payload.get("moduleId").and_then(Value::as_str)
                    == Some(module_id.as_str())
                    && envelope.payload.get("nonce").and_then(Value::as_str)
                        == Some(nonce.as_str());
                if !valid {
                    write_response(
                        &mut writer,
                        "module.hello.response",
                        &envelope.request_id,
                        json!({}),
                        Some(HostErrorBody {
                            code: "hello_rejected",
                            message: "模块 hello 校验失败。".to_string(),
                        }),
                    );
                    break;
                }
                handshaken = true;
                write_response(
                    &mut writer,
                    "module.hello.response",
                    &envelope.request_id,
                    json!({ "moduleId": module_id, "nonce": nonce, "name": "Window Topmost", "protocolVersion": HOST_PROTOCOL_VERSION }),
                    None,
                );
            }
            "module.invoke.request" if handshaken => {
                let method = envelope
                    .payload
                    .get("method")
                    .and_then(Value::as_str)
                    .unwrap_or_default();
                let payload = envelope.payload.get("payload").unwrap_or(&Value::Null);
                match app.invoke(method, payload) {
                    Ok(value) => write_response(
                        &mut writer,
                        "module.invoke.response",
                        &envelope.request_id,
                        value,
                        None,
                    ),
                    Err(error) => write_response(
                        &mut writer,
                        "module.invoke.response",
                        &envelope.request_id,
                        json!({}),
                        Some(HostErrorBody {
                            code: error.code,
                            message: error.message,
                        }),
                    ),
                }
            }
            "module.shutdown.request" if handshaken => {
                write_response(
                    &mut writer,
                    "module.shutdown.response",
                    &envelope.request_id,
                    json!({}),
                    None,
                );
                break;
            }
            _ => write_response(
                &mut writer,
                "module.protocol.response",
                &envelope.request_id,
                json!({}),
                Some(HostErrorBody {
                    code: "invalid_message",
                    message: "模块消息顺序或类型无效。".to_string(),
                }),
            ),
        }
    }
}

fn window_id(hwnd: isize) -> String {
    format!("w-{hwnd:X}")
}

#[cfg(windows)]
fn enumerate_windows() -> Vec<WindowTarget> {
    use windows_sys::Win32::{
        Foundation::{HWND, LPARAM},
        UI::WindowsAndMessaging::{
            EnumWindows, GetWindowLongPtrW, GetWindowTextLengthW, GetWindowTextW,
            GetWindowThreadProcessId, IsWindowVisible, GWL_EXSTYLE, WS_EX_TOPMOST,
        },
    };

    struct EnumContext {
        current_pid: u32,
        windows: Vec<WindowTarget>,
    }

    unsafe extern "system" fn callback(hwnd: HWND, lparam: LPARAM) -> windows_sys::core::BOOL {
        let context = &mut *(lparam as *mut EnumContext);
        if hwnd.is_null() || IsWindowVisible(hwnd) == 0 {
            return 1;
        }
        let length = GetWindowTextLengthW(hwnd);
        if length <= 0 {
            return 1;
        }
        let mut title = vec![0u16; (length as usize).saturating_add(1)];
        let written = GetWindowTextW(hwnd, title.as_mut_ptr(), title.len() as i32);
        if written <= 0 {
            return 1;
        }
        title.truncate(written as usize);
        let title = String::from_utf16_lossy(&title);
        if title.is_empty() || title.len() > MAX_TITLE_BYTES {
            return 1;
        }
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, &mut pid);
        if pid == 0 || pid == context.current_pid {
            return 1;
        }
        let process_name = process_name(pid);
        let style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        let raw = hwnd as isize;
        context.windows.push(WindowTarget {
            id: window_id(raw),
            hwnd: raw,
            pid,
            title,
            process_name,
            is_topmost: (style as u32 & WS_EX_TOPMOST) != 0,
        });
        1
    }

    let mut context = EnumContext {
        current_pid: std::process::id(),
        windows: Vec::new(),
    };
    unsafe {
        EnumWindows(Some(callback), &mut context as *mut EnumContext as LPARAM);
    }
    context.windows.sort_by(|left, right| {
        left.title
            .to_lowercase()
            .cmp(&right.title.to_lowercase())
            .then_with(|| left.id.cmp(&right.id))
    });
    // A pathological desktop can expose many owned/utility windows. Keep the
    // UI bounded while retaining deterministic ordering.
    context.windows.truncate(256);
    context.windows
}

#[cfg(not(windows))]
fn enumerate_windows() -> Vec<WindowTarget> {
    Vec::new()
}

#[cfg(windows)]
fn process_name(pid: u32) -> String {
    use std::path::Path;
    use windows_sys::Win32::{
        Foundation::CloseHandle,
        System::Threading::{
            OpenProcess, QueryFullProcessImageNameW, PROCESS_QUERY_LIMITED_INFORMATION,
        },
    };
    let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if handle.is_null() {
        return format!("PID {pid}");
    }
    let mut buffer = vec![0u16; 1024];
    let mut length = buffer.len() as u32;
    let result = unsafe { QueryFullProcessImageNameW(handle, 0, buffer.as_mut_ptr(), &mut length) };
    unsafe { CloseHandle(handle) };
    if result == 0 || length == 0 {
        return format!("PID {pid}");
    }
    let path = String::from_utf16_lossy(&buffer[..length as usize]);
    Path::new(&path)
        .file_stem()
        .and_then(|value| value.to_str())
        .filter(|value| !value.is_empty())
        .map(str::to_string)
        .unwrap_or_else(|| format!("PID {pid}"))
}

#[cfg(not(windows))]
fn process_name(pid: u32) -> String {
    format!("PID {pid}")
}

#[cfg(windows)]
fn pick_window_under_cursor() -> Option<isize> {
    use windows_sys::Win32::{
        Foundation::POINT,
        UI::WindowsAndMessaging::{GetAncestor, GetCursorPos, WindowFromPoint, GA_ROOT},
    };
    let mut point = POINT { x: 0, y: 0 };
    if unsafe { GetCursorPos(&mut point) } == 0 {
        return None;
    }
    let child = unsafe { WindowFromPoint(point) };
    if child.is_null() {
        return None;
    }
    let root = unsafe { GetAncestor(child, GA_ROOT) };
    let target = if root.is_null() { child } else { root };
    Some(target as isize)
}

#[cfg(not(windows))]
fn pick_window_under_cursor() -> Option<isize> {
    None
}

#[cfg(windows)]
fn set_window_topmost(hwnd: isize, topmost: bool) -> Result<(), String> {
    use windows_sys::Win32::{
        Foundation::{GetLastError, HWND},
        UI::WindowsAndMessaging::{
            IsWindow, SetWindowPos, HWND_NOTOPMOST, HWND_TOPMOST, SWP_NOACTIVATE, SWP_NOMOVE,
            SWP_NOSIZE,
        },
    };
    let handle = hwnd as HWND;
    if handle.is_null() || unsafe { IsWindow(handle) } == 0 {
        return Err("目标窗口已经关闭。".to_string());
    }
    let insert_after = if topmost {
        HWND_TOPMOST
    } else {
        HWND_NOTOPMOST
    };
    let result = unsafe {
        SetWindowPos(
            handle,
            insert_after,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
        )
    };
    if result == 0 {
        Err(format!("Win32 错误 {}。", unsafe { GetLastError() }))
    } else {
        Ok(())
    }
}

#[cfg(not(windows))]
fn set_window_topmost(_hwnd: isize, _topmost: bool) -> Result<(), String> {
    Err("窗口置顶仅支持 Windows。".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn opaque_window_ids_are_strict() {
        assert!(valid_window_id("w-1A2B"));
        assert!(!valid_window_id("0x1A2B"));
        assert!(!valid_window_id("w-"));
        assert!(!valid_window_id("w-zz"));
    }

    #[test]
    fn unknown_window_id_is_rejected_before_native_call() {
        let app = WindowApp::new();
        let error = app
            .invoke("setTopmost", &json!({ "windowId": "w-1" }))
            .unwrap_err();
        assert_eq!(error.code, "window_unavailable");
    }

    #[test]
    fn envelope_validation_rejects_wrong_protocol() {
        let envelope = HostEnvelope {
            protocol_version: 2,
            message_type: "module.invoke.request".to_string(),
            request_id: "x".to_string(),
            payload: json!({}),
        };
        assert!(!valid_envelope(&envelope));
    }
}
