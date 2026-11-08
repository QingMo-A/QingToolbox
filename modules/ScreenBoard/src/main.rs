#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod settings;

use serde::Deserialize;
use serde_json::{json, Value};
use settings::Settings;
use std::{
    io::{self, BufRead, BufReader, Write},
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
};
use tauri::{
    Emitter, Manager, PhysicalPosition, PhysicalSize, WebviewUrl, WebviewWindow,
    WebviewWindowBuilder,
};
use tauri_plugin_global_shortcut::{GlobalShortcutExt, ShortcutState};

const ID: &str = "qing.screenboard";
#[derive(Clone, Copy, serde::Serialize)]
struct Bounds {
    x: i32,
    y: i32,
    width: u32,
    height: u32,
}
struct Data {
    settings: Settings,
    active: bool,
    visible: bool,
    passthrough: bool,
    ready: Vec<String>,
    hotkey: bool,
    error: Option<String>,
}
struct Shared {
    data: Mutex<Data>,
    path: PathBuf,
    bounds: Bounds,
    shutting_down: AtomicBool,
}
impl Shared {
    fn lock(&self) -> std::sync::MutexGuard<'_, Data> {
        self.data.lock().unwrap_or_else(|e| e.into_inner())
    }
    fn snapshot(&self) -> Value {
        let d = self.lock();
        json!({"settings":d.settings,"active":d.active,"visible":d.visible,"passthrough":d.passthrough,"shortcutAvailable":d.hotkey,"error":d.error,"bounds":self.bounds})
    }
}
fn desktop() -> Bounds {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::UI::WindowsAndMessaging::*;
        return Bounds {
            x: GetSystemMetrics(SM_XVIRTUALSCREEN),
            y: GetSystemMetrics(SM_YVIRTUALSCREEN),
            width: GetSystemMetrics(SM_CXVIRTUALSCREEN).max(1) as u32,
            height: GetSystemMetrics(SM_CYVIRTUALSCREEN).max(1) as u32,
        };
    }
    #[cfg(not(windows))]
    Bounds {
        x: 0,
        y: 0,
        width: 1920,
        height: 1080,
    }
}
fn sync(app: &tauri::AppHandle, focus: bool) {
    let app = app.clone();
    let _ = app.clone().run_on_main_thread(move || {
        let shared = app.state::<Arc<Shared>>();
        let (visible, pass, ready) = {
            let d = shared.lock();
            (d.visible && d.active, d.passthrough, d.ready.len() == 2)
        };
        if let (Some(canvas), Some(tools)) = (
            app.get_webview_window("canvas"),
            app.get_webview_window("tools"),
        ) {
            if visible && ready {
                let _ = canvas.set_ignore_cursor_events(pass);
                let _ = canvas.show();
                if focus && !pass {
                    let _ = canvas.set_focus();
                }
                let _ = tools.show();
                // Raise the small toolbar after the full-screen drawing layer.
                let _ = tools.set_always_on_top(false);
                let _ = tools.set_always_on_top(true);
            } else {
                let _ = canvas.hide();
                let _ = tools.hide();
            }
        }
        let _ = app.emit("board-state", shared.snapshot());
    });
}
fn save_settings(shared: &Shared, patch: Value) -> Result<(), String> {
    let mut d = shared.lock();
    let mut value = serde_json::to_value(&d.settings).map_err(|e| e.to_string())?;
    let fields = patch.as_object().ok_or("设置须为对象。")?;
    for (key, val) in fields {
        value[key] = val.clone();
    }
    let next: Settings = serde_json::from_value(value).map_err(|_| "设置格式无效。")?;
    next.validate()?;
    std::fs::create_dir_all(shared.path.parent().ok_or("设置路径无效。")?)
        .map_err(|e| format!("无法保存设置：{e}"))?;
    let temp = shared.path.with_extension("json.tmp");
    std::fs::write(
        &temp,
        serde_json::to_vec_pretty(&next).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())?;
    std::fs::rename(temp, &shared.path).map_err(|e| e.to_string())?;
    d.settings = next;
    Ok(())
}
fn request(app: &tauri::AppHandle, method: &str, payload: &Value) -> Result<Value, String> {
    let shared = app.state::<Arc<Shared>>();
    match method {
        "getState" => {}
        "setSettings" => {
            save_settings(&shared, payload.clone())?;
            sync(app, false);
        }
        "showBoard" => {
            let mut d = shared.lock();
            if !d.active {
                return Err("请先启用屏幕画板模块。".into());
            }
            d.visible = true;
            d.passthrough = false;
            drop(d);
            sync(app, true);
        }
        "hideBoard" => {
            shared.lock().visible = false;
            sync(app, false);
        }
        "togglePassthrough" => {
            let mut d = shared.lock();
            d.passthrough = !d.passthrough;
            drop(d);
            sync(app, true);
        }
        _ => return Err("未知画板操作。".into()),
    }
    Ok(shared.snapshot())
}
fn local(window: &WebviewWindow) -> Result<(), String> {
    if ["canvas", "tools"].contains(&window.label()) {
        Ok(())
    } else {
        Err("非法画板界面。".into())
    }
}
#[tauri::command]
fn board_state(
    window: WebviewWindow,
    state: tauri::State<'_, Arc<Shared>>,
) -> Result<Value, String> {
    local(&window)?;
    Ok(state.snapshot())
}
#[tauri::command]
fn board_ready(window: WebviewWindow, app: tauri::AppHandle) -> Result<(), String> {
    local(&window)?;
    {
        let shared = app.state::<Arc<Shared>>();
        let mut d = shared.lock();
        if !d.ready.iter().any(|v| v == window.label()) {
            d.ready.push(window.label().into());
        }
    }
    sync(&app, false);
    Ok(())
}
#[tauri::command]
fn board_settings(
    window: WebviewWindow,
    app: tauri::AppHandle,
    patch: Value,
) -> Result<Value, String> {
    local(&window)?;
    request(&app, "setSettings", &patch)
}
#[tauri::command]
fn board_control(
    window: WebviewWindow,
    app: tauri::AppHandle,
    action: String,
) -> Result<Value, String> {
    local(&window)?;
    match action.as_str() {
        "hide" => request(&app, "hideBoard", &json!({})),
        "pass" => request(&app, "togglePassthrough", &json!({})),
        "drag" if window.label() == "tools" => {
            window.start_dragging().map_err(|e| e.to_string())?;
            Ok(json!({}))
        }
        "undo" | "redo" | "clear" | "delete" | "applyStyle" => {
            app.emit_to("canvas", "board-action", action)
                .map_err(|e| e.to_string())?;
            Ok(json!({}))
        }
        _ => Err("未知画板控件。".into()),
    }
}
#[tauri::command]
fn board_sample(
    window: WebviewWindow,
    app: tauri::AppHandle,
    x: i32,
    y: i32,
) -> Result<String, String> {
    local(&window)?;
    let shared = app.state::<Arc<Shared>>();
    let b = shared.bounds;
    if !shared.lock().visible
        || x < b.x
        || y < b.y
        || i64::from(x) >= i64::from(b.x) + i64::from(b.width)
        || i64::from(y) >= i64::from(b.y) + i64::from(b.height)
    {
        return Err("取色坐标不在屏幕内。".into());
    }
    // Sample only on an explicit Alt+click. Hide both module-owned surfaces so
    // the sampled pixel is the application beneath, not our own brush/UI.
    let canvas = app.get_webview_window("canvas").ok_or("画板未创建。")?;
    let tools = app.get_webview_window("tools").ok_or("工具条未创建。")?;
    canvas.hide().map_err(|e| e.to_string())?;
    let _ = tools.hide();
    #[cfg(windows)]
    let result = unsafe {
        use windows_sys::Win32::Graphics::{
            Dwm::DwmFlush,
            Gdi::{GetDC, GetPixel, ReleaseDC, CLR_INVALID},
        };
        DwmFlush();
        let dc = GetDC(std::ptr::null_mut());
        if dc.is_null() {
            Err("无法读取屏幕颜色。".to_string())
        } else {
            let pixel = GetPixel(dc, x, y);
            ReleaseDC(std::ptr::null_mut(), dc);
            if pixel == CLR_INVALID {
                Err("此屏幕位置无法取色。".into())
            } else {
                Ok(format!(
                    "#{:02x}{:02x}{:02x}",
                    pixel & 255,
                    (pixel >> 8) & 255,
                    (pixel >> 16) & 255
                ))
            }
        }
    };
    #[cfg(not(windows))]
    let result: Result<String, String> = Err("屏幕取色仅支持 Windows。".into());
    sync(&app, true);
    if let Ok(ref color) = result {
        save_settings(&shared, json!({"color":color}))?;
        sync(&app, false);
    }
    result
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Envelope {
    protocol_version: u16,
    message_type: String,
    request_id: String,
    payload: Value,
}
fn reply(out: &mut impl Write, kind: &str, id: &str, result: Result<Value, String>) {
    let mut v = json!({"protocolVersion":1,"messageType":kind,"requestId":id,"payload":{}});
    match result {
        Ok(p) => v["payload"] = p,
        Err(e) => v["error"] = json!({"code":"screen_board_error","message":e}),
    }
    let _ = serde_json::to_writer(&mut *out, &v);
    let _ = writeln!(out);
    let _ = out.flush();
}
fn serve(app: tauri::AppHandle) {
    let nonce = std::env::var("QINGTOOLBOX_MODULE_NONCE").unwrap_or_default();
    let mut reader = BufReader::new(io::stdin());
    let mut out = io::BufWriter::new(io::stdout());
    let mut hello = false;
    loop {
        let mut line = Vec::new();
        if !matches!(std::io::Read::take(&mut reader,1_048_577).read_until(b'\n',&mut line),Ok(n) if n>0)
            || line.len() > 1_048_576
        {
            break;
        }
        let req: Envelope = match serde_json::from_slice(&line) {
            Ok(r) => r,
            Err(_) => break,
        };
        if req.protocol_version != 1 || req.request_id.is_empty() || req.request_id.len() > 128 {
            break;
        }
        match req.message_type.as_str() {
            "module.hello.request" if !hello => {
                if nonce.is_empty()
                    || req.payload["nonce"] != nonce
                    || req.payload["moduleId"] != ID
                {
                    break;
                }
                hello = true;
                reply(
                    &mut out,
                    "module.hello.response",
                    &req.request_id,
                    Ok(
                        json!({"moduleId":ID,"nonce":nonce,"lifecycleVersion":1,"version":env!("CARGO_PKG_VERSION")}),
                    ),
                );
            }
            "module.lifecycle.request" if hello => {
                let Some(active) = req.payload["active"].as_bool() else {
                    break;
                };
                {
                    let s = app.state::<Arc<Shared>>();
                    let mut d = s.lock();
                    d.active = active;
                    if !active {
                        d.visible = false;
                    }
                }
                let shared = app.state::<Arc<Shared>>();
                if active && !shared.lock().hotkey {
                    let result = app.global_shortcut().register("Ctrl+Shift+D");
                    let mut d = shared.lock();
                    d.hotkey = result.is_ok();
                    d.error = result
                        .err()
                        .map(|e| format!("Ctrl+Shift+D 被占用，可使用工具条切换穿透：{e}"));
                } else if !active && shared.lock().hotkey {
                    let _ = app.global_shortcut().unregister("Ctrl+Shift+D");
                    shared.lock().hotkey = false;
                }
                sync(&app, false);
                reply(
                    &mut out,
                    "module.lifecycle.response",
                    &req.request_id,
                    Ok(json!({"active":active})),
                );
            }
            "module.invoke.request" if hello => reply(
                &mut out,
                "module.invoke.response",
                &req.request_id,
                request(
                    &app,
                    req.payload["method"].as_str().unwrap_or(""),
                    &req.payload["payload"],
                ),
            ),
            "module.shutdown.request" if hello => {
                reply(
                    &mut out,
                    "module.shutdown.response",
                    &req.request_id,
                    Ok(json!({})),
                );
                break;
            }
            _ => break,
        }
    }
    app.state::<Arc<Shared>>()
        .shutting_down
        .store(true, Ordering::SeqCst);
    app.exit(0);
}
fn main() {
    // No normal standalone launch: this executable is owned by the host's
    // authenticated process channel. Do not create overlays for stray starts.
    if std::env::var("QINGTOOLBOX_MODULE_NONCE")
        .unwrap_or_default()
        .is_empty()
    {
        return;
    }
    let dir = std::env::var_os("QINGTOOLBOX_MODULE_DATA_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| std::env::temp_dir().join("QingToolbox/qing.screenboard"));
    let path = dir.join("settings.json");
    let settings = std::fs::metadata(&path)
        .ok()
        .filter(|m| m.len() < 32768)
        .and_then(|_| std::fs::read(&path).ok())
        .and_then(|b| serde_json::from_slice::<Settings>(&b).ok())
        .filter(|s| s.validate().is_ok())
        .unwrap_or_default();
    let shared = Arc::new(Shared {
        data: Mutex::new(Data {
            settings,
            active: false,
            visible: false,
            passthrough: false,
            ready: Vec::new(),
            hotkey: false,
            error: None,
        }),
        path,
        bounds: desktop(),
        shutting_down: AtomicBool::new(false),
    });
    let app = tauri::Builder::default()
        .manage(shared)
        .plugin(
            tauri_plugin_global_shortcut::Builder::new()
                .with_handler(|app, _, event| {
                    if event.state() != ShortcutState::Pressed {
                        return;
                    }
                    let shared = app.state::<Arc<Shared>>();
                    let mut d = shared.lock();
                    if !d.active {
                        return;
                    }
                    if !d.visible {
                        d.visible = true;
                        d.passthrough = false;
                    } else {
                        d.passthrough = !d.passthrough;
                    }
                    drop(d);
                    sync(app, true);
                })
                .build(),
        )
        .invoke_handler(tauri::generate_handler![
            board_state,
            board_ready,
            board_settings,
            board_control,
            board_sample
        ])
        .setup(|app| {
            let shared = app.state::<Arc<Shared>>();
            let b = shared.bounds;
            WebviewWindowBuilder::new(
                app,
                "canvas",
                WebviewUrl::App("index.html?surface=canvas".into()),
            )
            .data_directory(shared.path.parent().unwrap().join("webview"))
            .title("QingToolbox · 屏幕画板")
            .transparent(true)
            .decorations(false)
            .shadow(false)
            .resizable(false)
            .always_on_top(true)
            .skip_taskbar(true)
            .visible(false)
            .build()?
            .set_position(PhysicalPosition::new(b.x, b.y))?;
            app.get_webview_window("canvas")
                .unwrap()
                .set_size(PhysicalSize::new(b.width, b.height))?;
            let monitor = app.primary_monitor()?.ok_or("没有可用显示器")?;
            let scale = monitor.scale_factor();
            let area = monitor.work_area();
            let width = ((area.size.width as f64 / scale) - 32.0).clamp(320.0, 1080.0);
            WebviewWindowBuilder::new(
                app,
                "tools",
                WebviewUrl::App("index.html?surface=tools".into()),
            )
            .data_directory(shared.path.parent().unwrap().join("webview"))
            .title("屏幕画板工具条")
            .inner_size(width, 192.0)
            .transparent(true)
            .decorations(false)
            .shadow(false)
            .resizable(false)
            .always_on_top(true)
            .skip_taskbar(true)
            .visible(false)
            .build()?
            .set_position(PhysicalPosition::new(
                area.position.x + ((area.size.width as f64 - width * scale) / 2.0) as i32,
                area.position.y + 16,
            ))?;
            for label in ["canvas", "tools"] {
                let handle = app.handle().clone();
                app.get_webview_window(label)
                    .unwrap()
                    .on_window_event(move |event| {
                        if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                            api.prevent_close();
                            handle.state::<Arc<Shared>>().lock().visible = false;
                            sync(&handle, false);
                        }
                    });
            }
            let handle = app.handle().clone();
            std::thread::spawn(move || serve(handle));
            Ok(())
        })
        .build(tauri::generate_context!());
    match app {
        Ok(app) => app.run(|app, event| {
            if let tauri::RunEvent::ExitRequested { api, .. } = event {
                if !app
                    .state::<Arc<Shared>>()
                    .shutting_down
                    .load(Ordering::SeqCst)
                {
                    api.prevent_exit();
                }
            }
        }),
        Err(e) => eprintln!("Screen Board native startup failed: {e}"),
    }
}
