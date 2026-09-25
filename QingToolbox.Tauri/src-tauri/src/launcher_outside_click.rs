//! Non-consuming outside-click dismissal, installed only while Launcher is
//! visible. It never captures the pointer or reads/stores keyboard input.
#[derive(Default)]
struct Gesture {
    origin: Option<(i32, i32)>,
    moved: bool,
}

impl Gesture {
    fn down(&mut self, point: (i32, i32), outside: bool) {
        self.origin = outside.then_some(point);
        self.moved = false;
    }
    fn motion(&mut self, point: (i32, i32), threshold: i32) {
        if let Some(origin) = self.origin {
            self.moved |=
                (point.0 - origin.0).abs() > threshold || (point.1 - origin.1).abs() > threshold;
        }
    }
    fn up(&mut self, point: (i32, i32), outside: bool, threshold: i32) -> bool {
        self.motion(point, threshold);
        let click = self.origin.is_some() && !self.moved && outside;
        self.origin = None;
        click
    }
}

#[cfg(windows)]
mod native {
    use super::Gesture;
    use std::{
        cell::RefCell,
        sync::{
            atomic::{AtomicBool, AtomicU32, Ordering},
            Arc,
        },
    };
    use tauri::{Manager, Runtime, WebviewWindow};
    use windows_sys::Win32::{
        Foundation::{LPARAM, LRESULT, POINT, RECT, WPARAM},
        System::{LibraryLoader::GetModuleHandleW, Threading::GetCurrentThreadId},
        UI::WindowsAndMessaging::*,
    };

    type MouseHandler = Box<dyn FnMut(u32, POINT)>;
    thread_local! { static HANDLER: RefCell<Option<MouseHandler>> = RefCell::new(None); }

    pub struct Observer {
        stop: Arc<AtomicBool>,
        thread: Arc<AtomicU32>,
    }

    impl Drop for Observer {
        fn drop(&mut self) {
            self.stop.store(true, Ordering::Release);
            let thread = self.thread.load(Ordering::Acquire);
            if thread != 0 {
                unsafe {
                    PostThreadMessageW(thread, WM_QUIT, 0, 0);
                }
            }
        }
    }

    unsafe extern "system" fn mouse_hook(code: i32, message: WPARAM, data: LPARAM) -> LRESULT {
        if code >= 0 && data != 0 {
            let point = (*(data as *const MSLLHOOKSTRUCT)).pt;
            HANDLER.with(|handler| {
                if let Some(handler) = handler.borrow_mut().as_mut() {
                    handler(message as u32, point);
                }
            });
        }
        // Desktop/Explorer must receive the original input, including the
        // first press that starts dragging a shortcut into Launcher.
        CallNextHookEx(std::ptr::null_mut(), code, message, data)
    }

    pub fn start<R: Runtime>(window: &WebviewWindow<R>) -> Option<Observer> {
        let hwnd = window.hwnd().ok()?.0 as usize;
        let threshold = (6.0 * window.scale_factor().unwrap_or(1.0)).round() as i32;
        let observer = Observer {
            stop: Arc::new(AtomicBool::new(false)),
            thread: Arc::new(AtomicU32::new(0)),
        };
        let stop = observer.stop.clone();
        let thread = observer.thread.clone();
        let window = window.clone();
        std::thread::spawn(move || unsafe {
            // Create the queue before publishing its ID, so Drop cannot lose
            // WM_QUIT while installation is still starting.
            let mut message = std::mem::zeroed::<MSG>();
            PeekMessageW(&mut message, std::ptr::null_mut(), 0, 0, PM_NOREMOVE);
            thread.store(GetCurrentThreadId(), Ordering::Release);
            if stop.load(Ordering::Acquire) {
                return;
            }
            let callback_stop = stop.clone();
            let mut gesture = Gesture::default();
            HANDLER.with(|handler| {
                *handler.borrow_mut() = Some(Box::new(move |kind, point| {
                    if callback_stop.load(Ordering::Acquire) || IsWindowVisible(hwnd as _) == 0 {
                        return;
                    }
                    let point = (point.x, point.y);
                    if kind == WM_MOUSEMOVE {
                        gesture.motion(point, threshold);
                        return;
                    }
                    if !matches!(kind, WM_LBUTTONDOWN | WM_LBUTTONUP) {
                        return;
                    }
                    let mut rect = RECT::default();
                    if GetWindowRect(hwnd as _, &mut rect) == 0 {
                        return;
                    }
                    let outside = point.0 < rect.left
                        || point.0 >= rect.right
                        || point.1 < rect.top
                        || point.1 >= rect.bottom;
                    if kind == WM_LBUTTONDOWN {
                        gesture.down(point, outside);
                        return;
                    }
                    if !gesture.up(point, outside, threshold) {
                        return;
                    }
                    let target = window.clone();
                    let still_current = callback_stop.clone();
                    // Never run Tauri or module IPC synchronously in the hook.
                    let _ = window.app_handle().run_on_main_thread(move || {
                        if !still_current.load(Ordering::Acquire)
                            && target.is_visible().unwrap_or(false)
                            && crate::launcher_overlay::can_dismiss(&target)
                        {
                            let _ = crate::launcher_overlay::hide(&target);
                        }
                    });
                }))
            });
            let hook = SetWindowsHookExW(
                WH_MOUSE_LL,
                Some(mouse_hook),
                GetModuleHandleW(std::ptr::null()),
                0,
            );
            if hook.is_null() {
                eprintln!(
                    "Launcher outside-click observer unavailable: {}",
                    std::io::Error::last_os_error()
                );
            } else {
                while !stop.load(Ordering::Acquire)
                    && GetMessageW(&mut message, std::ptr::null_mut(), 0, 0) > 0
                {
                    TranslateMessage(&message);
                    DispatchMessageW(&message);
                }
                UnhookWindowsHookEx(hook);
            }
            HANDLER.with(|handler| *handler.borrow_mut() = None);
        });
        Some(observer)
    }

    pub fn stop<R: Runtime>(app: &tauri::AppHandle<R>) {
        if let Some(state) = app.try_state::<crate::HostState>() {
            if let Ok(mut observer) = state.launcher_outside_click.lock() {
                observer.take();
            }
        }
    }
}

#[cfg(windows)]
pub use native::{start, stop, Observer};
#[cfg(not(windows))]
pub struct Observer;
#[cfg(not(windows))]
pub fn start<R: tauri::Runtime>(_: &tauri::WebviewWindow<R>) -> Option<Observer> {
    None
}
#[cfg(not(windows))]
pub fn stop<R: tauri::Runtime>(_: &tauri::AppHandle<R>) {}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn only_complete_outside_clicks_dismiss() {
        let mut gesture = Gesture::default();
        assert!(!gesture.up((0, 0), true, 6)); // drag started before showing
        gesture.down((0, 0), true);
        assert!(gesture.up((1, 2), true, 6));
        gesture.down((0, 0), false);
        assert!(!gesture.up((1, 2), true, 6));
        gesture.down((0, 0), true);
        assert!(!gesture.up((1, 2), false, 6));
    }
    #[test]
    fn outside_drags_and_returning_to_origin_never_dismiss() {
        let mut gesture = Gesture::default();
        gesture.down((0, 0), true);
        gesture.motion((100, 100), 6);
        assert!(!gesture.up((0, 0), true, 6));
        gesture.down((0, 0), true);
        assert!(!gesture.up((100, 100), false, 6));
    }
}
