//! A bounded, physical-pixel region picker. The module owns the native window;
//! no screenshot or arbitrary screen API is exposed to the web frontend.
#[cfg(windows)]
pub fn select() -> Result<Option<(i32, i32, i32, i32)>, String> {
    std::thread::spawn(pick)
        .join()
        .map_err(|_| "截图选区线程异常。".to_string())?
}

#[cfg(not(windows))]
pub fn select() -> Result<Option<(i32, i32, i32, i32)>, String> {
    Err("区域截图仅支持 Windows。".to_string())
}

#[cfg(windows)]
fn pick() -> Result<Option<(i32, i32, i32, i32)>, String> {
    use std::{cell::RefCell, ptr};
    use windows_sys::Win32::{
        Foundation::{HWND, LPARAM, LRESULT, RECT, WPARAM},
        Graphics::Gdi::*,
        System::LibraryLoader::GetModuleHandleW,
        UI::{
            HiDpi::*,
            Input::KeyboardAndMouse::{ReleaseCapture, SetCapture},
            WindowsAndMessaging::*,
        },
    };
    #[derive(Default)]
    struct Selection {
        start: Option<(i32, i32)>,
        end: (i32, i32),
        result: Option<(i32, i32, i32, i32)>,
        done: bool,
    }
    thread_local! { static STATE: RefCell<Selection> = RefCell::new(Selection::default()); }
    unsafe extern "system" fn procedure(hwnd: HWND, msg: u32, w: WPARAM, l: LPARAM) -> LRESULT {
        let point = ((l as u16 as i16) as i32, ((l >> 16) as u16 as i16) as i32);
        match msg {
            WM_LBUTTONDOWN => {
                STATE.with(|s| {
                    let mut s = s.borrow_mut();
                    s.start = Some(point);
                    s.end = point;
                });
                SetCapture(hwnd);
                0
            }
            WM_MOUSEMOVE => {
                STATE.with(|s| s.borrow_mut().end = point);
                InvalidateRect(hwnd, ptr::null(), 1);
                0
            }
            WM_LBUTTONUP => {
                STATE.with(|s| {
                    let mut s = s.borrow_mut();
                    if let Some(a) = s.start {
                        let width = (a.0 - point.0).abs();
                        let height = (a.1 - point.1).abs();
                        if width >= 2 && height >= 2 {
                            s.result = Some((a.0.min(point.0), a.1.min(point.1), width, height));
                        }
                    }
                });
                ReleaseCapture();
                DestroyWindow(hwnd);
                0
            }
            WM_KEYDOWN if w == 27 => {
                DestroyWindow(hwnd);
                0
            }
            WM_RBUTTONDOWN | WM_CLOSE | WM_TIMER => {
                DestroyWindow(hwnd);
                0
            }
            WM_DESTROY => {
                STATE.with(|s| s.borrow_mut().done = true);
                PostQuitMessage(0);
                0
            }
            WM_PAINT => {
                let mut ps = std::mem::zeroed();
                let dc = BeginPaint(hwnd, &mut ps);
                let mut rect = RECT::default();
                GetClientRect(hwnd, &mut rect);
                FillRect(dc, &rect, GetStockObject(BLACK_BRUSH) as _);
                SetTextColor(dc, 0x00ffffff);
                SetBkMode(dc, TRANSPARENT as i32);
                let text: Vec<u16> = "拖拽选择区域 · Esc / 右键取消".encode_utf16().collect();
                TextOutW(dc, 24, 24, text.as_ptr(), text.len() as i32);
                STATE.with(|s| {
                    let s = s.borrow();
                    if let Some(a) = s.start {
                        let r = RECT {
                            left: a.0.min(s.end.0),
                            top: a.1.min(s.end.1),
                            right: a.0.max(s.end.0),
                            bottom: a.1.max(s.end.1),
                        };
                        FrameRect(dc, &r, GetStockObject(WHITE_BRUSH) as _);
                        let size: Vec<u16> = format!("{} × {}", r.right - r.left, r.bottom - r.top)
                            .encode_utf16()
                            .collect();
                        TextOutW(dc, r.left, r.bottom + 8, size.as_ptr(), size.len() as i32);
                    }
                });
                EndPaint(hwnd, &ps);
                0
            }
            _ => DefWindowProcW(hwnd, msg, w, l),
        }
    }
    unsafe {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        let bounds = super::display_bounds();
        let class: Vec<u16> = "QingScreenPinSelection\0".encode_utf16().collect();
        let instance = GetModuleHandleW(ptr::null());
        let wc = WNDCLASSW {
            lpfnWndProc: Some(procedure),
            hInstance: instance,
            lpszClassName: class.as_ptr(),
            hCursor: LoadCursorW(ptr::null_mut(), IDC_CROSS),
            ..std::mem::zeroed()
        };
        RegisterClassW(&wc);
        let hwnd = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED,
            class.as_ptr(),
            class.as_ptr(),
            WS_POPUP,
            bounds.x,
            bounds.y,
            bounds.width,
            bounds.height,
            ptr::null_mut(),
            ptr::null_mut(),
            instance,
            ptr::null(),
        );
        if hwnd.is_null() {
            return Err("无法打开截图选区。".to_string());
        }
        SetLayeredWindowAttributes(hwnd, 0, 90, LWA_ALPHA);
        if SetTimer(hwnd, 1, 60_000, None) == 0 {
            DestroyWindow(hwnd);
            return Err("无法启动截图计时器。".to_string());
        }
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
        let mut message = std::mem::zeroed();
        while GetMessageW(&mut message, ptr::null_mut(), 0, 0) > 0 {
            TranslateMessage(&message);
            DispatchMessageW(&message);
            if STATE.with(|s| s.borrow().done) {
                break;
            }
        }
        if IsWindow(hwnd) != 0 {
            DestroyWindow(hwnd);
        }
        let selected = STATE
            .with(|s| s.borrow().result)
            .map(|(x, y, w, h)| (x + bounds.x, y + bounds.y, w, h));
        // Let DWM remove the picker before the caller reads screen pixels.
        std::thread::sleep(std::time::Duration::from_millis(120));
        Ok(selected)
    }
}
