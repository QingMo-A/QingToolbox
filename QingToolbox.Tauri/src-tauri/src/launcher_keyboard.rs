//! Window-scoped recording. No global keyboard hook or keystroke storage.
use tauri::{Emitter, Manager, Runtime, WebviewWindow};

#[derive(Default)]
pub struct Recording {
    pub ready: bool,
    pub active: bool,
    pub session: u64,
    previous: Option<String>,
    suppress_alt_release: bool,
}

pub fn stop<R: Runtime>(app: &tauri::AppHandle<R>) -> Result<(), String> {
    let state = app.state::<crate::HostState>();
    let (previous, session) = {
        let mut recording = state
            .launcher_keyboard
            .lock()
            .map_err(|_| "录入状态不可用")?;
        if !recording.active {
            return Ok(());
        }
        recording.active = false;
        (recording.previous.take(), recording.session)
    };
    let result = previous
        .map(|hotkey| {
            crate::register_module_hotkey_binding(
                app,
                &state,
                crate::launcher_overlay::MODULE_ID,
                &hotkey,
            )
            .map_err(|error| error.message)
        })
        .unwrap_or(Ok(()));
    let _ = app.emit_to(
        crate::module_window_label(crate::launcher_overlay::MODULE_ID),
        "launcher:recording-stopped",
        session,
    );
    result
}

pub fn start<R: Runtime>(window: &WebviewWindow<R>) -> Result<u64, String> {
    if !window.is_visible().unwrap_or(false) || !window.is_focused().unwrap_or(false) {
        return Err("请先切回启动台再录入快捷键。".into());
    }
    let app = window.app_handle();
    stop(app)?;
    let state = app.state::<crate::HostState>();
    if !state
        .launcher_keyboard
        .lock()
        .map_err(|_| "录入状态不可用")?
        .ready
    {
        return Err("快捷键录入尚未就绪，请重新打开启动台。".into());
    }
    let previous = state
        .module_hotkeys
        .lock()
        .map_err(|_| "快捷键状态不可用")?
        .get(crate::launcher_overlay::MODULE_ID)
        .cloned();
    crate::clear_module_hotkey_binding(app, &state, crate::launcher_overlay::MODULE_ID)
        .map_err(|error| error.message)?;
    let mut recording = state
        .launcher_keyboard
        .lock()
        .map_err(|_| "录入状态不可用")?;
    recording.session = recording.session.wrapping_add(1);
    recording.previous = previous;
    recording.active = true;
    Ok(recording.session)
}

#[cfg(windows)]
unsafe extern "system" fn overlay_window_proc(
    hwnd: windows_sys::Win32::Foundation::HWND,
    message: u32,
    wparam: usize,
    lparam: isize,
    id: usize,
    _data: usize,
) -> isize {
    use windows_sys::Win32::UI::{
        Shell::{DefSubclassProc, RemoveWindowSubclass},
        WindowsAndMessaging::{SC_KEYMENU, WM_NCDESTROY, WM_SYSCOMMAND},
    };
    // The launcher is a frameless overlay and has no native system menu.
    // WebView2 handling alone cannot stop SC_KEYMENU queued by its parent
    // HWND after Alt+Space. Keep this scoped to the launcher's window only.
    if message == WM_SYSCOMMAND && wparam & 0xfff0 == SC_KEYMENU as usize {
        return 0;
    }
    if message == WM_NCDESTROY {
        RemoveWindowSubclass(hwnd, Some(overlay_window_proc), id);
    }
    DefSubclassProc(hwnd, message, wparam, lparam)
}

#[cfg(windows)]
pub fn install<R: Runtime>(window: &WebviewWindow<R>) -> tauri::Result<()> {
    use webview2_com::{AcceleratorKeyPressedEventHandler, Microsoft::Web::WebView2::Win32::*};
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::GetKeyState;
    let app = window.app_handle().clone();
    let ready_app = app.clone();
    let label = window.label().to_string();
    let hwnd = window.hwnd()?.0 as usize;
    window.with_webview(move |webview| unsafe {
        if windows_sys::Win32::UI::Shell::SetWindowSubclass(hwnd as _, Some(overlay_window_proc), 0x514c4155, 0) == 0 {
            eprintln!("Launcher system-menu suppression could not be installed");
            return;
        }
        let handler = AcceleratorKeyPressedEventHandler::create(Box::new(move |_, args| {
            let Some(args) = args else { return Ok(()); };
            let state = app.state::<crate::HostState>();
            let Ok(mut recording) = state.launcher_keyboard.lock() else { return Ok(()); };
            let mut key = 0;
            let mut kind = COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN;
            args.VirtualKey(&mut key)?;
            args.KeyEventKind(&mut kind)?;
            let down = kind == COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN || kind == COREWEBVIEW2_KEY_EVENT_KIND_SYSTEM_KEY_DOWN;
            let alt = matches!(key, 0x12 | 0xa4 | 0xa5);
            if recording.suppress_alt_release && alt && !down {
                args.SetHandled(true)?;
                recording.suppress_alt_release = false;
            }
            if !recording.active { return Ok(()); }
            // Handled must be set before any emit/other cross-process work.
            args.SetHandled(true)?;
            let pressed = |vk| GetKeyState(vk) < 0;
            let ctrl = pressed(0x11);
            let alt_down = pressed(0x12);
            if alt_down || alt { recording.suppress_alt_release = true; }
            if !down || matches!(key, 0x10..=0x12 | 0x5b | 0x5c | 0xa0..=0xa5) { return Ok(()); }
            let mut status = COREWEBVIEW2_PHYSICAL_KEY_STATUS::default();
            args.PhysicalKeyStatus(&mut status)?;
            if status.WasKeyDown.as_bool() { return Ok(()); }
            let payload = serde_json::json!({ "session": recording.session, "virtualKey": key,
                "ctrl": ctrl, "alt": alt_down, "shift": pressed(0x10), "win": pressed(0x5b) || pressed(0x5c) });
            drop(recording);
            // WebView2's accelerator callback is synchronous. Queue the JS
            // notification off this callback to avoid a re-entrant browser
            // IPC call while its input transaction is still on the stack.
            let notify_app = app.clone();
            let notify_label = label.clone();
            tauri::async_runtime::spawn(async move {
                let _ = notify_app.emit_to(&notify_label, "launcher:recorded-key", payload);
            });
            Ok(())
        }));
        let mut token = 0;
        if let Err(error) = webview.controller().add_AcceleratorKeyPressed(&handler, &mut token) {
            eprintln!("Launcher native key recorder unavailable: {error}");
        } else if let Ok(mut recording) = ready_app.state::<crate::HostState>().launcher_keyboard.lock() {
            recording.ready = true;
        }
    })
}

#[cfg(not(windows))]
pub fn install<R: Runtime>(_window: &WebviewWindow<R>) -> tauri::Result<()> {
    Ok(())
}
