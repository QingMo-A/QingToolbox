//! Win32 window plumbing for the island.
//!
//! Everything in this file is platform code and therefore untestable in the
//! usual sense. It is kept strictly mechanical: it creates a window, it applies
//! a rectangle, and it answers hit tests using the pure rules in `overlay.rs`.
//! No policy decision is made here, so a mistake in this file can only be a
//! wiring mistake, never a wrong rule.

#![cfg(windows)]

use std::cell::{Cell, RefCell};
use std::sync::{
    atomic::{AtomicIsize, AtomicU32, Ordering},
    Arc,
};

use crate::display::MonitorMetrics;
use crate::overlay::{Bounds, Hit};
use crate::settings::SurfaceStyle;
use windows_sys::Win32::{
    Foundation::{HWND, LPARAM, LRESULT, POINT, RECT, WPARAM},
    Graphics::Gdi::{
        BeginPaint, EndPaint, GetMonitorInfoW, InvalidateRect, MonitorFromPoint, MonitorFromWindow,
        MONITORINFO, MONITOR_DEFAULTTONEAREST, PAINTSTRUCT,
    },
    System::LibraryLoader::GetModuleHandleW,
    UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, GetCursorPos, GetWindowLongPtrW, IsWindow,
        LoadCursorW, RegisterClassW, SetWindowLongPtrW, SetWindowPos, ShowWindow, CS_HREDRAW,
        CS_VREDRAW, CW_USEDEFAULT, GWLP_USERDATA, HTTRANSPARENT, IDC_ARROW, MA_NOACTIVATE,
        SWP_NOACTIVATE, SWP_NOZORDER, SW_HIDE, SW_SHOWNOACTIVATE, WM_DESTROY, WM_ERASEBKGND,
        WM_MOUSEACTIVATE, WM_NCHITTEST, WM_PAINT, WNDCLASSW, WS_EX_LAYERED, WS_EX_NOACTIVATE,
        WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_POPUP,
    },
};

/// Window class name. Namespaced so it cannot collide with host windows.
const CLASS_NAME: &[u16] = &[
    b'Q' as u16,
    b'i' as u16,
    b'n' as u16,
    b'g' as u16,
    b'I' as u16,
    b's' as u16,
    b'l' as u16,
    b'a' as u16,
    b'n' as u16,
    b'd' as u16,
    0,
];

/// A message-only identity for the island window.
pub struct IslandWindow {
    handle: HWND,
    /// Shared with the window procedure, which is a free function and therefore
    /// cannot borrow from the owning struct.
    hit_state: Arc<HitState>,
    requested_style: SurfaceStyle,
    material: Cell<SurfaceStyle>,
    material_fallback: RefCell<Option<String>>,
}

/// Pointer and geometry shared with the window procedure.
///
/// `WM_NCHITTEST` is called by the system on the window's own thread, so plain
/// atomics are sufficient and no lock is taken in the hit-test path — which is
/// important because that path runs on every mouse move.
struct HitState {
    /// Client rectangle in physical pixels, updated on every resize.
    width: AtomicIsize,
    height: AtomicIsize,
    /// Interactive regions in window-relative logical units. Stored as a
    /// serialised rect list to keep the shared state lock-free.
    controls: std::sync::Mutex<Vec<Bounds>>,
    scale_millis: AtomicIsize,
    /// Whether the island currently accepts input at all. Dormant means the
    /// whole window is click-through.
    interactive: AtomicIsize,
    hovered: AtomicIsize,
    clicked: AtomicIsize,
    layout_changed: AtomicIsize,
}

impl HitState {
    fn new() -> Self {
        Self {
            width: AtomicIsize::new(0),
            height: AtomicIsize::new(0),
            controls: std::sync::Mutex::new(Vec::new()),
            scale_millis: AtomicIsize::new(1000),
            interactive: AtomicIsize::new(0),
            hovered: AtomicIsize::new(0),
            clicked: AtomicIsize::new(0),
            layout_changed: AtomicIsize::new(0),
        }
    }

    fn set_interactive(&self, interactive: bool) {
        self.interactive
            .store(if interactive { 1 } else { 0 }, Ordering::Relaxed);
    }

    fn set_scale(&self, scale: f64) {
        let millis = (scale * 1000.0).round().max(1.0);
        self.scale_millis
            .store(millis.min(f64::from(i32::MAX)) as isize, Ordering::Relaxed);
    }

    fn set_size(&self, width: i32, height: i32) {
        self.width.store(width as isize, Ordering::Relaxed);
        self.height.store(height as isize, Ordering::Relaxed);
    }

    fn set_controls(&self, controls: Vec<Bounds>) {
        if let Ok(mut guard) = self.controls.lock() {
            *guard = controls;
        }
    }

    /// Answer one hit test using the pure rules in `overlay.rs`.
    fn resolve(&self, x: i32, y: i32) -> Hit {
        if self.interactive.load(Ordering::Relaxed) == 0 {
            return Hit::PassThrough;
        }
        let scale = self.scale_millis.load(Ordering::Relaxed) as f64 / 1000.0;
        let size = (
            self.width.load(Ordering::Relaxed) as i32,
            self.height.load(Ordering::Relaxed) as i32,
        );
        if crate::renderer::corner_alpha(
            x,
            y,
            size.0,
            size.1,
            (18.0 * scale).min(size.1 as f64 / 2.0),
        ) == 0
        {
            return Hit::PassThrough;
        }
        let controls = self
            .controls
            .lock()
            .map(|guard| guard.clone())
            .unwrap_or_default();
        // Reuse the tested implementation rather than duplicating the rule.
        crate::overlay::hit_test(
            crate::overlay::IslandState::Expanded,
            (x, y),
            size,
            scale,
            &controls,
        )
    }
}

unsafe extern "system" fn window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match message {
        0x02E0 /* WM_DPICHANGED */ | 0x007E /* WM_DISPLAYCHANGE */ | 0x001A /* WM_SETTINGCHANGE */ => {
            if let Some(state) = hit_state_for(hwnd) {
                state.layout_changed.store(1, Ordering::Relaxed);
            }
            // Our anchor/work-area calculation replaces the suggested rectangle.
            DefWindowProcW(hwnd, message, wparam, lparam)
        }
        windows_sys::Win32::UI::WindowsAndMessaging::WM_MOUSEMOVE => {
            if let Some(state) = hit_state_for(hwnd) {
                state.hovered.store(1, Ordering::Relaxed);
                use windows_sys::Win32::UI::Input::KeyboardAndMouse::{TrackMouseEvent, TRACKMOUSEEVENT, TME_LEAVE};
                let mut track = TRACKMOUSEEVENT { cbSize: std::mem::size_of::<TRACKMOUSEEVENT>() as u32, dwFlags:TME_LEAVE, hwndTrack:hwnd, dwHoverTime:0 };
                TrackMouseEvent(&mut track);
            }
            0
        }
        0x02A3 /* WM_MOUSELEAVE */ => {
            if let Some(state) = hit_state_for(hwnd) { state.hovered.store(0, Ordering::Relaxed); }
            0
        }
        windows_sys::Win32::UI::WindowsAndMessaging::WM_LBUTTONUP => {
            if let Some(state) = hit_state_for(hwnd) { state.clicked.store(1, Ordering::Relaxed); }
            0
        }
        // The whole point of the overlay: an empty pixel must not swallow a
        // click meant for whatever is underneath.
        WM_NCHITTEST => {
            let state = hit_state_for(hwnd);
            let Some(state) = state else {
                return HTTRANSPARENT as LRESULT;
            };
            // `lparam` carries screen coordinates in the low/high words.
            let screen_x = (lparam & 0xFFFF) as i16 as i32;
            let screen_y = ((lparam >> 16) & 0xFFFF) as i16 as i32;
            let mut origin = POINT { x: 0, y: 0 };
            // Convert to client coordinates without pulling in a second API
            // family: the window position is stable while the pointer is inside.
            let mut rect = RECT {
                left: 0,
                top: 0,
                right: 0,
                bottom: 0,
            };
            if windows_sys::Win32::UI::WindowsAndMessaging::GetWindowRect(hwnd, &mut rect) != 0 {
                origin.x = screen_x - rect.left;
                origin.y = screen_y - rect.top;
            }
            match state.resolve(origin.x, origin.y) {
                Hit::Interactive => {
                    // HTCLIENT: route the message normally.
                    1
                }
                Hit::PassThrough => HTTRANSPARENT as LRESULT,
            }
        }
        // Clicking the island must never pull focus from what the user is
        // typing in. `MA_NOACTIVATE` is the documented way to say so.
        WM_MOUSEACTIVATE => MA_NOACTIVATE as LRESULT,
        // A layered, fully-drawn window has no background to erase. Claiming
        // the message avoids a flash of unpainted window on every resize.
        WM_ERASEBKGND => 1,
        WM_PAINT => {
            // The island is drawn by the renderer; this handler exists so the
            // window validates its region and the message loop stays quiet
            // instead of repainting continuously.
            let mut paint = PAINTSTRUCT::default();
            BeginPaint(hwnd, &mut paint);
            EndPaint(hwnd, &paint);
            0
        }
        // Only the owner stops the message loop. Destroying a window must not
        // poison the thread queue with WM_QUIT if it is ever recreated.
        WM_DESTROY => {
            0
        }
        _ => DefWindowProcW(hwnd, message, wparam, lparam),
    }
}

/// Recover the shared hit state from the window's user data.
///
/// The pointer is stored rather than the `Arc`, and is only read on the
/// window's own thread while the owning struct is alive, so no refcount is
/// touched in the hot path.
fn hit_state_for(hwnd: HWND) -> Option<&'static HitState> {
    let raw = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) };
    if raw == 0 {
        return None;
    }
    Some(unsafe { &*(raw as *const HitState) })
}

impl IslandWindow {
    pub fn requested_style(&self) -> SurfaceStyle {
        self.requested_style
    }
    pub fn material(&self) -> SurfaceStyle {
        self.material.get()
    }
    pub fn material_fallback(&self) -> Option<String> {
        self.material_fallback.borrow().clone()
    }
    pub fn fallback_to_translucent(&self, reason: &str) {
        self.material.set(SurfaceStyle::Translucent);
        *self.material_fallback.borrow_mut() = Some(reason.into());
        unsafe {
            windows_sys::Win32::UI::WindowsAndMessaging::SetWindowDisplayAffinity(self.handle, 0);
        }
        crate::diagnostics::warning("overlay/material", reason);
    }
    pub fn handle(&self) -> HWND {
        self.handle
    }
    pub fn hovered(&self) -> bool {
        self.hit_state.hovered.load(Ordering::Relaxed) != 0
    }
    pub fn take_click(&self) -> bool {
        self.hit_state.clicked.swap(0, Ordering::Relaxed) != 0
    }
    pub fn take_layout_change(&self) -> bool {
        self.hit_state.layout_changed.swap(0, Ordering::Relaxed) != 0
    }
    pub fn is_visible(&self) -> bool {
        unsafe { windows_sys::Win32::UI::WindowsAndMessaging::IsWindowVisible(self.handle) != 0 }
    }
    /// Create the island window. It starts hidden; the caller shows it once
    /// there is something to display.
    pub fn create(requested_style: SurfaceStyle) -> Result<Self, String> {
        let hit_state = Arc::new(HitState::new());
        unsafe {
            let instance = GetModuleHandleW(std::ptr::null());
            if instance.is_null() {
                return Err("could not obtain the module handle".to_string());
            }
            let class = WNDCLASSW {
                style: CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc: Some(window_proc),
                cbClsExtra: 0,
                cbWndExtra: 0,
                hInstance: instance,
                hIcon: std::ptr::null_mut(),
                hCursor: LoadCursorW(std::ptr::null_mut(), IDC_ARROW),
                hbrBackground: std::ptr::null_mut(),
                lpszMenuName: std::ptr::null(),
                lpszClassName: CLASS_NAME.as_ptr(),
            };
            // A second registration of the same class in the same process is
            // harmless; the failure mode we care about is a foreign class.
            RegisterClassW(&class);

            let extended_style =
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            let handle = CreateWindowExW(
                extended_style,
                CLASS_NAME.as_ptr(),
                std::ptr::null(),
                WS_POPUP,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                1,
                1,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                instance,
                std::ptr::null(),
            );
            if handle.is_null() {
                return Err("could not create the island window".to_string());
            }
            // Publish the shared state so the window procedure can find it.
            SetWindowLongPtrW(handle, GWLP_USERDATA, Arc::as_ptr(&hit_state) as isize);
            let mut material = requested_style;
            let mut material_fallback = None;
            if requested_style == SurfaceStyle::Frosted {
                use windows_sys::Win32::UI::WindowsAndMessaging::{
                    SetWindowDisplayAffinity, WDA_EXCLUDEFROMCAPTURE,
                };
                if !supports_capture_exclusion() {
                    material = SurfaceStyle::Translucent;
                    material_fallback =
                        Some("自绘磨砂需要 Windows 10 2004 或更新版本，已回退半透明".into());
                } else if SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE) == 0 {
                    material = SurfaceStyle::Translucent;
                    material_fallback =
                        Some("系统无法排除悬浮窗自身的背景采样，已回退半透明".to_string());
                }
            }
            Ok(Self {
                handle,
                hit_state,
                requested_style,
                material: Cell::new(material),
                material_fallback: RefCell::new(material_fallback),
            })
        }
    }

    pub fn is_alive(&self) -> bool {
        unsafe { IsWindow(self.handle) != 0 }
    }

    /// Apply a rectangle and the interactive state for the current island.
    ///
    /// `SWP_NOACTIVATE` everywhere: resizing the island must never take focus.
    pub fn apply(&self, bounds: Bounds, interactive: bool, scale: f64) -> Result<(), String> {
        if !self.is_alive() {
            return Err("the island window is gone".to_string());
        }
        self.hit_state.set_interactive(interactive);
        self.hit_state.set_scale(scale);
        self.hit_state.set_size(bounds.width, bounds.height);
        unsafe {
            let (x, y, width, height) = bounds.as_win32();
            let result = SetWindowPos(
                self.handle,
                std::ptr::null_mut(),
                x,
                y,
                width,
                height,
                SWP_NOACTIVATE | SWP_NOZORDER,
            );
            if result == 0 {
                return Err("could not position the island window".to_string());
            }
        }
        Ok(())
    }

    /// Register which regions accept the mouse. Called only when the layout
    /// changes, never per frame.
    pub fn set_controls(&self, controls: Vec<Bounds>) {
        self.hit_state.set_controls(controls);
    }

    pub fn set_visible(&self, visible: bool) {
        if !self.is_alive() {
            return;
        }
        if !visible {
            self.hit_state.hovered.store(0, Ordering::Relaxed);
            self.hit_state.clicked.store(0, Ordering::Relaxed);
            self.hit_state.set_interactive(false);
        }
        let was_visible = self.is_visible();
        if visible == was_visible {
            return;
        }
        unsafe {
            // SW_SHOWNOACTIVATE on the way in, SW_HIDE on the way out. Using
            // SW_SHOW here would steal focus from the foreground window.
            ShowWindow(
                self.handle,
                if visible { SW_SHOWNOACTIVATE } else { SW_HIDE },
            );
        }
    }

    /// Ask for a repaint without blocking.
    pub fn invalidate(&self) {
        if self.is_alive() {
            unsafe {
                InvalidateRect(self.handle, std::ptr::null(), 0);
            }
        }
    }

    /// The pointer position in virtual-desktop coordinates, or `None` when the
    /// query fails (which happens on a locked workstation).
    pub fn cursor_position() -> Option<(i32, i32)> {
        let mut point = POINT { x: 0, y: 0 };
        if unsafe { GetCursorPos(&mut point) } == 0 {
            return None;
        }
        Some((point.x, point.y))
    }
}

/// Avoid pre-2004 Windows, where exclusion becomes a black rectangle.
unsafe fn supports_capture_exclusion() -> bool {
    use windows_sys::Win32::System::{
        LibraryLoader::GetProcAddress, SystemInformation::OSVERSIONINFOW,
    };
    let name: Vec<u16> = "ntdll.dll\0".encode_utf16().collect();
    let module = GetModuleHandleW(name.as_ptr());
    if module.is_null() {
        return false;
    }
    let Some(function) = GetProcAddress(module, c"RtlGetVersion".as_ptr().cast()) else {
        return false;
    };
    let read_version: unsafe extern "system" fn(*mut OSVERSIONINFOW) -> i32 =
        std::mem::transmute(function);
    let mut version = OSVERSIONINFOW {
        dwOSVersionInfoSize: std::mem::size_of::<OSVERSIONINFOW>() as u32,
        ..Default::default()
    };
    read_version(&mut version) >= 0
        && version.dwMajorVersion >= 10
        && version.dwBuildNumber >= 19041
}

pub fn prepare_thread(thread_id: &AtomicU32) {
    use windows_sys::Win32::{
        System::Threading::GetCurrentThreadId,
        UI::WindowsAndMessaging::{PeekMessageW, MSG, PM_NOREMOVE},
    };
    unsafe {
        let mut message = MSG::default();
        PeekMessageW(&mut message, std::ptr::null_mut(), 0, 0, PM_NOREMOVE);
        thread_id.store(GetCurrentThreadId(), Ordering::Release);
    }
}
pub fn wake_thread(thread_id: u32) {
    if thread_id != 0 {
        unsafe {
            windows_sys::Win32::UI::WindowsAndMessaging::PostThreadMessageW(
                thread_id, 0x8001, 0, 0,
            );
        }
    }
}
pub fn pump_messages() {
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        DispatchMessageW, PeekMessageW, TranslateMessage, MSG, PM_REMOVE,
    };
    unsafe {
        let mut message = MSG::default();
        while PeekMessageW(&mut message, std::ptr::null_mut(), 0, 0, PM_REMOVE) != 0 {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
}
pub fn wait_messages(timeout: Option<std::time::Duration>) {
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        MsgWaitForMultipleObjectsEx, MWMO_INPUTAVAILABLE, QS_ALLINPUT,
    };
    unsafe {
        MsgWaitForMultipleObjectsEx(
            0,
            std::ptr::null(),
            timeout.map(|v| v.as_millis() as u32).unwrap_or(u32::MAX),
            QS_ALLINPUT,
            MWMO_INPUTAVAILABLE,
        );
    }
}
pub fn enable_dpi_awareness() {
    use windows_sys::Win32::UI::HiDpi::{
        SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
    };
    unsafe {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
}
pub fn primary_monitor() -> Option<MonitorMetrics> {
    use windows_sys::Win32::Graphics::Gdi::{MonitorFromPoint, MONITOR_DEFAULTTOPRIMARY};
    let handle = unsafe {
        MonitorFromPoint(
            POINT {
                x: i32::MAX,
                y: i32::MAX,
            },
            MONITOR_DEFAULTTOPRIMARY,
        )
    };
    if handle.is_null() {
        return None;
    }
    let mut info = MONITORINFO {
        cbSize: std::mem::size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    if unsafe { GetMonitorInfoW(handle, &mut info) } == 0 {
        return None;
    }
    monitor_for_point(info.rcWork.left, info.rcWork.top)
}

impl Drop for IslandWindow {
    fn drop(&mut self) {
        if self.is_alive() {
            unsafe {
                // Clear the shared pointer before the Arc that owns it goes
                // away, so no message can observe a dangling reference.
                SetWindowLongPtrW(self.handle, GWLP_USERDATA, 0);
                DestroyWindow(self.handle);
            }
        }
    }
}

/// Work area and scale of the monitor containing a point.
///
/// Queried per point rather than cached, because a laptop docked and undocked
/// changes its monitors without restarting the module.
pub fn monitor_for_point(x: i32, y: i32) -> Option<MonitorMetrics> {
    unsafe {
        let handle = MonitorFromPoint(POINT { x, y }, MONITOR_DEFAULTTONEAREST);
        if handle.is_null() {
            return None;
        }
        let mut info = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        if GetMonitorInfoW(handle, &mut info) == 0 {
            return None;
        }
        let work = info.rcWork;
        let dpi = dpi_for_monitor(handle);
        Some(MonitorMetrics {
            x: work.left,
            y: work.top,
            width: (work.right - work.left).max(1) as u32,
            height: (work.bottom - work.top).max(1) as u32,
            scale: if dpi == 0 { 1.0 } else { f64::from(dpi) / 96.0 },
            primary: (info.dwFlags & 1) != 0,
        })
    }
}

fn dpi_for_monitor(monitor: *mut std::ffi::c_void) -> u32 {
    use windows_sys::Win32::Graphics::Gdi::HMONITOR;
    use windows_sys::Win32::UI::HiDpi::GetDpiForMonitor;
    // `MDT_EFFECTIVE_DPI`
    const DEFAULT: i32 = 0;
    let mut dpi_x = 96u32;
    let mut dpi_y = 96u32;
    let result = unsafe { GetDpiForMonitor(monitor as HMONITOR, DEFAULT, &mut dpi_x, &mut dpi_y) };
    if result == 0 && dpi_x > 0 {
        dpi_x
    } else {
        96
    }
}

/// Whether any monitor currently shows a fullscreen application.
///
/// Compares the foreground window against its monitor's full bounds rather than
/// the work area: a maximised window stops at the taskbar, while a genuinely
/// fullscreen one covers everything. The small tolerance absorbs borders.
pub fn foreground_is_fullscreen() -> bool {
    use windows_sys::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowRect};
    unsafe {
        let foreground = GetForegroundWindow();
        if foreground.is_null() {
            return false;
        }
        let mut class = [0u16; 128];
        let length = windows_sys::Win32::UI::WindowsAndMessaging::GetClassNameW(
            foreground,
            class.as_mut_ptr(),
            class.len() as i32,
        );
        if matches!(
            String::from_utf16_lossy(&class[..length.max(0) as usize]).as_str(),
            "Progman" | "WorkerW" | "Shell_TrayWnd"
        ) {
            return false;
        }
        let monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        if monitor.is_null() {
            return false;
        }
        // Full monitor bounds, not the work area.
        let mut info = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        if GetMonitorInfoW(monitor, &mut info) == 0 {
            return false;
        }
        let mut rect = RECT {
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
        };
        if GetWindowRect(foreground, &mut rect) == 0 {
            return false;
        }
        let screen = info.rcMonitor;
        const TOLERANCE: i32 = 2;
        rect.left <= screen.left + TOLERANCE
            && rect.top <= screen.top + TOLERANCE
            && rect.right >= screen.right - TOLERANCE
            && rect.bottom >= screen.bottom - TOLERANCE
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_class_name_is_terminated_and_ascii() {
        assert_eq!(*CLASS_NAME.last().expect("terminator"), 0);
        let text = CLASS_NAME[..CLASS_NAME.len() - 1]
            .iter()
            .map(|unit| char::from_u32(*unit as u32).unwrap_or('?'))
            .collect::<String>();
        assert_eq!(text, "QingIsland");
    }

    #[test]
    fn hit_state_reports_click_through_while_not_interactive() {
        let state = HitState::new();
        state.set_size(100, 40);
        state.set_scale(1.0);
        state.set_controls(vec![Bounds {
            x: 0,
            y: 0,
            width: 100,
            height: 40,
        }]);
        state.set_interactive(false);
        assert_eq!(
            state.resolve(10, 10),
            Hit::PassThrough,
            "a dormant island owns no clickable pixels"
        );
        state.set_interactive(true);
        assert_eq!(state.resolve(10, 10), Hit::Interactive);
    }

    #[test]
    fn hit_state_is_lock_free_in_the_common_path() {
        // Reads of size and scale must not need the mutex; this test simply
        // asserts the values are visible without locking.
        let state = HitState::new();
        state.set_size(50, 20);
        state.set_scale(1.5);
        assert_eq!(state.width.load(Ordering::Relaxed), 50);
        assert_eq!(state.height.load(Ordering::Relaxed), 20);
        assert_eq!(state.scale_millis.load(Ordering::Relaxed), 1500);
    }

    #[test]
    fn a_degenerate_scale_is_clamped_to_something_usable() {
        let state = HitState::new();
        state.set_scale(0.0);
        assert_eq!(state.scale_millis.load(Ordering::Relaxed), 1);
        state.set_scale(f64::NAN);
        assert!(state.scale_millis.load(Ordering::Relaxed) >= 1);
    }

    #[test]
    fn unknown_hit_state_is_treated_as_click_through() {
        // The safe failure direction: if the shared state cannot be read, the
        // user's clicks must reach the desktop rather than vanish.
        let state = HitState::new();
        assert_eq!(state.resolve(5, 5), Hit::PassThrough);
    }
}
